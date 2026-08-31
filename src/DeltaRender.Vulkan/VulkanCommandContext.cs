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

    protected void BindPipeline()
        => Graph.Bind(Pipeline);
}
