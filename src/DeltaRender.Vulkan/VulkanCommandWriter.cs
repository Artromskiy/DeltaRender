using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanCommandWriter(VulkanRenderSession session)
{
    private RenderViewport _lastViewport;
    private PixelRect _lastScissor;
    private Pipeline _lastGraphicsPipeline;
    private Pipeline _lastComputePipeline;
    private DescriptorSet[] _lastDescriptorSets = [];
    private DescriptorSet _lastDescriptorSet;
    private int _lastDescriptorSetCount;
    private PipelineLayout _lastDescriptorLayout;
    private PipelineBindPoint _lastDescriptorBindPoint;
    private Silk.NET.Vulkan.Buffer _lastVertexBuffer;
    private Silk.NET.Vulkan.Buffer _lastIndexBuffer;
    private uint _lastVertexBinding;
    private ulong _lastVertexOffset;
    private ulong _lastIndexOffset;
    private IndexType _lastIndexType;
    private bool _hasViewport;
    private bool _hasScissor;
    private bool _hasGraphicsPipeline;
    private bool _hasComputePipeline;
    private bool _hasDescriptorSets;
    private bool _hasVertexBuffer;
    private bool _hasIndexBuffer;

    internal void ResetState()
    {
        _hasViewport = false;
        _hasScissor = false;
        _hasGraphicsPipeline = false;
        _hasComputePipeline = false;
        _hasDescriptorSets = false;
        _hasVertexBuffer = false;
        _hasIndexBuffer = false;
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
    {
        if (bindPoint == PipelineBindPoint.Graphics)
        {
            if (_hasGraphicsPipeline && _lastGraphicsPipeline.Handle == pipeline.Handle)
            {
                return;
            }

            _lastGraphicsPipeline = pipeline;
            _hasGraphicsPipeline = true;
        }
        else if (bindPoint == PipelineBindPoint.Compute)
        {
            if (_hasComputePipeline && _lastComputePipeline.Handle == pipeline.Handle)
            {
                return;
            }

            _lastComputePipeline = pipeline;
            _hasComputePipeline = true;
        }

        session.Api.CmdBindPipeline(session.CommandBuffer, bindPoint, pipeline);
    }

    internal unsafe void BindDescriptorSets(
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        ReadOnlySpan<DescriptorSet> descriptorSets)
    {
        if (_hasDescriptorSets &&
            _lastDescriptorBindPoint == bindPoint &&
            _lastDescriptorLayout.Handle == layout.Handle &&
            DescriptorSetsMatch(descriptorSets))
        {
            return;
        }

        if (descriptorSets.IsEmpty)
        {
            _lastDescriptorSetCount = 0;
            _lastDescriptorBindPoint = bindPoint;
            _lastDescriptorLayout = layout;
            _hasDescriptorSets = true;
            return;
        }

        if (descriptorSets.Length > 1 && _lastDescriptorSets.Length < descriptorSets.Length)
        {
            Array.Resize(ref _lastDescriptorSets, descriptorSets.Length);
        }

        if (descriptorSets.Length == 1)
        {
            _lastDescriptorSet = descriptorSets[0];
        }
        else
        {
            descriptorSets.CopyTo(_lastDescriptorSets);
        }
        _lastDescriptorSetCount = descriptorSets.Length;
        _lastDescriptorBindPoint = bindPoint;
        _lastDescriptorLayout = layout;
        _hasDescriptorSets = true;
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
            session.ProfilerState?.RecordDescriptorBind();
        }
    }

    private bool DescriptorSetsMatch(ReadOnlySpan<DescriptorSet> descriptorSets)
    {
        if (descriptorSets.Length != _lastDescriptorSetCount)
        {
            return false;
        }

        if (descriptorSets.Length == 1)
        {
            return _lastDescriptorSet.Handle == descriptorSets[0].Handle;
        }

        for (int index = 0; index < descriptorSets.Length; index++)
        {
            if (_lastDescriptorSets[index].Handle != descriptorSets[index].Handle)
            {
                return false;
            }
        }

        return true;
    }

    internal void EndRenderPass()
        => session.Api.CmdEndRenderPass(session.CommandBuffer);

    internal unsafe void BeginRenderPass(RenderPass renderPass, Framebuffer framebuffer, Extent2D extent, ClearValue* clearValues, uint clearValueCount)
    {
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
        => session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, [], buffers, images);

    internal unsafe void PipelineBarrier(PipelineStageFlags sourceStage, PipelineStageFlags destinationStage, in BufferMemoryBarrier barrier)
    {
        var value = barrier;
        session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, [], new ReadOnlySpan<BufferMemoryBarrier>(&value, 1), []);
    }

    internal unsafe void PipelineBarrier(PipelineStageFlags sourceStage, PipelineStageFlags destinationStage, in ImageMemoryBarrier barrier)
    {
        var value = barrier;
        session.Api.CmdPipelineBarrier(session.CommandBuffer, sourceStage, destinationStage, DependencyFlags.None, [], [], new ReadOnlySpan<ImageMemoryBarrier>(&value, 1));
    }

    internal unsafe void CopyImageToBuffer(Image source, ImageLayout layout, Silk.NET.Vulkan.Buffer destination, BufferImageCopy copy)
        => session.Api.CmdCopyImageToBuffer(session.CommandBuffer, source, layout, destination, 1, &copy);

    internal unsafe void BindVertexBuffer(uint binding, Silk.NET.Vulkan.Buffer buffer, ulong offset)
    {
        if (_hasVertexBuffer &&
            _lastVertexBinding == binding &&
            _lastVertexBuffer.Handle == buffer.Handle &&
            _lastVertexOffset == offset)
        {
            return;
        }

        session.Api.CmdBindVertexBuffers(session.CommandBuffer, binding, 1, &buffer, &offset);
        session.ProfilerState?.RecordVertexBufferBind();
        _lastVertexBinding = binding;
        _lastVertexBuffer = buffer;
        _lastVertexOffset = offset;
        _hasVertexBuffer = true;
    }

    internal void BindIndexBuffer(Silk.NET.Vulkan.Buffer buffer, IndexElementFormat format, ulong offset)
    {
        var indexType = format == IndexElementFormat.UnsignedShort ? IndexType.Uint16 : IndexType.Uint32;
        if (_hasIndexBuffer &&
            _lastIndexBuffer.Handle == buffer.Handle &&
            _lastIndexOffset == offset &&
            _lastIndexType == indexType)
        {
            return;
        }

        session.Api.CmdBindIndexBuffer(session.CommandBuffer, buffer, offset, indexType);
        session.ProfilerState?.RecordIndexBufferBind();
        _lastIndexBuffer = buffer;
        _lastIndexOffset = offset;
        _lastIndexType = indexType;
        _hasIndexBuffer = true;
    }

    internal void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        session.Api.CmdDraw(session.CommandBuffer, vertexCount, instanceCount, firstVertex, firstInstance);
        session.ProfilerState?.RecordDrawCall();
    }

    internal void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        session.Api.CmdDrawIndexed(session.CommandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
        session.ProfilerState?.RecordDrawCall();
    }

    internal void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        => session.Api.CmdDispatch(session.CommandBuffer, groupCountX, groupCountY, groupCountZ);

    internal unsafe void CopyBuffer(Silk.NET.Vulkan.Buffer source, Silk.NET.Vulkan.Buffer destination, BufferCopy copy)
        => session.Api.CmdCopyBuffer(session.CommandBuffer, source, destination, 1, &copy);

    internal unsafe void CopyTexture(Image source, Image destination, ImageCopy copy)
        => session.Api.CmdCopyImage(session.CommandBuffer, source, ImageLayout.TransferSrcOptimal, destination, ImageLayout.TransferDstOptimal, 1, &copy);

    internal unsafe void UploadTexture(Silk.NET.Vulkan.Buffer source, Image destination, BufferImageCopy copy)
        => session.Api.CmdCopyBufferToImage(session.CommandBuffer, source, destination, ImageLayout.TransferDstOptimal, 1, &copy);
}
