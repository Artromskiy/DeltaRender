/// <summary>
/// Renderer-owned persistent instance batcher. It applies producer changes to
/// ordered segments or unordered buckets, uploads only changed byte ranges and
/// records one instanced draw per active segment. It is neutral to ECS, XAML and
/// shader authoring; instance bytes and ABI metadata come from the caller.
/// </summary>
public sealed class RenderBatcher : IRenderFeature, IDisposable
{
    private readonly IRenderFrameSession _session;
    private readonly PixelExtent _viewport;
    private readonly RenderBatchOrderMode _orderMode;
    private readonly List<PipelineState> _pipelines = [];
    private readonly List<MaterialState> _materials = [];
    private readonly Dictionary<RenderBatchItemId, int> _itemLookup = [];
    private readonly List<RenderBatchSegment> _orderedSegments = [];
    private readonly Dictionary<RenderBatchKey, RenderBatchSegment> _unorderedSegments = [];
    private readonly List<RenderBatchSegment> _unorderedSegmentOrder = [];
    private readonly Stack<int> _freeItemIndices = [];
    private readonly List<string> _diagnostics = [];
    private ItemState[] _items = new ItemState[16];
    private ulong _nextPipelineValue = 1;
    private ulong _nextMaterialValue = 1;
    private bool _disposed;

    public RenderBatcher(
        IRenderFrameSession session,
        PixelExtent viewport,
        RenderBatchOrderMode orderMode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (viewport.IsEmpty)
        {
            throw new ArgumentException("The batch viewport must be non-empty.", nameof(viewport));
        }

        _session = session;
        _viewport = viewport;
        _orderMode = orderMode;
    }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public int ActiveItemCount { get; private set; }

    public int ActiveSegmentCount => _orderMode == RenderBatchOrderMode.Ordered
        ? _orderedSegments.Count
        : _unorderedSegmentOrder.Count;

    public RenderBatchPipelineHandle RegisterPipeline(
        RasterPipelineDescription pipeline,
        ShaderBinding instanceBinding,
        uint instanceStride,
        uint vertexCount = 6)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfZero(instanceStride);
        ArgumentOutOfRangeException.ThrowIfZero(vertexCount);

        var handle = new RenderBatchPipelineHandle(_nextPipelineValue++, 1);
        _pipelines.Add(new PipelineState(
            _session,
            handle,
            pipeline,
            instanceBinding,
            instanceStride,
            vertexCount,
            Math.Max(1UL, _session.Capabilities.MinStorageBufferOffsetAlignment)));
        return handle;
    }

    public RenderBatchMaterialHandle RegisterMaterial(ReadOnlySpan<byte> pushConstants)
    {
        ThrowIfDisposed();
        var handle = new RenderBatchMaterialHandle(_nextMaterialValue++, 1);
        _materials.Add(new MaterialState(handle, pushConstants));
        return handle;
    }

    public bool TryUpdateMaterial(
        RenderBatchMaterialHandle handle,
        RenderBatchVersion version,
        ReadOnlySpan<byte> pushConstants,
        out string diagnostic)
    {
        ThrowIfDisposed();
        diagnostic = string.Empty;
        if (!version.IsValid)
        {
            diagnostic = "Material version must be non-zero.";
            return false;
        }

        if (!TryGetMaterial(handle, out var material))
        {
            diagnostic = $"Unknown or stale material handle {handle}.";
            return false;
        }

        if (version.Value <= material.Version.Value)
        {
            diagnostic = $"Material update {version.Value} is not newer than {material.Version.Value}.";
            return false;
        }

        material.Version = version;
        material.PushConstants = pushConstants.ToArray();
        return true;
    }

    public bool TryApply(in RenderBatchItemChange change, out string diagnostic)
    {
        ThrowIfDisposed();
        diagnostic = string.Empty;
        if (!change.Id.IsValid)
        {
            diagnostic = "Render batch item identity is invalid.";
            return false;
        }

        if (!change.Version.IsValid)
        {
            diagnostic = "Render batch item version must be non-zero.";
            return false;
        }

        if (!change.Key.IsValid)
        {
            diagnostic = "Render batch key must contain valid pipeline, material and clip values.";
            return false;
        }

        if (!TryGetPipeline(change.Key.Pipeline, out var pipeline))
        {
            diagnostic = $"Unknown or stale pipeline handle {change.Key.Pipeline}.";
            return false;
        }

        if (!TryGetMaterial(change.Key.Material, out _))
        {
            diagnostic = $"Unknown or stale material handle {change.Key.Material}.";
            return false;
        }

        if (change.InstanceData.Length != pipeline.InstanceStride)
        {
            diagnostic = $"Item payload has {change.InstanceData.Length} bytes; pipeline requires {pipeline.InstanceStride}.";
            return false;
        }

        if (_itemLookup.TryGetValue(change.Id, out var itemIndex))
        {
            ref var existing = ref _items[itemIndex];
            if (!existing.Active && change.Id.Generation <= existing.Id.Generation)
            {
                diagnostic = $"Item handle {change.Id} is stale after removal.";
                return false;
            }

            if (change.Version.Value <= existing.Version.Value)
            {
                diagnostic = $"Item update {change.Version.Value} is not newer than {existing.Version.Value}.";
                return false;
            }

            if (existing.Active && existing.Key == change.Key &&
                (_orderMode == RenderBatchOrderMode.Unordered || existing.Order == change.Order))
            {
                existing.Version = change.Version;
                existing.Order = change.Order;
                GetSegment(existing).SetPayload(existing.Position, change.InstanceData);
                return true;
            }

            if (existing.Active)
            {
                RemoveItemFromSegment(itemIndex, ref existing);
            }

            existing.Id = change.Id;
            existing.Version = change.Version;
            existing.Order = change.Order;
            existing.Key = change.Key;
            existing.Active = true;
            InsertItem(itemIndex, change.InstanceData);
            ActiveItemCount++;
            return true;
        }

        itemIndex = AllocateItemIndex();
        _itemLookup.Add(change.Id, itemIndex);
        _items[itemIndex] = new ItemState
        {
            Id = change.Id,
            Version = change.Version,
            Order = change.Order,
            Key = change.Key,
            Active = true,
        };
        InsertItem(itemIndex, change.InstanceData);
        ActiveItemCount++;
        return true;
    }

    public bool TryRemove(
        RenderBatchItemId id,
        RenderBatchVersion version,
        out string diagnostic)
    {
        ThrowIfDisposed();
        diagnostic = string.Empty;
        if (!_itemLookup.TryGetValue(id, out var itemIndex))
        {
            diagnostic = $"Unknown item handle {id}.";
            return false;
        }

        ref var item = ref _items[itemIndex];
        if (!item.Active || id.Generation != item.Id.Generation)
        {
            diagnostic = $"Item handle {id} is stale.";
            return false;
        }

        if (!version.IsValid || version.Value <= item.Version.Value)
        {
            diagnostic = $"Removal version {version.Value} is not newer than {item.Version.Value}.";
            return false;
        }

        RemoveItemFromSegment(itemIndex, ref item);
        item.Version = version;
        item.Active = false;
        ActiveItemCount--;
        _freeItemIndices.Push(itemIndex);
        return true;
    }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (ActiveItemCount == 0)
        {
            return;
        }

        var target = _session.Target;
        if (!target.IsValid)
        {
            _diagnostics.Add("Render batcher requires a valid session target.");
            return;
        }

        var graphTarget = graph.ImportTarget(target);
        for (var index = 0; index < _pipelines.Count; index++)
        {
            var pipeline = _pipelines[index];
            if (!pipeline.HasActiveSegments)
            {
                continue;
            }

            pipeline.EnsureBuffer();
            pipeline.GraphBuffer = graph.ImportBuffer(pipeline.Buffer);
            if (pipeline.DirtyCount != 0)
            {
                var upload = graph.AddTransferPass(pipeline.UploadPassName, pipeline.UploadPass);
                graph.UseBuffer(upload, pipeline.GraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }
        }

        if (_orderMode == RenderBatchOrderMode.Ordered)
        {
            for (var index = 0; index < _orderedSegments.Count; index++)
            {
                AddRasterPass(graph, graphTarget, _orderedSegments[index]);
            }
        }
        else
        {
            for (var index = 0; index < _unorderedSegmentOrder.Count; index++)
            {
                AddRasterPass(graph, graphTarget, _unorderedSegmentOrder[index]);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (var index = 0; index < _pipelines.Count; index++)
        {
            _pipelines[index].Dispose();
        }

        _pipelines.Clear();
        _materials.Clear();
        _orderedSegments.Clear();
        _unorderedSegments.Clear();
        _unorderedSegmentOrder.Clear();
        _itemLookup.Clear();
        _diagnostics.Clear();
        _disposed = true;
    }

    private void AddRasterPass(
        IRenderGraphBuilder graph,
        RenderGraphTextureHandle target,
        RenderBatchSegment segment)
    {
        var pass = graph.AddRasterPass(segment.Description, segment.RasterPass);
        graph.UseColorAttachment(
            pass,
            0,
            new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
        graph.UseBuffer(
            pass,
            segment.Pipeline.GraphBuffer,
            RenderResourceAccess.Read,
            RenderPipelineStages.Vertex);
    }

    private void InsertItem(int itemIndex, ReadOnlySpan<byte> payload)
    {
        if (_orderMode == RenderBatchOrderMode.Unordered)
        {
            InsertUnordered(itemIndex, payload);
            return;
        }

        InsertOrdered(itemIndex, payload);
    }

    private void InsertUnordered(int itemIndex, ReadOnlySpan<byte> payload)
    {
        ref var item = ref _items[itemIndex];
        if (_unorderedSegments.TryGetValue(item.Key, out var segment))
        {
            segment.Append(itemIndex, payload);
        }
        else
        {
            segment = CreateSegment(item.Key);
            _unorderedSegments.Add(item.Key, segment);
            _unorderedSegmentOrder.Add(segment);
            segment.Append(itemIndex, payload);
        }

        item.Segment = segment;
        item.Position = segment.Count - 1;
    }

    private void InsertOrdered(int itemIndex, ReadOnlySpan<byte> payload)
    {
        ref var item = ref _items[itemIndex];
        for (var segmentIndex = 0; segmentIndex < _orderedSegments.Count; segmentIndex++)
        {
            var segment = _orderedSegments[segmentIndex];
            var first = _items[segment.ItemIndices[0]];
            var last = _items[segment.ItemIndices[segment.Count - 1]];
            if (Compare(item.Order, item.Id, first.Order, first.Id) < 0)
            {
                if (segment.Key == item.Key)
                {
                    segment.Insert(0, itemIndex, payload);
                    RefreshPositions(segment);
                    MergeAdjacentOrderedSegments(segmentIndex);
                    return;
                }

                InsertNewOrderedSegment(segmentIndex, itemIndex, payload);
                return;
            }

            if (Compare(item.Order, item.Id, last.Order, last.Id) > 0)
            {
                if (segment.Key == item.Key &&
                    (segmentIndex == _orderedSegments.Count - 1 ||
                     Compare(item.Order, item.Id, _items[_orderedSegments[segmentIndex + 1].ItemIndices[0]].Order, _items[_orderedSegments[segmentIndex + 1].ItemIndices[0]].Id) < 0))
                {
                    segment.Append(itemIndex, payload);
                    item.Segment = segment;
                    item.Position = segment.Count - 1;
                    return;
                }

                continue;
            }

            var position = 0;
            while (position < segment.Count &&
                   Compare(_items[segment.ItemIndices[position]].Order, _items[segment.ItemIndices[position]].Id, item.Order, item.Id) < 0)
            {
                position++;
            }

            if (segment.Key == item.Key)
            {
                segment.Insert(position, itemIndex, payload);
                RefreshPositions(segment);
                return;
            }

            if (position == 0)
            {
                InsertNewOrderedSegment(segmentIndex, itemIndex, payload);
                return;
            }

            if (position == segment.Count)
            {
                InsertNewOrderedSegment(segmentIndex + 1, itemIndex, payload);
                return;
            }

            var right = segment.Split(position);
            _orderedSegments.Insert(segmentIndex + 1, right);
            InsertNewOrderedSegment(segmentIndex + 1, itemIndex, payload);
            return;
        }

        InsertNewOrderedSegment(_orderedSegments.Count, itemIndex, payload);
    }

    private void InsertNewOrderedSegment(int segmentIndex, int itemIndex, ReadOnlySpan<byte> payload)
    {
        var segment = CreateSegment(_items[itemIndex].Key);
        segment.Append(itemIndex, payload);
        _orderedSegments.Insert(segmentIndex, segment);
        ref var item = ref _items[itemIndex];
        item.Segment = segment;
        item.Position = 0;
    }

    private void RemoveItemFromSegment(int itemIndex, ref ItemState item)
    {
        var segment = GetSegment(item);
        if (_orderMode == RenderBatchOrderMode.Unordered)
        {
            var lastPosition = segment.Count - 1;
            if (item.Position != lastPosition)
            {
                var movedItemIndex = segment.ItemIndices[lastPosition];
                segment.CopySlot(lastPosition, item.Position);
                ref var movedItem = ref _items[movedItemIndex];
                movedItem.Position = item.Position;
                movedItem.Segment = segment;
            }

            segment.Count--;
            if (item.Position < segment.Count)
            {
                segment.MarkDirty(item.Position * checked((int)segment.Pipeline.InstanceStride), checked((int)segment.Pipeline.InstanceStride));
            }
            if (segment.Count == 0)
            {
                _unorderedSegments.Remove(segment.Key);
                _unorderedSegmentOrder.Remove(segment);
                segment.Pipeline.ClearDirty(segment);
                segment.Pipeline.RemoveSegment(segment);
            }
            return;
        }

        var segmentIndex = _orderedSegments.IndexOf(segment);
        segment.RemoveAt(item.Position);
        RefreshPositions(segment);
        if (segment.Count == 0)
        {
            _orderedSegments.RemoveAt(segmentIndex);
            segment.Pipeline.ClearDirty(segment);
            segment.Pipeline.RemoveSegment(segment);
            return;
        }

        MergeAdjacentOrderedSegments(segmentIndex);
    }

    private void MergeAdjacentOrderedSegments(int segmentIndex)
    {
        if (segmentIndex > 0 && _orderedSegments[segmentIndex - 1].Key == _orderedSegments[segmentIndex].Key)
        {
            var previous = _orderedSegments[segmentIndex - 1];
            var current = _orderedSegments[segmentIndex];
            previous.AppendSegment(current);
            RefreshPositions(previous);
            current.Pipeline.ClearDirty(current);
            current.Pipeline.RemoveSegment(current);
            _orderedSegments.RemoveAt(segmentIndex);
            segmentIndex--;
        }

        if (segmentIndex + 1 < _orderedSegments.Count && _orderedSegments[segmentIndex].Key == _orderedSegments[segmentIndex + 1].Key)
        {
            var current = _orderedSegments[segmentIndex];
            var next = _orderedSegments[segmentIndex + 1];
            current.AppendSegment(next);
            RefreshPositions(current);
            next.Pipeline.ClearDirty(next);
            next.Pipeline.RemoveSegment(next);
            _orderedSegments.RemoveAt(segmentIndex + 1);
        }
    }

    private RenderBatchSegment CreateSegment(RenderBatchKey key)
    {
        if (!TryGetPipeline(key.Pipeline, out var pipeline))
        {
            throw new InvalidOperationException($"Pipeline {key.Pipeline} disappeared during batch update.");
        }

        return new RenderBatchSegment(this, pipeline, key, _viewport);
    }

    private void RefreshPositions(RenderBatchSegment segment)
    {
        for (var position = 0; position < segment.Count; position++)
        {
            ref var item = ref _items[segment.ItemIndices[position]];
            item.Segment = segment;
            item.Position = position;
        }
    }

    private int AllocateItemIndex()
    {
        if (_freeItemIndices.Count != 0)
        {
            return _freeItemIndices.Pop();
        }

        var index = _itemLookup.Count;
        if (index >= _items.Length)
        {
            Array.Resize(ref _items, checked(_items.Length * 2));
        }

        return index;
    }

    private bool TryGetPipeline(RenderBatchPipelineHandle handle, [NotNullWhen(true)] out PipelineState? pipeline)
    {
        var index = handle.Value == 0 || handle.Value > (ulong)_pipelines.Count ? -1 : checked((int)handle.Value - 1);
        if (index >= 0 && _pipelines[index].Handle.Generation == handle.Generation)
        {
            pipeline = _pipelines[index];
            return true;
        }

        pipeline = null;
        return false;
    }

    private bool TryGetMaterial(RenderBatchMaterialHandle handle, [NotNullWhen(true)] out MaterialState? material)
    {
        var index = handle.Value == 0 || handle.Value > (ulong)_materials.Count ? -1 : checked((int)handle.Value - 1);
        if (index >= 0 && _materials[index].Handle.Generation == handle.Generation)
        {
            material = _materials[index];
            return true;
        }

        material = null;
        return false;
    }

    private static int Compare(
        RenderBatchOrderKey left,
        RenderBatchItemId leftId,
        RenderBatchOrderKey right,
        RenderBatchItemId rightId)
    {
        var z = left.ZIndex.CompareTo(right.ZIndex);
        if (z != 0)
        {
            return z;
        }

        var sequence = left.StableSequence.CompareTo(right.StableSequence);
        return sequence != 0 ? sequence : leftId.Value.CompareTo(rightId.Value);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private struct ItemState
    {
        public RenderBatchItemId Id;
        public RenderBatchVersion Version;
        public RenderBatchOrderKey Order;
        public RenderBatchKey Key;
        public RenderBatchSegment? Segment;
        public int Position;
        public bool Active;
    }

    private sealed class MaterialState
    {
        public MaterialState(RenderBatchMaterialHandle handle, ReadOnlySpan<byte> pushConstants)
        {
            Handle = handle;
            Version = new RenderBatchVersion(1);
            PushConstants = pushConstants.ToArray();
        }

        public RenderBatchMaterialHandle Handle;
        public RenderBatchVersion Version;
        public byte[] PushConstants;
    }

    private sealed class PipelineState : IDisposable
    {
        private readonly IRenderFrameSession _session;
        private readonly ulong _alignment;
        private readonly List<DirtyRange> _dirty = [];
        private readonly List<RenderBufferHandle> _retiredBuffers = [];
        private ulong _nextOffset;
        private ulong _bufferCapacity;

        public PipelineState(
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
            UploadPass = new PipelineUploadPass(this);
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
            _dirty.Clear();
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
            for (var index = 0; index < _dirty.Count; index++)
            {
                var existing = _dirty[index];
                if (existing.Segment != segment || end < existing.Offset || offset > existing.End)
                {
                    continue;
                }

                existing.Offset = Math.Min(existing.Offset, offset);
                existing.End = Math.Max(existing.End, end);
                _dirty[index] = existing;
                return;
            }

            _dirty.Add(new DirtyRange(segment, offset, length));
        }

        public void ClearDirty(RenderBatchSegment segment)
        {
            for (var index = _dirty.Count - 1; index >= 0; index--)
            {
                if (_dirty[index].Segment == segment)
                {
                    _dirty.RemoveAt(index);
                }
            }
        }

        public void ClearDirty() => _dirty.Clear();

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

        private sealed class PipelineUploadPass(PipelineState pipeline) : ITransferPass
        {
            public void Record(ITransferCommandContext commands)
            {
                for (var index = 0; index < pipeline._dirty.Count; index++)
                {
                    var range = pipeline._dirty[index];
                    commands.UploadBuffer(
                        pipeline.GraphBuffer,
                        range.Segment.Packed.AsSpan(range.Offset, range.Length),
                        checked(range.Segment.GpuOffset + (ulong)range.Offset));
                }

                pipeline.ClearDirty();
            }
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

    private sealed class RenderBatchSegment
    {
        private readonly RenderBatcher _owner;
        private readonly PixelExtent _viewport;
        private int _capacity;

        public RenderBatchSegment(
            RenderBatcher owner,
            PipelineState pipeline,
            RenderBatchKey key,
            PixelExtent viewport)
        {
            _owner = owner;
            _viewport = viewport;
            Pipeline = pipeline;
            Key = key;
            _capacity = 1;
            ItemIndices = new int[1];
            Packed = new byte[checked((int)pipeline.InstanceStride)];
            GpuOffset = pipeline.Allocate(_capacity);
            Description = new RasterPassDescription(
                $"Delta.Render.Batch[{key.Pipeline.Value}:{key.Material.Value}]",
                pipeline.Description);
            RasterPass = new SegmentRasterPass(this, viewport);
            pipeline.AddSegment(this);
        }

        public RenderBatchKey Key { get; }

        public PipelineState Pipeline { get; }

        public int[] ItemIndices { get; private set; }

        public byte[] Packed { get; private set; }

        public ulong GpuOffset { get; private set; }

        public int Count { get; set; }

        public RasterPassDescription Description { get; }

        public IRasterPass RasterPass { get; }

        public void Append(int itemIndex, ReadOnlySpan<byte> payload)
        {
            EnsureCapacity(Count + 1);
            ItemIndices[Count] = itemIndex;
            payload.CopyTo(Packed.AsSpan(Count * checked((int)Pipeline.InstanceStride)));
            Count++;
            MarkDirty((Count - 1) * checked((int)Pipeline.InstanceStride), checked((int)Pipeline.InstanceStride));
        }

        public void Insert(int position, int itemIndex, ReadOnlySpan<byte> payload)
        {
            EnsureCapacity(Count + 1);
            var stride = checked((int)Pipeline.InstanceStride);
            for (var index = Count; index > position; index--)
            {
                ItemIndices[index] = ItemIndices[index - 1];
                Packed.AsSpan((index - 1) * stride, stride).CopyTo(Packed.AsSpan(index * stride, stride));
            }

            ItemIndices[position] = itemIndex;
            payload.CopyTo(Packed.AsSpan(position * stride, stride));
            Count++;
            MarkDirty(position * stride, checked((Count - position) * stride));
        }

        public RenderBatchSegment Split(int position)
        {
            var right = new RenderBatchSegment(_owner, Pipeline, Key, _viewport);
            var stride = checked((int)Pipeline.InstanceStride);
            right.EnsureCapacity(Count - position);
            for (var index = position; index < Count; index++)
            {
                var rightIndex = index - position;
                right.ItemIndices[rightIndex] = ItemIndices[index];
                Packed.AsSpan(index * stride, stride).CopyTo(right.Packed.AsSpan(rightIndex * stride, stride));
            }

            right.Count = Count - position;
            right.MarkDirty(0, checked(right.Count * stride));
            Count = position;
            Pipeline.ClearDirty(this);
            return right;
        }

        public void RemoveAt(int position)
        {
            var stride = checked((int)Pipeline.InstanceStride);
            for (var index = position; index < Count - 1; index++)
            {
                ItemIndices[index] = ItemIndices[index + 1];
                Packed.AsSpan((index + 1) * stride, stride).CopyTo(Packed.AsSpan(index * stride, stride));
            }

            Count--;
            MarkDirty(position * stride, checked((Count - position) * stride));
        }

        public void CopySlot(int sourcePosition, int destinationPosition)
        {
            var stride = checked((int)Pipeline.InstanceStride);
            ItemIndices[destinationPosition] = ItemIndices[sourcePosition];
            Packed.AsSpan(sourcePosition * stride, stride).CopyTo(Packed.AsSpan(destinationPosition * stride, stride));
            MarkDirty(destinationPosition * stride, stride);
        }

        public void AppendSegment(RenderBatchSegment other)
        {
            EnsureCapacity(Count + other.Count);
            var stride = checked((int)Pipeline.InstanceStride);
            for (var index = 0; index < other.Count; index++)
            {
                ItemIndices[Count + index] = other.ItemIndices[index];
                other.Packed.AsSpan(index * stride, stride).CopyTo(Packed.AsSpan((Count + index) * stride, stride));
            }

            var oldCount = Count;
            Count += other.Count;
            MarkDirty(oldCount * stride, checked(other.Count * stride));
        }

        public void SetPayload(int position, ReadOnlySpan<byte> payload)
        {
            var stride = checked((int)Pipeline.InstanceStride);
            var destination = Packed.AsSpan(position * stride, stride);
            if (destination.SequenceEqual(payload))
            {
                return;
            }

            payload.CopyTo(destination);
            MarkDirty(position * stride, stride);
        }

        public void MarkDirty(int offset, int length) => Pipeline.AddDirty(this, offset, length);

        private void EnsureCapacity(int required)
        {
            if (required <= _capacity)
            {
                return;
            }

            var next = _capacity;
            while (next < required)
            {
                next = checked(next * 2);
            }

            var oldPacked = Packed;
            var oldItems = ItemIndices;
            _capacity = next;
            ItemIndices = new int[next];
            Packed = new byte[checked(next * (int)Pipeline.InstanceStride)];
            oldItems.AsSpan(0, Count).CopyTo(ItemIndices);
            oldPacked.AsSpan(0, checked(Count * (int)Pipeline.InstanceStride)).CopyTo(Packed);
            GpuOffset = Pipeline.Allocate(next);
            Pipeline.ClearDirty(this);
            MarkDirty(0, checked(Count * (int)Pipeline.InstanceStride));
        }

        private sealed class SegmentRasterPass(RenderBatchSegment segment, PixelExtent viewport) : IRasterPass
        {
            public void Record(IRasterCommandContext commands)
            {
                commands.SetViewport(new RenderViewport(0, 0, viewport.Width, viewport.Height));
                commands.SetScissor(segment.Key.Clip);
                var material = segment._owner.GetMaterial(segment.Key.Material);
                if (material.PushConstants.Length != 0)
                {
                    commands.PushConstants(material.PushConstants);
                }

                commands.BindBuffer(
                    segment.Pipeline.InstanceBinding,
                    segment.Pipeline.GraphBuffer,
                    segment.GpuOffset,
                    checked((ulong)segment.Count * segment.Pipeline.InstanceStride));
                commands.Draw(segment.Pipeline.VertexCount, checked((uint)segment.Count));
            }
        }
    }

    private static RenderBatchSegment GetSegment(in ItemState item)
        => item.Segment ?? throw new InvalidOperationException("An active render batch item has no segment.");

    private MaterialState GetMaterial(RenderBatchMaterialHandle handle)
    {
        if (TryGetMaterial(handle, out var material))
        {
            return material;
        }

        throw new InvalidOperationException($"Material {handle} disappeared during graph recording.");
    }
}
