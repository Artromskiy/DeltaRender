using Silk.NET.Vulkan;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

internal sealed class VulkanFrameSlotResources
{
    internal VulkanFrameSlotResources(VulkanRenderSession session)
    {
        StagingBuffer = new VulkanStagingBuffer(session);
    }

    internal Fence Fence;

    internal CommandPool CommandPool;

    internal CommandBuffer CommandBuffer;

    internal CommandPool ComputeCommandPool;

    internal CommandBuffer ComputeCommandBuffer;

    internal CommandPool TransferCommandPool;

    internal CommandBuffer TransferCommandBuffer;

    internal VulkanSemaphore ImageAvailable;

    internal VulkanSemaphore RenderComplete;

    internal Image TargetImage;

    internal DeviceMemory TargetMemory;

    internal ImageView TargetView;

    internal Framebuffer TargetFramebuffer;

    internal bool InFlight;

    internal List<VulkanSemaphore> QueueTransitionSignals { get; } = [];

    internal VulkanStagingBuffer StagingBuffer { get; }
}
