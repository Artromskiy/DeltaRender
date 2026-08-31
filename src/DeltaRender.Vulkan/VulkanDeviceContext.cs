using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal readonly record struct VulkanDeviceContext(
    Vk Api,
    PhysicalDevice PhysicalDevice,
    Device Device,
    PhysicalDeviceMemoryProperties MemoryProperties,
    Queue GraphicsQueue,
    Queue ComputeQueue,
    Queue TransferQueue,
    Queue PresentQueue,
    uint GraphicsFamily,
    uint ComputeFamily,
    uint TransferFamily,
    uint[] QueueFamilies,
    uint PresentFamily);
