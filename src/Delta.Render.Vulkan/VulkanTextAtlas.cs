using System.Buffers;
using Delta.Render.Core;
using Silk.NET.Vulkan;
using VulkanImage = Silk.NET.Vulkan.Image;

namespace Delta.Render.Vulkan;

public sealed unsafe partial class VulkanComputeDevice : ITextAtlasDevice
{
    private readonly List<VulkanTextAtlasPage> _atlasPages = new();
    private BufferAllocation _atlasStaging;
    private int _atlasStagingAllocationCount;
    private int _atlasSubmissionCount;

    public TextAtlasUploadStatistics AtlasUploadStatistics => new(_atlasStagingAllocationCount, _atlasSubmissionCount, _atlasStaging.AllocationSize);

    public ITextAtlasPage CreateAtlasPage(in TextAtlasPageDescription description)
    {
        ThrowIfDisposed();
        if (!description.IsValid) throw new ArgumentException("Atlas page description is invalid.", nameof(description));

        var image = default(VulkanImage);
        var memory = default(DeviceMemory);
        var view = default(ImageView);
        var sampler = default(Sampler);
        var layout = default(DescriptorSetLayout);
        var pool = default(DescriptorPool);
        var descriptorSet = default(DescriptorSet);
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
            Ensure(_api.CreateImage(_device, imageInfo, null, out image), "CreateImage");
            var requirements = _api.GetImageMemoryRequirements(_device, image);
            var memoryType = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
            var allocation = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType
            };
            Ensure(_api.AllocateMemory(_device, allocation, null, out memory), "AllocateImageMemory");
            Ensure(_api.BindImageMemory(_device, image, memory, 0), "BindImageMemory");

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
            Ensure(_api.CreateImageView(_device, viewInfo, null, out view), "CreateImageView");

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
            Ensure(_api.CreateSampler(_device, samplerInfo, null, out sampler), "CreateSampler");

            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
            };
            Ensure(_api.CreateDescriptorSetLayout(_device, layoutInfo, null, out layout), "CreateDescriptorSetLayout");

            var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize
            };
            Ensure(_api.CreateDescriptorPool(_device, poolInfo, null, out pool), "CreateDescriptorPool");
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };
            Ensure(_api.AllocateDescriptorSets(_device, allocateInfo, out descriptorSet), "AllocateDescriptorSets");

            var imageDescriptor = new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageDescriptor
            };
            _api.UpdateDescriptorSets(_device, 1, &write, 0, null);

            var result = new VulkanTextAtlasPage(this, description, image, memory, requirements.Size, view, sampler, layout, pool, descriptorSet);
            _atlasPages.Add(result);
            return result;
        }
        catch
        {
            if (pool.Handle != default) _api.DestroyDescriptorPool(_device, pool, null);
            if (layout.Handle != default) _api.DestroyDescriptorSetLayout(_device, layout, null);
            if (sampler.Handle != default) _api.DestroySampler(_device, sampler, null);
            if (view.Handle != default) _api.DestroyImageView(_device, view, null);
            if (image.Handle != default) _api.DestroyImage(_device, image, null);
            if (memory.Handle != default) _api.FreeMemory(_device, memory, null);
            throw;
        }
    }

    public bool UploadAtlasPage(ITextAtlasPage page, ReadOnlySpan<byte> pixels, uint sourceRowPitch)
    {
        if (!TryGetAtlasPage(page, out var target) || sourceRowPitch < target.Description.Width * target.Description.BytesPerPixel)
            return false;
        var required = checked((ulong)sourceRowPitch * target.Description.Height);
        if ((ulong)pixels.Length < required) return false;
        var range = new TextAtlasDirtyRange(0, 0, target.Description.Width, target.Description.Height, sourceRowPitch, pixels.ToArray());
        return UploadAtlasDirtyRanges(target, new[] { range });
    }

    public bool UploadAtlasDirtyRanges(ITextAtlasPage page, ReadOnlySpan<TextAtlasDirtyRange> ranges)
    {
        ThrowIfDisposed();
        if (!TryGetAtlasPage(page, out var target)) return false;
        if (ranges.IsEmpty) return true;

        var copies = ArrayPool<BufferImageCopy>.Shared.Rent(ranges.Length);
        var stagingBytes = 0UL;
        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var range = ranges[i];
                var rowBytes = checked((ulong)range.Width * target.Description.BytesPerPixel);
                if (!range.IsValid || (ulong)range.X + range.Width > target.Description.Width || (ulong)range.Y + range.Height > target.Description.Height ||
                    range.SourceRowPitch < rowBytes || (ulong)range.Source.Length < (ulong)range.SourceRowPitch * range.Height)
                    return false;
                copies[i] = new BufferImageCopy
                {
                    BufferOffset = stagingBytes,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                    ImageOffset = new Offset3D { X = (int)range.X, Y = (int)range.Y, Z = 0 },
                    ImageExtent = new Extent3D { Width = range.Width, Height = range.Height, Depth = 1 }
                };
                stagingBytes = checked(stagingBytes + rowBytes * range.Height);
            }

            EnsureAtlasStagingCapacity(stagingBytes);
            FillAtlasStaging(ranges, stagingBytes, target.Description.BytesPerPixel);
            BeginCommandBuffer();
            var transition = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = target.HasUploaded ? AccessFlags.ShaderReadBit : AccessFlags.None,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = target.HasUploaded ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
            };
            _api.CmdPipelineBarrier(_commandBuffer, target.HasUploaded ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty,
                ReadOnlySpan<BufferMemoryBarrier>.Empty, new[] { transition });
            _api.CmdCopyBufferToImage(_commandBuffer, _atlasStaging.Buffer, target.Image, ImageLayout.TransferDstOptimal, copies.AsSpan(0, ranges.Length));
            transition.SrcAccessMask = AccessFlags.TransferWriteBit;
            transition.DstAccessMask = AccessFlags.ShaderReadBit;
            transition.OldLayout = ImageLayout.TransferDstOptimal;
            transition.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            _api.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, ReadOnlySpan<BufferMemoryBarrier>.Empty, new[] { transition });
            if (!EndAndWait()) return false;
            target.HasUploaded = true;
            _atlasSubmissionCount++;
            return true;
        }
        finally
        {
            ArrayPool<BufferImageCopy>.Shared.Return(copies, clearArray: true);
        }
    }

    internal void DestroyAtlasPage(VulkanTextAtlasPage page)
    {
        if (page.Image.Handle == default) return;
        if (page.DescriptorPool.Handle != default) _api.DestroyDescriptorPool(_device, page.DescriptorPool, null);
        if (page.DescriptorSetLayout.Handle != default) _api.DestroyDescriptorSetLayout(_device, page.DescriptorSetLayout, null);
        if (page.Sampler.Handle != default) _api.DestroySampler(_device, page.Sampler, null);
        if (page.ImageView.Handle != default) _api.DestroyImageView(_device, page.ImageView, null);
        _api.DestroyImage(_device, page.Image, null);
        _api.FreeMemory(_device, page.Memory, null);
        _atlasPages.Remove(page);
        page.MarkDestroyed();
    }

    private bool TryGetAtlasPage(ITextAtlasPage page, out VulkanTextAtlasPage result)
    {
        result = page as VulkanTextAtlasPage ?? null!;
        return result is not null && ReferenceEquals(result.Owner, this) && result.Image.Handle != default;
    }

    private void EnsureAtlasStagingCapacity(ulong requiredBytes)
    {
        if (_atlasStaging.Buffer.Handle != default && _atlasStaging.AllocationSize >= requiredBytes) return;
        var requested = Math.Max(requiredBytes, 4UL);
        var current = _atlasStaging.AllocationSize;
        while (current != 0 && current < requested) current = current > ulong.MaxValue / 2 ? requested : current * 2;
        var replacement = CreateBuffer(Math.Max(requested, current), BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit);
        var previous = _atlasStaging;
        _atlasStaging = replacement;
        _atlasStagingAllocationCount++;
        DestroyAllocation(previous);
    }

    private void FillAtlasStaging(ReadOnlySpan<TextAtlasDirtyRange> ranges, ulong stagingBytes, uint bytesPerPixel)
    {
        void* mapped = null;
        Ensure(_api.MapMemory(_device, _atlasStaging.Memory, 0, _atlasStaging.AllocationSize, 0, &mapped), "MapMemory");
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
            if (!_atlasStaging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit)) Flush(_atlasStaging.Memory, _atlasStaging.AllocationSize);
        }
        finally { _api.UnmapMemory(_device, _atlasStaging.Memory); }
    }

    private static Format ToVulkanFormat(TextAtlasFormat format) => format switch
    {
        TextAtlasFormat.R8Unorm => Format.R8Unorm,
        TextAtlasFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

public sealed unsafe class VulkanTextAtlasPage : ITextAtlasPage
{
    internal VulkanTextAtlasPage(VulkanComputeDevice owner, TextAtlasPageDescription description, VulkanImage image,
        DeviceMemory memory, ulong allocationSize, ImageView imageView, Sampler sampler,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool, DescriptorSet descriptorSet)
    {
        Owner = owner;
        Description = description;
        Image = image;
        Memory = memory;
        AllocationSize = allocationSize;
        ImageView = imageView;
        Sampler = sampler;
        DescriptorSetLayout = descriptorSetLayout;
        DescriptorPool = descriptorPool;
        DescriptorSet = descriptorSet;
    }

    internal VulkanComputeDevice Owner { get; }
    internal VulkanImage Image { get; private set; }
    internal DeviceMemory Memory { get; private set; }
    internal ulong AllocationSize { get; }
    internal ImageView ImageView { get; }
    internal Sampler Sampler { get; }
    internal DescriptorSetLayout DescriptorSetLayout { get; }
    internal DescriptorPool DescriptorPool { get; }
    internal DescriptorSet DescriptorSet { get; }
    internal bool HasUploaded { get; set; }

    public TextAtlasPageDescription Description { get; }
    public bool IsDisposed => Image.Handle == default;

    public ValueTask DisposeAsync()
    {
        if (Image.Handle != default) Owner.DestroyAtlasPage(this);
        return ValueTask.CompletedTask;
    }

    internal void MarkDestroyed()
    {
        Image = default;
        Memory = default;
    }
}
