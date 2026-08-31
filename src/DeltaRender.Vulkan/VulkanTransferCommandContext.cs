using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanTransferCommandContext(VulkanRenderGraph graph) : ITransferCommandContext
{
    private VulkanRenderGraph Graph { get; } = graph;

    public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0)
    {
        var src = Graph.ResolveBufferAllocation(source).Buffer;
        var dst = Graph.ResolveBufferAllocation(destination).Buffer;
        var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = sizeInBytes };
        Graph.CommandWriter.CopyBuffer(src, dst, copy);
    }

    public void CopyTexture(RenderGraphTextureHandle source, in PixelRect sourceRegion, RenderGraphTextureHandle destination, in PixelRect destinationRegion)
    {
        var sourceResource = Graph.ResolveTexture(source);
        var destinationResource = Graph.ResolveTexture(destination);
        var sourceImage = sourceResource.IsTarget ? Graph.TargetImage : sourceResource.Image;
        var destinationImage = destinationResource.IsTarget ? Graph.TargetImage : destinationResource.Image;
        if (sourceImage.Handle == default || destinationImage.Handle == default)
        {
            throw new InvalidOperationException("Texture copy resource is unavailable.");
        }

        if (sourceRegion.X < 0 || sourceRegion.Y < 0 || destinationRegion.X < 0 || destinationRegion.Y < 0 ||
            sourceRegion.Width <= 0 || sourceRegion.Height <= 0 ||
            sourceRegion.Width != destinationRegion.Width || sourceRegion.Height != destinationRegion.Height)
        {
            throw new ArgumentException("Texture copy regions must be positive and have equal dimensions.");
        }

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            SrcOffset = new Offset3D(sourceRegion.X, sourceRegion.Y, 0),
            DstSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            DstOffset = new Offset3D(destinationRegion.X, destinationRegion.Y, 0),
            Extent = new Extent3D((uint)sourceRegion.Width, (uint)sourceRegion.Height, 1)
        };
        Graph.CommandWriter.CopyTexture(sourceImage, destinationImage, copy);
    }

    public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
    {
        var sourceOffset = Graph.AllocateStaging(data);
        var src = Graph.StagingBufferHandle;
        var dst = Graph.ResolveBufferAllocation(destination).Buffer;
        var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = (ulong)data.Length };
        Graph.CommandWriter.CopyBuffer(src, dst, copy);
    }

    public void UploadTexture(RenderGraphTextureHandle destination, in PixelRect destinationRegion, ReadOnlySpan<byte> data, uint sourceRowPitch)
    {
        var resource = Graph.ResolveTexture(destination);
        if (resource.Texture is not { } texture)
        {
            throw new InvalidOperationException("Texture upload requires a registered or graph-owned texture.");
        }

        if (destinationRegion.X < 0 || destinationRegion.Y < 0 || destinationRegion.Width <= 0 || destinationRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationRegion), "Texture upload region must have positive dimensions and non-negative coordinates.");
        }

        var right = checked((uint)destinationRegion.X + (uint)destinationRegion.Width);
        var bottom = checked((uint)destinationRegion.Y + (uint)destinationRegion.Height);
        if (right > texture.Extent.Width || bottom > texture.Extent.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationRegion), "Texture upload region is outside the destination texture.");
        }

        var bytesPerPixel = texture.Format switch
        {
            Format.R8Unorm => 1u,
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4u,
            _ => throw new NotSupportedException($"Texture uploads do not support Vulkan format {texture.Format}.")
        };
        var minimumRowPitch = checked((uint)destinationRegion.Width * bytesPerPixel);
        if (sourceRowPitch < minimumRowPitch || sourceRowPitch % bytesPerPixel != 0)
        {
            throw new ArgumentException("Texture source row pitch is smaller than the region or misaligned to its pixel format.", nameof(sourceRowPitch));
        }

        var requiredBytes = checked((ulong)sourceRowPitch * (uint)destinationRegion.Height);
        if ((ulong)data.Length < requiredBytes)
        {
            throw new ArgumentException("Texture upload data is shorter than sourceRowPitch multiplied by region height.", nameof(data));
        }

        var sourceOffset = Graph.AllocateStaging(data[..checked((int)requiredBytes)]);
        var copy = new BufferImageCopy
        {
            BufferOffset = sourceOffset,
            BufferRowLength = sourceRowPitch / bytesPerPixel,
            BufferImageHeight = (uint)destinationRegion.Height,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(destinationRegion.X, destinationRegion.Y, 0),
            ImageExtent = new Extent3D((uint)destinationRegion.Width, (uint)destinationRegion.Height, 1)
        };
        Graph.CommandWriter.UploadTexture(Graph.StagingBufferHandle, texture.Image, copy);
    }
}
