using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using DeltaRender;
using Silk.NET.Vulkan;
using VulkanImage = Silk.NET.Vulkan.Image;

namespace DeltaRender.Vulkan;

internal interface IVulkanTextAtlasCommandContext
{
    Vk Api { get; }
    Device Device { get; }
    PhysicalDeviceMemoryProperties MemoryProperties { get; }
    CommandBuffer CommandBuffer { get; }
    bool BeginUploadCommands();
    bool EndUploadCommands();
}

internal sealed unsafe class VulkanTextAtlasService : ITextAtlasDevice, IAsyncDisposable
{
    private const ulong MinimumVulkanBufferSize = 4;

    private readonly IVulkanTextAtlasCommandContext _context;
    private readonly List<VulkanTextAtlasPage> _atlasPages = new();
    private BufferAllocation _atlasStaging;
    private int _atlasStagingAllocationCount;
    private int _atlasSubmissionCount;

    public VulkanTextAtlasService(IVulkanTextAtlasCommandContext context)
    {
        _context = context;
    }

    public TextAtlasUploadStatistics AtlasUploadStatistics => new(_atlasStagingAllocationCount, _atlasSubmissionCount, _atlasStaging.AllocationSize);

    public ITextAtlasPage CreateAtlasPage(in TextAtlasPageDescription description)
    {
        if (!description.IsValid)
        {
            throw new ArgumentException("Atlas page description is invalid.", nameof(description));
        }

        var api = _context.Api;
        var device = _context.Device;
        var image = default(VulkanImage);
        var memory = default(DeviceMemory);
        var view = default(ImageView);
        var sampler = default(Sampler);
        try
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = ToVulkanFormat(description.Format),
                Extent = new Extent3D { Width = description.Width, Height = description.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            Ensure(api.CreateImage(device, imageInfo, null, out image), "CreateImage");

            var requirements = api.GetImageMemoryRequirements(device, image);
            var memoryType = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
            var allocation = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType
            };
            Ensure(api.AllocateMemory(device, allocation, null, out memory), "AllocateImageMemory");
            Ensure(api.BindImageMemory(device, image, memory, 0), "BindImageMemory");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = imageInfo.Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            Ensure(api.CreateImageView(device, viewInfo, null, out view), "CreateImageView");

            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MaxLod = 1,
                BorderColor = BorderColor.FloatTransparentBlack
            };
            Ensure(api.CreateSampler(device, samplerInfo, null, out sampler), "CreateSampler");

            var result = new VulkanTextAtlasPage(this, description, image, memory, requirements.Size, view, sampler);
            _atlasPages.Add(result);
            return result;
        }
        catch
        {
            if (sampler.Handle != default)
            {
                api.DestroySampler(device, sampler, null);
            }

            if (view.Handle != default)
            {
                api.DestroyImageView(device, view, null);
            }

            if (image.Handle != default)
            {
                api.DestroyImage(device, image, null);
            }

            if (memory.Handle != default)
            {
                api.FreeMemory(device, memory, null);
            }

            throw;
        }
    }

    public bool UploadAtlasPage(ITextAtlasPage page, ReadOnlySpan<byte> pixels, uint sourceRowPitch)
    {
        if (!TryGetAtlasPage(page, out var target) || sourceRowPitch < target.Description.Width * target.Description.BytesPerPixel)
        {
            return false;
        }

        var required = checked((ulong)sourceRowPitch * target.Description.Height);
        if ((ulong)pixels.Length < required)
        {
            return false;
        }

        var range = new TextAtlasDirtyRange(0, 0, target.Description.Width, target.Description.Height, sourceRowPitch, pixels.ToArray());
        return UploadAtlasDirtyRanges(target, new[] { range });
    }

    public bool UploadAtlasDirtyRanges(ITextAtlasPage page, ReadOnlySpan<TextAtlasDirtyRange> ranges)
    {
        if (!TryGetAtlasPage(page, out var target))
        {
            return false;
        }

        if (ranges.IsEmpty)
        {
            return true;
        }

        var copies = ArrayPool<BufferImageCopy>.Shared.Rent(ranges.Length);
        var stagingBytes = 0UL;
        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var range = ranges[i];
                var rowBytes = checked((ulong)range.Width * target.Description.BytesPerPixel);
                if (!range.IsValid ||
                    (ulong)range.X + range.Width > target.Description.Width ||
                    (ulong)range.Y + range.Height > target.Description.Height ||
                    range.SourceRowPitch < rowBytes ||
                    (ulong)range.Source.Length < (ulong)range.SourceRowPitch * range.Height)
                {
                    return false;
                }

                copies[i] = new BufferImageCopy
                {
                    BufferOffset = stagingBytes,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D { X = (int)range.X, Y = (int)range.Y, Z = 0 },
                    ImageExtent = new Extent3D { Width = range.Width, Height = range.Height, Depth = 1 }
                };
                stagingBytes = checked(stagingBytes + rowBytes * range.Height);
            }

            EnsureAtlasStagingCapacity(stagingBytes);
            FillAtlasStaging(ranges, stagingBytes, target.Description.BytesPerPixel);

            if (!_context.BeginUploadCommands())
            {
                return false;
            }

            var api = _context.Api;
            var commandBuffer = _context.CommandBuffer;
            var preBarrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = target.HasUploaded ? AccessFlags.ShaderReadBit : AccessFlags.None,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = target.HasUploaded ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            api.CmdPipelineBarrier(commandBuffer,
                target.HasUploaded ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                new[] { preBarrier });

            api.CmdCopyBufferToImage(commandBuffer, _atlasStaging.Buffer, target.Image, ImageLayout.TransferDstOptimal, copies.AsSpan(0, ranges.Length));

            var postBarrier = preBarrier;
            postBarrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            postBarrier.DstAccessMask = AccessFlags.ShaderReadBit;
            postBarrier.OldLayout = ImageLayout.TransferDstOptimal;
            postBarrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            api.CmdPipelineBarrier(commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                new[] { postBarrier });

            if (!_context.EndUploadCommands())
            {
                return false;
            }

            target.HasUploaded = true;
            _atlasSubmissionCount++;
            return true;
        }
        finally
        {
            ArrayPool<BufferImageCopy>.Shared.Return(copies, clearArray: true);
        }
    }

    public void Dispose()
    {
        var api = _context.Api;
        var device = _context.Device;
        foreach (var page in _atlasPages.ToArray())
        {
            DestroyAtlasPage(page);
        }

        if (_atlasStaging.Buffer.Handle != default)
        {
            DestroyAllocation(_atlasStaging);
            _atlasStaging = default;
        }

    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal bool TryGetAtlasPage(ITextAtlasPage page, [NotNullWhen(true)] out VulkanTextAtlasPage? result)
    {
        if (page is VulkanTextAtlasPage candidate && ReferenceEquals(candidate.Owner, this) && candidate.Image.Handle != default)
        {
            result = candidate;
            return true;
        }

        result = null;
        return false;
    }

    internal void DestroyAtlasPage(VulkanTextAtlasPage page)
    {
        if (page.Image.Handle == default)
        {
            return;
        }

        var api = _context.Api;
        var device = _context.Device;
        if (page.Sampler.Handle != default)
        {
            api.DestroySampler(device, page.Sampler, null);
        }

        if (page.ImageView.Handle != default)
        {
            api.DestroyImageView(device, page.ImageView, null);
        }

        api.DestroyImage(device, page.Image, null);
        api.FreeMemory(device, page.Memory, null);
        _atlasPages.Remove(page);
        page.MarkDestroyed();
    }

    private void EnsureAtlasStagingCapacity(ulong requiredBytes)
    {
        if (_atlasStaging.Buffer.Handle != default && _atlasStaging.AllocationSize >= requiredBytes)
        {
            return;
        }

        var requested = Math.Max(requiredBytes, MinimumVulkanBufferSize);
        var current = _atlasStaging.AllocationSize;
        while (current != 0 && current < requested)
        {
            current = current > ulong.MaxValue / 2 ? requested : current * 2;
        }

        var replacement = CreateBuffer(Math.Max(requested, current), BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit);
        var previous = _atlasStaging;
        _atlasStaging = replacement;
        _atlasStagingAllocationCount++;
        DestroyAllocation(previous);
    }

    private void FillAtlasStaging(ReadOnlySpan<TextAtlasDirtyRange> ranges, ulong stagingBytes, uint bytesPerPixel)
    {
        var api = _context.Api;
        var device = _context.Device;
        void* mapped = null;
        Ensure(api.MapMemory(device, _atlasStaging.Memory, 0, _atlasStaging.AllocationSize, 0, &mapped), "MapMemory");
        try
        {
            var destination = new Span<byte>(mapped, checked((int)stagingBytes));
            var offset = 0;
            foreach (var range in ranges)
            {
                var rowBytes = checked((int)(range.Width * bytesPerPixel));
                for (var row = 0u; row < range.Height; row++)
                {
                    range.Source.Span.Slice(checked((int)((ulong)row * range.SourceRowPitch)), rowBytes).CopyTo(destination.Slice(offset, rowBytes));
                    offset += rowBytes;
                }
            }

            if (!_atlasStaging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                Flush(_atlasStaging.Memory, _atlasStaging.AllocationSize);
            }
        }
        finally
        {
            api.UnmapMemory(device, _atlasStaging.Memory);
        }
    }

    private BufferAllocation CreateBuffer(ulong requestedSize, BufferUsageFlags usage, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        var api = _context.Api;
        var device = _context.Device;
        var actualSize = Math.Max(requestedSize, MinimumVulkanBufferSize);
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = actualSize,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        Ensure(api.CreateBuffer(device, createInfo, null, out var buffer), "CreateBuffer");
        var requirements = api.GetBufferMemoryRequirements(device, buffer);
        try
        {
            var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, required, preferred);
            var memoryProperties = _context.MemoryProperties.MemoryTypes[(int)memoryTypeIndex];
            var allocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex
            };
            Ensure(api.AllocateMemory(device, allocateInfo, null, out var memory), "AllocateMemory");
            try
            {
                Ensure(api.BindBufferMemory(device, buffer, memory, 0), "BindBufferMemory");
                return new BufferAllocation(buffer, memory, requirements.Size, memoryProperties.PropertyFlags);
            }
            catch
            {
                api.FreeMemory(device, memory, null);
                throw;
            }
        }
        catch
        {
            api.DestroyBuffer(device, buffer, null);
            throw;
        }
    }

    private void DestroyAllocation(BufferAllocation allocation)
    {
        var api = _context.Api;
        var device = _context.Device;
        if (allocation.Buffer.Handle != default)
        {
            api.DestroyBuffer(device, allocation.Buffer, null);
        }

        if (allocation.Memory.Handle != default)
        {
            api.FreeMemory(device, allocation.Memory, null);
        }
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < _context.MemoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
            {
                continue;
            }

            var flags = _context.MemoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if (!flags.HasFlag(required))
            {
                continue;
            }

            if (flags.HasFlag(preferred))
            {
                return i;
            }

            fallback = i;
        }

        if (fallback != uint.MaxValue)
        {
            return fallback;
        }

        throw new InvalidOperationException($"No Vulkan memory type satisfies {required}.");
    }

    private void Flush(DeviceMemory memory, ulong size)
    {
        var api = _context.Api;
        var device = _context.Device;
        Ensure(api.FlushMappedMemoryRanges(device, new[] { new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = memory, Size = size } }), "FlushMappedMemoryRanges");
    }

    private static Format ToVulkanFormat(TextAtlasFormat format) => format switch
    {
        TextAtlasFormat.R8Unorm => Format.R8Unorm,
        TextAtlasFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan {operation} failed: {result}.");
        }
    }
}

public sealed unsafe class VulkanTextAtlasPage : ITextAtlasPage
{
    internal VulkanTextAtlasPage(VulkanTextAtlasService owner, TextAtlasPageDescription description, VulkanImage image,
        DeviceMemory memory, ulong allocationSize, ImageView imageView, Sampler sampler)
    {
        Owner = owner;
        Description = description;
        Image = image;
        Memory = memory;
        AllocationSize = allocationSize;
        ImageView = imageView;
        Sampler = sampler;
    }

    internal VulkanTextAtlasService Owner { get; }
    internal VulkanImage Image { get; private set; }
    internal DeviceMemory Memory { get; private set; }
    internal ulong AllocationSize { get; }
    internal ImageView ImageView { get; }
    internal Sampler Sampler { get; }
    internal bool HasUploaded { get; set; }

    public TextAtlasPageDescription Description { get; }
    public bool IsDisposed => Image.Handle == default;

    public ValueTask DisposeAsync()
    {
        if (Image.Handle != default)
        {
            Owner.DestroyAtlasPage(this);
        }

        return ValueTask.CompletedTask;
    }

    internal void MarkDestroyed()
    {
        Image = default;
        Memory = default;
    }
}
