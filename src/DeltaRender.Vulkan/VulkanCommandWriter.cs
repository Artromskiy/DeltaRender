using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanCommandWriter(VulkanRenderSession session)
{
    private RenderViewport _lastViewport;
    private PixelRect _lastScissor;
    private bool _hasViewport;
    private bool _hasScissor;

    internal void ResetState()
    {
        _hasViewport = false;
        _hasScissor = false;
    }

    internal void SetViewport(in RenderViewport viewport)
    {
        if (_hasViewport && _lastViewport.Equals(viewport))
        {
            return;
        }

        var value = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
        session.Api.CmdSetViewport(session.CommandBuffer, 0, 1, &value);
        _lastViewport = viewport;
        _hasViewport = true;
    }

    internal void SetScissor(in PixelRect scissor)
    {
        if (_hasScissor && _lastScissor == scissor)
        {
            return;
        }

        var value = new Rect2D
        {
            Offset = new Offset2D(scissor.X, scissor.Y),
            Extent = new Extent2D((uint)scissor.Width, (uint)scissor.Height)
        };
        session.Api.CmdSetScissor(session.CommandBuffer, 0, 1, &value);
        _lastScissor = scissor;
        _hasScissor = true;
    }

    internal void BindPipeline(PipelineBindPoint bindPoint, Pipeline pipeline)
        => session.Api.CmdBindPipeline(session.CommandBuffer, bindPoint, pipeline);

    internal unsafe void BindDescriptorSets(
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        ReadOnlySpan<DescriptorSet> descriptorSets)
    {
        fixed (DescriptorSet* descriptorSetPointer = descriptorSets)
        {
            session.Api.CmdBindDescriptorSets(
                session.CommandBuffer,
                bindPoint,
                layout,
                0,
                (uint)descriptorSets.Length,
                descriptorSetPointer,
                0,
                null);
        }
    }

    internal void EndRenderPass()
        => session.Api.CmdEndRenderPass(session.CommandBuffer);

    internal unsafe void BeginRenderPass(RenderPass renderPass, Framebuffer framebuffer, Extent2D extent, ClearValue* clearValues, uint clearValueCount)
    {
        _hasViewport = false;
        _hasScissor = false;
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = extent },
            ClearValueCount = clearValueCount,
            PClearValues = clearValues,
        };
        session.Api.CmdBeginRenderPass(session.CommandBuffer, &begin, SubpassContents.Inline);
    }

    internal unsafe void PushConstants(PipelineLayout layout, ShaderStageFlags stageFlags, ReadOnlySpan<byte> data, uint offset)
    {
        fixed (byte* pointer = data)
        {
            session.Api.CmdPushConstants(session.CommandBuffer, layout, stageFlags, offset, (uint)data.Length, pointer);
        }
    }

    internal void PipelineBarrier(
        PipelineStageFlags sourceStage,
        PipelineStageFlags destinationStage,
        ReadOnlySpan<BufferMemoryBarrier> buffers,
        ReadOnlySpan<ImageMemoryBarrier> images)
        => session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, buffers, images);

    internal unsafe void PipelineBarrier(PipelineStageFlags sourceStage, PipelineStageFlags destinationStage, in BufferMemoryBarrier barrier)
    {
        var value = barrier;
        session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, new ReadOnlySpan<BufferMemoryBarrier>(&value, 1), ReadOnlySpan<ImageMemoryBarrier>.Empty);
    }

    internal unsafe void PipelineBarrier(PipelineStageFlags sourceStage, PipelineStageFlags destinationStage, in ImageMemoryBarrier barrier)
    {
        var value = barrier;
        session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, ReadOnlySpan<BufferMemoryBarrier>.Empty, new ReadOnlySpan<ImageMemoryBarrier>(&value, 1));
    }

    internal unsafe void CopyImageToBuffer(Image source, ImageLayout layout, Silk.NET.Vulkan.Buffer destination, BufferImageCopy copy)
        => session.Api.CmdCopyImageToBuffer(session.CommandBuffer, source, layout, destination, 1, &copy);

    internal unsafe void BindVertexBuffer(uint binding, Silk.NET.Vulkan.Buffer buffer, ulong offset)
        => session.Api.CmdBindVertexBuffers(session.CommandBuffer, binding, 1, &buffer, &offset);

    internal void BindIndexBuffer(Silk.NET.Vulkan.Buffer buffer, IndexElementFormat format, ulong offset)
        => session.Api.CmdBindIndexBuffer(session.CommandBuffer, buffer, offset, format == IndexElementFormat.UnsignedShort ? IndexType.Uint16 : IndexType.Uint32);

    internal void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        => session.Api.CmdDraw(session.CommandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);

    internal void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
        => session.Api.CmdDrawIndexed(session.CommandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    internal void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        => session.Api.CmdDispatch(session.CommandBuffer, groupCountX, groupCountY, groupCountZ);

    internal unsafe void CopyBuffer(Silk.NET.Vulkan.Buffer source, Silk.NET.Vulkan.Buffer destination, BufferCopy copy)
        => session.Api.CmdCopyBuffer(session.CommandBuffer, source, destination, 1, &copy);

    internal unsafe void CopyTexture(Image source, Image destination, ImageCopy copy)
        => session.Api.CmdCopyImage(session.CommandBuffer, source, ImageLayout.TransferSrcOptimal, destination, ImageLayout.TransferDstOptimal, 1, &copy);

    internal unsafe void UploadTexture(Silk.NET.Vulkan.Buffer source, Image destination, BufferImageCopy copy)
        => session.Api.CmdCopyBufferToImage(session.CommandBuffer, source, destination, ImageLayout.TransferDstOptimal, 1, &copy);
}
