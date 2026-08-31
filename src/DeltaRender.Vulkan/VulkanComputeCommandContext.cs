using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanComputeCommandContext(VulkanRenderGraph graph, VulkanGraphPipeline? pipeline)
    : VulkanCommandContext(graph, pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline.")), IComputeCommandContext
{
    public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1)
    {
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
        {
            return;
        }

        BindPipeline();
        Graph.CommandWriter.Dispatch(groupCountX, groupCountY, groupCountZ);
    }
}
