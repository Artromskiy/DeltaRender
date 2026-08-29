using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal readonly record struct VulkanDeviceContext(
    Vk Api,
    PhysicalDevice PhysicalDevice,
    Device Device,
    PhysicalDeviceMemoryProperties MemoryProperties,
    Queue GraphicsQueue,
    Queue PresentQueue,
    uint GraphicsFamily,
    uint PresentFamily);
