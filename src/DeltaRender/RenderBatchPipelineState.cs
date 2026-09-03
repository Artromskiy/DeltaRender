using Delta.Maths;
using Delta.Shader.Contract;

namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchPipelineState : IDisposable
{
    private readonly IRenderFrameSession _session;
    private readonly ulong _alignment;
    private readonly List<DirtyRange> _dirty = [];
    private readonly Dictionary<RenderBatchSegment, int> _dirtyIndices = [];
    private readonly List<RenderBufferHandle> _retiredBuffers = [];
    private ulong _nextOffset;
    private ulong _bufferCapacity;

    public RenderBatchPipelineState(
        IRenderFrameSession session,
        RenderBatchPipelineHandle handle,
        RasterPipelineDescription description,
        ShaderBinding instanceBinding,
        uint instanceStride,
        uint vertexCount,
        ulong alignment)
    {
        _session = session;
        Handle = handle;
        Description = description;
        InstanceBinding = instanceBinding;
        InstanceStride = instanceStride;
        VertexCount = vertexCount;
        _alignment = alignment;
        UploadPassName = $"Delta.Render.Batch.Upload[{handle.Value}]";
        UploadPass = new RenderBatchPipelineUploadPass(this);
    }

    public RenderBatchPipelineHandle Handle { get; }

    public RasterPipelineDescription Description { get; }

    public ShaderBinding InstanceBinding { get; }

    public uint InstanceStride { get; }

    public uint VertexCount { get; }

    public RenderBufferHandle Buffer { get; private set; }

    public RenderGraphBufferHandle GraphBuffer { get; set; }

    public string UploadPassName { get; }

    public ITransferPass UploadPass { get; }

    public int DirtyCount => _dirty.Count;

    public bool HasActiveSegments => _segments.Count != 0;

    private readonly List<RenderBatchSegment> _segments = [];

    public ulong Allocate(int capacity)
    {
        var bytes = checked((ulong)capacity * InstanceStride);
        var offset = Align(_nextOffset, _alignment);
        _nextOffset = checked(offset + bytes);
        return offset;
    }

    public void AddSegment(RenderBatchSegment segment) => _segments.Add(segment);

    public void RemoveSegment(RenderBatchSegment segment) => _segments.Remove(segment);

    public void EnsureBuffer()
    {
        if (_nextOffset == 0)
        {
            return;
        }

        if (Buffer.IsValid && _bufferCapacity >= _nextOffset)
        {
            return;
        }

        var capacity = _bufferCapacity == 0 ? 256UL : _bufferCapacity;
        while (capacity < _nextOffset)
        {
            capacity = checked(capacity * 2);
        }

        var replacement = _session.CreateBuffer(new RenderBufferDescription(
            capacity,
            RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
        if (Buffer.IsValid)
        {
            _retiredBuffers.Add(Buffer);
        }

        Buffer = replacement;
        _bufferCapacity = capacity;
        ClearDirty();
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            AddDirty(segment, 0, checked(segment.Count * (int)InstanceStride));
        }
    }

    public void AddDirty(RenderBatchSegment segment, int offset, int length)
    {
        if (length <= 0)
        {
            return;
        }

        var end = checked(offset + length);
        if (_dirtyIndices.TryGetValue(segment, out var candidateIndex))
        {
            if (TryMergeDirty(candidateIndex, segment, offset, end))
            {
                return;
            }

            _dirtyIndices.Remove(segment);
        }

        for (var index = _dirty.Count - 1; index >= 0; index--)
        {
            if (!TryMergeDirty(index, segment, offset, end))
            {
                continue;
            }

            _dirtyIndices[segment] = index;
            return;
        }

        _dirtyIndices[segment] = _dirty.Count;
        _dirty.Add(new DirtyRange(segment, offset, length));
    }

    public void ClearDirty(RenderBatchSegment segment)
    {
        var removed = false;
        for (var index = _dirty.Count - 1; index >= 0; index--)
        {
            if (_dirty[index].Segment == segment)
            {
                _dirty.RemoveAt(index);
                removed = true;
            }
        }

        _dirtyIndices.Remove(segment);
        if (removed)
        {
            RebuildDirtyIndices();
        }
    }

    public void ClearDirty()
    {
        _dirty.Clear();
        _dirtyIndices.Clear();
    }

    public void Dispose()
    {
        if (Buffer.IsValid)
        {
            _session.Release(Buffer);
            Buffer = default;
        }

        for (var index = 0; index < _retiredBuffers.Count; index++)
        {
            if (_retiredBuffers[index].IsValid)
            {
                _session.Release(_retiredBuffers[index]);
            }
        }

        _retiredBuffers.Clear();
        _dirty.Clear();
    }

    private static ulong Align(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private bool TryMergeDirty(int index, RenderBatchSegment segment, int offset, int end)
    {
        if (index < 0 || index >= _dirty.Count)
        {
            return false;
        }

        var existing = _dirty[index];
        if (existing.Segment != segment || end < existing.Offset || offset > existing.End)
        {
            return false;
        }

        existing.Offset = DeltaMaths.Min(existing.Offset, offset);
        existing.End = DeltaMaths.Max(existing.End, end);
        _dirty[index] = existing;
        return true;
    }

    private void RebuildDirtyIndices()
    {
        _dirtyIndices.Clear();
        for (var index = 0; index < _dirty.Count; index++)
        {
            _dirtyIndices[_dirty[index].Segment] = index;
        }
    }

    internal void UploadDirtyRanges(ITransferCommandContext commands)
    {
        for (var index = 0; index < _dirty.Count; index++)
        {
            var range = _dirty[index];
            commands.UploadBuffer(
                GraphBuffer,
                range.Segment.Packed.AsSpan(range.Offset, range.Length),
                checked(range.Segment.GpuOffset + (ulong)range.Offset));
        }

        ClearDirty();
    }

    internal struct DirtyRange
    {
        public DirtyRange(RenderBatchSegment segment, int offset, int length)
        {
            Segment = segment;
            Offset = offset;
            Length = length;
        }

        public RenderBatchSegment Segment;

        public int Offset;

        public int Length;

        public int End
        {
            readonly get => checked(Offset + Length);
            set => Length = checked(value - Offset);
        }
    }
}
