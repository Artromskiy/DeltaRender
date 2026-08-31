using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed class VulkanHeadlessFrameSlot
{
    internal VulkanHeadlessFrameSlot(VulkanRenderSession session)
    {
        StagingBuffer = new VulkanStagingBuffer(session);
    }

    internal Fence Fence;

    internal CommandPool CommandPool;

    internal CommandBuffer CommandBuffer;

    internal Image TargetImage;

    internal DeviceMemory TargetMemory;

    internal ImageView TargetView;

    internal Framebuffer TargetFramebuffer;

    internal VulkanStagingBuffer StagingBuffer { get; }
}
