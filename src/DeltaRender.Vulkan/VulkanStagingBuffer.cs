using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanStagingBuffer
{
    private readonly VulkanRenderSession _session;
    private readonly List<BufferAllocation> _retired = new();
    private BufferAllocation _current;
    private ulong _cursor;

    internal VulkanStagingBuffer(VulkanRenderSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal BufferAllocation Current => _current;

    internal ulong Allocate(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return _cursor;
        }

        var offset = Align(_cursor, 4);
        EnsureCapacity(checked(offset + (ulong)data.Length));

        void* pointer = null;
        VulkanCall.Ensure(
            _session.Api.MapMemory(_session.Device, _current.Memory, offset, (ulong)data.Length, 0, &pointer),
            "MapMemory(staging upload)");
        try
        {
            data.CopyTo(new Span<byte>(pointer, data.Length));
            if (!_current.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                var range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = _current.Memory,
                    Offset = 0,
                    Size = (nuint)_current.AllocationSize
                };
                VulkanCall.Ensure(
                    _session.Api.FlushMappedMemoryRanges(_session.Device, 1, &range),
                    "FlushMappedMemoryRanges(staging upload)");
            }
        }
        finally
        {
            _session.Api.UnmapMemory(_session.Device, _current.Memory);
        }

        _cursor = checked(offset + (ulong)data.Length);
        return offset;
    }

    internal ulong Reserve(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var offset = Align(_cursor, 4);
        EnsureCapacity(checked(offset + (ulong)size));
        _cursor = checked(offset + (ulong)size);
        return offset;
    }

    internal void ReclaimCompleted()
    {
        foreach (var allocation in _retired)
        {
            _session.DestroyAllocation(allocation);
        }

        _retired.Clear();
        _cursor = 0;
    }

    internal void InvalidateDeviceLocalState()
    {
        _current = default;
        _retired.Clear();
        _cursor = 0;
    }

    internal void Dispose()
    {
        if (VulkanBufferAllocation.IsLive(in _current))
        {
            _session.DestroyAllocation(_current);
            _current = default;
        }

        foreach (var allocation in _retired)
        {
            _session.DestroyAllocation(allocation);
        }

        _retired.Clear();
        _cursor = 0;
    }

    private void EnsureCapacity(ulong required)
    {
        if (VulkanBufferAllocation.IsLive(in _current) && _current.AllocationSize >= required)
        {
            return;
        }

        if (VulkanBufferAllocation.IsLive(in _current))
        {
            _retired.Add(_current);
            _current = default;
        }

        var capacity = 4096UL;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        _current = _session.CreateNativeBuffer(
            capacity,
            BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
    }

    private static ulong Align(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);
}
