using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal abstract unsafe class VulkanCommandContext(VulkanRenderGraph graph, VulkanRenderGraph.VulkanGraphPipeline pipeline)
{
    protected VulkanRenderGraph Graph { get; } = graph;
    protected VulkanRenderGraph.VulkanGraphPipeline Pipeline { get; } = pipeline;

    public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0)
        => Graph.BindBuffer(Pipeline, binding, buffer, offset, sizeInBytes);

    public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
        => Graph.BindTexture(Pipeline, binding, texture, sampler);

    public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0)
        => Graph.PushConstants(Pipeline, data, offset);
}

internal sealed unsafe class VulkanCommandWriter(VulkanRenderSession session)
{
    internal void SetViewport(in RenderViewport viewport)
    {
        var value = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
        session.Api.CmdSetViewport(session.CommandBuffer, 0, 1, &value);
    }

    internal void SetScissor(in PixelRect scissor)
    {
        var value = new Rect2D { Offset = new Offset2D(scissor.X, scissor.Y), Extent = new Extent2D((uint)scissor.Width, (uint)scissor.Height) };
        session.Api.CmdSetScissor(session.CommandBuffer, 0, 1, &value);
    }

    internal void BindVertexBuffer(uint binding, Silk.NET.Vulkan.Buffer buffer, ulong offset)
        => session.Api.CmdBindVertexBuffers(session.CommandBuffer, binding, 1, &buffer, &offset);

    internal void BindIndexBuffer(Silk.NET.Vulkan.Buffer buffer, IndexElementFormat format, ulong offset)
        => session.Api.CmdBindIndexBuffer(session.CommandBuffer, buffer, offset, format == IndexElementFormat.UnsignedShort ? IndexType.Uint16 : IndexType.Uint32);

    internal void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        => session.Api.CmdDraw(session.CommandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);

    internal void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
        => session.Api.CmdDrawIndexed(session.CommandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    internal void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        => session.Api.CmdDispatch(session.CommandBuffer, groupCountX, groupCountY, groupCountZ);

    internal void CopyBuffer(Silk.NET.Vulkan.Buffer source, Silk.NET.Vulkan.Buffer destination, BufferCopy copy)
        => session.Api.CmdCopyBuffer(session.CommandBuffer, source, destination, 1, &copy);

    internal void CopyTexture(Image source, Image destination, ImageCopy copy)
        => session.Api.CmdCopyImage(session.CommandBuffer, source, ImageLayout.TransferSrcOptimal, destination, ImageLayout.TransferDstOptimal, 1, &copy);

    internal void UploadTexture(Silk.NET.Vulkan.Buffer source, Image destination, BufferImageCopy copy)
        => session.Api.CmdCopyBufferToImage(session.CommandBuffer, source, destination, ImageLayout.TransferDstOptimal, 1, &copy);
}

internal sealed unsafe partial class VulkanRenderGraph
{
    private VulkanCommandWriter? _commandWriter;
    internal VulkanCommandWriter CommandWriter => _commandWriter ??= new VulkanCommandWriter(Session);
    internal Image TargetImage => Session.GraphImage;
    internal Silk.NET.Vulkan.Buffer StagingBufferHandle => StagingBuffer.Buffer;
}

internal sealed unsafe class VulkanRasterCommandContext(VulkanRenderGraph graph, VulkanRenderGraph.VulkanGraphPipeline? pipeline)
    : VulkanCommandContext(graph, pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.")), IRasterCommandContext
{
    public void SetViewport(in RenderViewport viewport) { if (!viewport.IsValid) throw new ArgumentException("Viewport is invalid.", nameof(viewport)); Graph.CommandWriter.SetViewport(viewport); }
    public void SetScissor(in PixelRect scissor) { if (scissor.IsEmpty) throw new ArgumentException("Scissor is empty.", nameof(scissor)); Graph.CommandWriter.SetScissor(scissor); }
    public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0) { var native = Graph.ResolveBufferAllocation(buffer).Buffer; Graph.CommandWriter.BindVertexBuffer(binding, native, offset); }
    public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0) { Graph.Bind(Pipeline); Graph.CommandWriter.BindIndexBuffer(Graph.ResolveBufferAllocation(buffer).Buffer, format, offset); }
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) { Graph.Bind(Pipeline); Graph.CommandWriter.Draw(vertexCount, instanceCount, firstVertex, firstInstance); }
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0) { Graph.Bind(Pipeline); Graph.CommandWriter.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance); }
}

internal sealed unsafe class VulkanComputeCommandContext(VulkanRenderGraph graph, VulkanRenderGraph.VulkanGraphPipeline? pipeline)
    : VulkanCommandContext(graph, pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline.")), IComputeCommandContext
{
    public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1) { if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0) return; Graph.Bind(Pipeline); Graph.CommandWriter.Dispatch(groupCountX, groupCountY, groupCountZ); }
}

internal sealed unsafe class VulkanTransferCommandContext(VulkanRenderGraph graph) : ITransferCommandContext
{
    private VulkanRenderGraph Graph { get; } = graph;

    public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0) { var src = Graph.ResolveBufferAllocation(source).Buffer; var dst = Graph.ResolveBufferAllocation(destination).Buffer; var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = sizeInBytes }; Graph.CommandWriter.CopyBuffer(src, dst, copy); }
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
    public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0) { var sourceOffset = Graph.AllocateStaging(data); var src = Graph.StagingBufferHandle; var dst = Graph.ResolveBufferAllocation(destination).Buffer; var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = (ulong)data.Length }; Graph.CommandWriter.CopyBuffer(src, dst, copy); }
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
