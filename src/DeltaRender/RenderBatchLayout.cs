using System.Diagnostics.CodeAnalysis;

namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchLayout
{
    private readonly RenderBatchResourceRegistry _resources;
    private readonly PixelExtent _viewport;
    private readonly RenderBatchOrderMode _orderMode;
    private readonly Dictionary<RenderBatchItemId, int> _itemLookup = [];
    private readonly List<RenderBatchSegment> _orderedSegments = [];
    private readonly Dictionary<RenderBatchKey, RenderBatchSegment> _unorderedSegments = [];
    private readonly List<RenderBatchSegment> _unorderedSegmentOrder = [];
    private readonly Stack<int> _freeItemIndices = [];
    private ItemState[] _items = new ItemState[16];

    internal RenderBatchLayout(
        RenderBatchResourceRegistry resources,
        PixelExtent viewport,
        RenderBatchOrderMode orderMode)
    {
        _resources = resources;
        _viewport = viewport;
        _orderMode = orderMode;
    }

    internal RenderBatchOrderMode OrderMode => _orderMode;

    internal int ActiveItemCount { get; private set; }

    internal int ActiveSegmentCount => _orderMode == RenderBatchOrderMode.Ordered
        ? _orderedSegments.Count
        : _unorderedSegmentOrder.Count;

    internal int OrderedSegmentCount => _orderedSegments.Count;

    internal int UnorderedSegmentCount => _unorderedSegmentOrder.Count;

    internal RenderBatchSegment GetOrderedSegment(int index) => _orderedSegments[index];

    internal RenderBatchSegment GetUnorderedSegment(int index) => _unorderedSegmentOrder[index];

    internal bool TryApply(in RenderBatchItemChange change, out string diagnostic)
    {
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

    internal bool TryRemove(
        RenderBatchItemId id,
        RenderBatchVersion version,
        out string diagnostic)
    {
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
        var segmentIndex = FindFirstOrderedSegment(item.Order, item.Id);
        if (segmentIndex == _orderedSegments.Count)
        {
            if (segmentIndex > 0 && _orderedSegments[segmentIndex - 1].Key == item.Key)
            {
                var segment = _orderedSegments[segmentIndex - 1];
                segment.Append(itemIndex, payload);
                item.Segment = segment;
                item.Position = segment.Count - 1;
                return;
            }

            InsertNewOrderedSegment(segmentIndex, itemIndex, payload);
            return;
        }

        var candidate = _orderedSegments[segmentIndex];
        var first = _items[candidate.ItemIndices[0]];
        if (Compare(item.Order, item.Id, first.Order, first.Id) < 0)
        {
            if (segmentIndex > 0 && _orderedSegments[segmentIndex - 1].Key == item.Key)
            {
                var previous = _orderedSegments[segmentIndex - 1];
                var previousLast = _items[previous.ItemIndices[previous.Count - 1]];
                if (Compare(previousLast.Order, previousLast.Id, item.Order, item.Id) < 0)
                {
                    previous.Append(itemIndex, payload);
                    item.Segment = previous;
                    item.Position = previous.Count - 1;
                    return;
                }
            }

            if (candidate.Key == item.Key)
            {
                candidate.Insert(0, itemIndex, payload);
                RefreshPositions(candidate);
                MergeAdjacentOrderedSegments(segmentIndex);
                return;
            }

            InsertNewOrderedSegment(segmentIndex, itemIndex, payload);
            return;
        }

        var position = 0;
        while (position < candidate.Count &&
               Compare(_items[candidate.ItemIndices[position]].Order, _items[candidate.ItemIndices[position]].Id, item.Order, item.Id) < 0)
        {
            position++;
        }

        if (candidate.Key == item.Key)
        {
            candidate.Insert(position, itemIndex, payload);
            RefreshPositions(candidate);
            return;
        }

        if (position == 0)
        {
            InsertNewOrderedSegment(segmentIndex, itemIndex, payload);
            return;
        }

        if (position == candidate.Count)
        {
            InsertNewOrderedSegment(segmentIndex + 1, itemIndex, payload);
            return;
        }

        var right = candidate.Split(position);
        _orderedSegments.Insert(segmentIndex + 1, right);
        InsertNewOrderedSegment(segmentIndex + 1, itemIndex, payload);
    }

    private int FindFirstOrderedSegment(RenderBatchOrderKey order, RenderBatchItemId id)
    {
        var low = 0;
        var high = _orderedSegments.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var segment = _orderedSegments[middle];
            var last = _items[segment.ItemIndices[segment.Count - 1]];
            if (Compare(order, id, last.Order, last.Id) <= 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
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


    private RenderBatchSegment CreateSegment(RenderBatchKey key)
    {
        if (!TryGetPipeline(key.Pipeline, out var pipeline))
        {
            throw new InvalidOperationException($"Pipeline {key.Pipeline} disappeared during batch update.");
        }

        if (!TryGetMaterial(key.Material, out var material))
        {
            throw new InvalidOperationException($"Material {key.Material} disappeared during batch update.");
        }

        return new RenderBatchSegment(pipeline, material, key, _viewport);
    }

    private bool TryGetPipeline(
        RenderBatchPipelineHandle handle,
        [NotNullWhen(true)] out RenderBatchPipelineState? pipeline)
        => _resources.TryGetPipeline(handle, out pipeline);

    private bool TryGetMaterial(
        RenderBatchMaterialHandle handle,
        [NotNullWhen(true)] out RenderBatchMaterialState? material)
        => _resources.TryGetMaterial(handle, out material);

    private static RenderBatchSegment GetSegment(in ItemState item)
        => item.Segment ?? throw new InvalidOperationException("An active render batch item has no segment.");

    internal void Clear()
    {
        for (var index = 0; index < _orderedSegments.Count; index++)
        {
            var segment = _orderedSegments[index];
            segment.Pipeline.ClearDirty(segment);
            segment.Pipeline.RemoveSegment(segment);
        }

        for (var index = 0; index < _unorderedSegmentOrder.Count; index++)
        {
            var segment = _unorderedSegmentOrder[index];
            segment.Pipeline.ClearDirty(segment);
            segment.Pipeline.RemoveSegment(segment);
        }

        _orderedSegments.Clear();
        _unorderedSegments.Clear();
        _unorderedSegmentOrder.Clear();
        _itemLookup.Clear();
        _freeItemIndices.Clear();
        ActiveItemCount = 0;
    }

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
}
