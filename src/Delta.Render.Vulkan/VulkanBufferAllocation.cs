using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace Delta.Render.Vulkan;

internal readonly record struct BufferAllocation(VulkanBuffer Buffer, DeviceMemory Memory, ulong AllocationSize, MemoryPropertyFlags MemoryProperties);

internal sealed class VulkanBufferLease
{
    private BufferAllocation _allocation;

    internal bool IsLive => VulkanBufferAllocation.IsLive(in _allocation);

    internal BufferAllocation Value => _allocation;

    internal void Assign(BufferAllocation allocation)
    {
        if (IsLive)
        {
            throw new InvalidOperationException("Cannot replace a live Vulkan buffer lease without releasing it first.");
        }

        _allocation = allocation;
    }

    internal BufferAllocation Release()
    {
        var allocation = _allocation;
        _allocation = default;
        return allocation;
    }
}

internal static class VulkanBufferAllocation
{
    internal static bool IsLive(in BufferAllocation allocation)
        => allocation.Buffer.Handle != default || allocation.Memory.Handle != default;
}
