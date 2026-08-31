using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderSession
{
    internal BufferAllocation CreateNativeBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = Math.Max(4, size), Usage = usage, SharingMode = SharingMode.Exclusive };
        VulkanCall.Ensure(Api.CreateBuffer(Device, info, null, out var buffer), "CreateBuffer");
        try
        {
            var requirements = Api.GetBufferMemoryRequirements(Device, buffer);
            uint type = FindMemoryType(requirements.MemoryTypeBits, required, preferred);
            var properties = MemoryProperties.MemoryTypes[(int)type].PropertyFlags;
            var memory = AllocateMemory(requirements.Size, type, "AllocateBufferMemory");
            try
            {
                VulkanCall.Ensure(Api.BindBufferMemory(Device, buffer, memory, 0), "BindBufferMemory");
                return new BufferAllocation(buffer, memory, requirements.Size, properties);
            }
            catch
            {
                Api.FreeMemory(Device, memory, null);
                throw;
            }
        }
        catch
        {
            Api.DestroyBuffer(Device, buffer, null);
            throw;
        }
    }

    private DeviceMemory AllocateAndBindImageMemory(Image image)
    {
        var requirements = Api.GetImageMemoryRequirements(Device, image);
        uint type = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        DeviceMemory memory = default;
        try
        {
            memory = AllocateMemory(requirements.Size, type, "AllocateImageMemory");
            VulkanCall.Ensure(Api.BindImageMemory(Device, image, memory, 0), "BindImageMemory");
            return memory;
        }
        catch
        {
            if (memory.Handle != default)
            {
                Api.FreeMemory(Device, memory, null);
            }

            throw;
        }
    }

    private DeviceMemory AllocateMemory(ulong size, uint typeIndex, string operation)
    {
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = typeIndex
        };
        VulkanCall.Ensure(Api.AllocateMemory(Device, info, null, out var memory), operation);
        return memory;
    }

    private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspectMask, uint levelCount, uint layerCount, string operation)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspectMask,
                BaseMipLevel = 0,
                LevelCount = levelCount,
                BaseArrayLayer = 0,
                LayerCount = layerCount
            }
        };
        VulkanCall.Ensure(Api.CreateImageView(Device, info, null, out var view), operation);
        return view;
    }

    internal BufferAllocation CreateTransientBuffer(in RenderBufferDescription description)
    {
        var key = new TransientBufferKey(description.SizeInBytes, description.Usage);
        if (_transientBuffers.TryTake(key, out var reused))
        {
            return reused;
        }

        var created = CreateNativeBuffer(description.SizeInBytes, ToVulkanBufferUsage(description.Usage), MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        _transientBuffers.RecordCreated();
        return created;
    }

    internal PersistentTexture CreateTransientTexture(in RenderTextureDescription description)
    {
        var key = new TransientTextureKey(description.Width, description.Height, description.Format, description.MipLevels, description.Layers, description.Samples, description.Usage);
        if (_transientTextures.TryTake(key, out var reused))
        {
            return reused;
        }

        var created = CreateNativeTexture(description);
        _transientTextures.RecordCreated();
        return created;
    }

    internal void DeferTransient(BufferAllocation allocation, in RenderBufferDescription description)
    {
        if (VulkanBufferAllocation.IsLive(in allocation))
        {
            _deferredBuffers.Add(new DeferredBuffer(allocation, new TransientBufferKey(description.SizeInBytes, description.Usage), CurrentFrameSlot));
        }
    }

    internal void DeferTransient(PersistentTexture texture, in RenderTextureDescription description)
    {
        if (texture.Image.Handle != default)
        {
            _deferredTextures.Add(new DeferredTexture(texture, new TransientTextureKey(description.Width, description.Height, description.Format, description.MipLevels, description.Layers, description.Samples, description.Usage), CurrentFrameSlot));
        }
    }

    private void ReclaimDeferredTransients()
    {
        foreach (var texture in _deferredTextures)
        {
            _transientTextures.Return(texture.Key, texture.Texture);
        }

        foreach (var allocation in _deferredBuffers)
        {
            _transientBuffers.Return(allocation.Key, allocation.Allocation);
        }

        _deferredTextures.Clear();
        _deferredBuffers.Clear();
    }

    internal void ReclaimDeferredTransientsForBuild()
    {
        PrepareFrameSlot();
        if (_deferredTextures.Count == 0 && _deferredBuffers.Count == 0)
        {
            return;
        }
    }

    private void ReclaimDeferredTransientsForSlot(int slotIndex)
    {
        for (int index = _deferredTextures.Count - 1; index >= 0; index--)
        {
            var deferred = _deferredTextures[index];
            if (deferred.FrameSlot != slotIndex)
            {
                continue;
            }

            _transientTextures.Return(deferred.Key, deferred.Texture);
            _deferredTextures.RemoveAt(index);
        }

        for (int index = _deferredBuffers.Count - 1; index >= 0; index--)
        {
            var deferred = _deferredBuffers[index];
            if (deferred.FrameSlot != slotIndex)
            {
                continue;
            }

            _transientBuffers.Return(deferred.Key, deferred.Allocation);
            _deferredBuffers.RemoveAt(index);
        }
    }

    internal void DestroyAllocation(BufferAllocation allocation)
    {
        if (allocation.Buffer.Handle != default)
        {
            Api.DestroyBuffer(Device, allocation.Buffer, null);
        }

        if (allocation.Memory.Handle != default)
        {
            Api.FreeMemory(Device, allocation.Memory, null);
        }
    }

    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint index = 0; index < MemoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) == 0)
            {
                continue;
            }

            var flags = MemoryProperties.MemoryTypes[(int)index].PropertyFlags;
            if (!flags.HasFlag(required))
            {
                continue;
            }

            if (flags.HasFlag(preferred))
            {
                return index;
            }

            fallback = index;
        }

        if (fallback != uint.MaxValue)
        {
            return fallback;
        }

        throw new InvalidOperationException($"No Vulkan memory type satisfies {required}.");
    }

    private static BufferUsageFlags ToVulkanBufferUsage(RenderBufferUsage usage)
    {
        var result = BufferUsageFlags.None;
        if (usage.HasFlag(RenderBufferUsage.Vertex))
        {
            result |= BufferUsageFlags.VertexBufferBit;
        }

        if (usage.HasFlag(RenderBufferUsage.Index))
        {
            result |= BufferUsageFlags.IndexBufferBit;
        }

        if (usage.HasFlag(RenderBufferUsage.Uniform))
        {
            result |= BufferUsageFlags.UniformBufferBit;
        }

        if (usage.HasFlag(RenderBufferUsage.Storage))
        {
            result |= BufferUsageFlags.StorageBufferBit;
        }

        if (usage.HasFlag(RenderBufferUsage.Indirect))
        {
            result |= BufferUsageFlags.IndirectBufferBit;
        }

        if (usage.HasFlag(RenderBufferUsage.TransferSource))
        {
            result |= BufferUsageFlags.TransferSrcBit;
        }

        if (usage.HasFlag(RenderBufferUsage.TransferDestination))
        {
            result |= BufferUsageFlags.TransferDstBit;
        }

        return result;
    }

    private static ImageUsageFlags ToVulkanImageUsage(RenderTextureUsage usage)
    {
        var result = ImageUsageFlags.SampledBit;
        if (usage.HasFlag(RenderTextureUsage.Storage))
        {
            result |= ImageUsageFlags.StorageBit;
        }

        if (usage.HasFlag(RenderTextureUsage.ColorAttachment))
        {
            result |= ImageUsageFlags.ColorAttachmentBit;
        }

        if (usage.HasFlag(RenderTextureUsage.DepthStencilAttachment))
        {
            result |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if (usage.HasFlag(RenderTextureUsage.TransferSource))
        {
            result |= ImageUsageFlags.TransferSrcBit;
        }

        if (usage.HasFlag(RenderTextureUsage.TransferDestination))
        {
            result |= ImageUsageFlags.TransferDstBit;
        }

        return result;
    }

    private PersistentTexture CreateNativeTexture(RenderTextureDescription description)
    {
        var format = ToVulkanFormat(description.Format);
        var usage = ToVulkanImageUsage(description.Usage);
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(description.Width, description.Height, 1), MipLevels = description.MipLevels, ArrayLayers = description.Layers, Samples = ToSampleCount(description.Samples), Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined };
        VulkanCall.Ensure(Api.CreateImage(Device, info, null, out var image), "CreateImage");
        DeviceMemory memory = default;
        ImageView view = default;
        try
        {
            memory = AllocateAndBindImageMemory(image);
            view = CreateImageView(image, format, ToAspectMask(description.Format), description.MipLevels, description.Layers, "CreateImageView");
            return new PersistentTexture(image, memory, view, format, new Extent2D(description.Width, description.Height), 0) { Usage = description.Usage };
        }
        catch
        {
            DestroyTexture(new PersistentTexture(image, memory, view, format, new Extent2D(description.Width, description.Height), 0) { Usage = description.Usage });
            throw;
        }
    }

    internal void DestroyTexture(PersistentTexture texture)
    {
        if (texture.View.Handle != default)
        {
            Api.DestroyImageView(Device, texture.View, null);
        }

        if (texture.Image.Handle != default)
        {
            Api.DestroyImage(Device, texture.Image, null);
        }

        if (texture.Memory.Handle != default)
        {
            Api.FreeMemory(Device, texture.Memory, null);
        }
    }
}
