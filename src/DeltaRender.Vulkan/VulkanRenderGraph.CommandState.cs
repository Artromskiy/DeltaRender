using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderGraph
{
    private VulkanCommandWriter? _commandWriter;

    internal VulkanCommandWriter CommandWriter => _commandWriter ??= new VulkanCommandWriter(Session);

    internal Image TargetImage => Session.GraphImage;

    internal Silk.NET.Vulkan.Buffer StagingBufferHandle => StagingBuffer.Buffer;
}
