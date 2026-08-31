using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanRasterCommandContext(VulkanRenderGraph graph, VulkanRenderGraph.VulkanGraphPipeline? pipeline)
    : VulkanCommandContext(graph, pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.")), IRasterCommandContext
{
    public void SetViewport(in RenderViewport viewport)
    {
        if (!viewport.IsValid)
        {
            throw new ArgumentException("Viewport is invalid.", nameof(viewport));
        }

        Graph.CommandWriter.SetViewport(viewport);
    }

    public void SetScissor(in PixelRect scissor)
    {
        if (scissor.IsEmpty)
        {
            throw new ArgumentException("Scissor is empty.", nameof(scissor));
        }

        Graph.CommandWriter.SetScissor(scissor);
    }

    public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0)
    {
        var native = Graph.ResolveBufferAllocation(buffer).Buffer;
        Graph.CommandWriter.BindVertexBuffer(binding, native, offset);
    }

    public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0)
    {
        BindPipeline();
        Graph.CommandWriter.BindIndexBuffer(Graph.ResolveBufferAllocation(buffer).Buffer, format, offset);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        BindPipeline();
        Graph.CommandWriter.Draw(vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        BindPipeline();
        Graph.CommandWriter.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }
}
