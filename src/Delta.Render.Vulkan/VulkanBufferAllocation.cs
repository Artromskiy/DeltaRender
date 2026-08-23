using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace Delta.Render.Vulkan;

internal readonly record struct BufferAllocation(VulkanBuffer Buffer, DeviceMemory Memory, ulong AllocationSize, MemoryPropertyFlags MemoryProperties);

