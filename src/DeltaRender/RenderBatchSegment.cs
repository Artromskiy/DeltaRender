namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchSegment
{
    private readonly PixelExtent _viewport;
    private readonly int _stride;
    private int _capacity;

    public RenderBatchSegment(
        RenderBatchPipelineState pipeline,
        RenderBatchMaterialState material,
        RenderBatchKey key,
        PixelExtent viewport)
    {
        _viewport = viewport;
        Material = material;
        Pipeline = pipeline;
        Key = key;
        _stride = checked((int)pipeline.InstanceStride);
        _capacity = 1;
        ItemIndices = new int[1];
        Packed = new byte[_stride];
        GpuOffset = pipeline.Allocate(_capacity);
        Description = new RasterPassDescription(
            $"Delta.Render.Batch[{key.Pipeline.Value}:{key.Material.Value}]",
            pipeline.Description);
        RasterPass = new RenderBatchSegmentRasterPass(this, viewport);
        pipeline.AddSegment(this);
    }

    public RenderBatchKey Key { get; }

    public RenderBatchPipelineState Pipeline { get; }

    public RenderBatchMaterialState Material { get; }

    public int[] ItemIndices { get; private set; }

    public byte[] Packed { get; private set; }

    public ulong GpuOffset { get; private set; }

    public int Count { get; set; }

    public RasterPassDescription Description { get; }

    public IRasterPass RasterPass { get; }

    public void Append(int itemIndex, ReadOnlySpan<byte> payload)
    {
        EnsureCapacity(Count + 1);
        ItemIndices.RefAt(Count) = itemIndex;
        payload.CopyTo(Packed.AsSpan(Count * _stride));
        Count++;
        MarkDirty((Count - 1) * _stride, _stride);
    }

    public void Insert(int position, int itemIndex, ReadOnlySpan<byte> payload)
    {
        EnsureCapacity(Count + 1);
        var movedCount = Count - position;
        if (movedCount > 0)
        {
            ItemIndices.AsSpan(position, movedCount).CopyTo(ItemIndices.AsSpan(position + 1, movedCount));
            var movedBytes = checked(movedCount * _stride);
            var sourceOffset = checked(position * _stride);
            var destinationOffset = checked((position + 1) * _stride);
            Packed.AsSpan(sourceOffset, movedBytes).CopyTo(Packed.AsSpan(destinationOffset, movedBytes));
        }

        ItemIndices.RefAt(position) = itemIndex;
        payload.CopyTo(Packed.AsSpan(position * _stride, _stride));
        Count++;
        MarkDirty(position * _stride, checked((Count - position) * _stride));
    }

    public RenderBatchSegment Split(int position)
    {
        var right = new RenderBatchSegment(Pipeline, Material, Key, _viewport);
        var rightCount = Count - position;
        right.EnsureCapacity(rightCount);
        ItemIndices.AsSpan(position, rightCount).CopyTo(right.ItemIndices.AsSpan(0, rightCount));
        var rightBytes = checked(rightCount * _stride);
        Packed.AsSpan(checked(position * _stride), rightBytes).CopyTo(right.Packed.AsSpan(0, rightBytes));

        right.Count = rightCount;
        right.MarkDirty(0, checked(right.Count * _stride));
        Count = position;
        Pipeline.ClearDirty(this);
        return right;
    }

    public void RemoveAt(int position)
    {
        var movedCount = Count - position - 1;
        if (movedCount > 0)
        {
            ItemIndices.AsSpan(position + 1, movedCount).CopyTo(ItemIndices.AsSpan(position, movedCount));
            var movedBytes = checked(movedCount * _stride);
            Packed.AsSpan(checked((position + 1) * _stride), movedBytes).CopyTo(Packed.AsSpan(position * _stride, movedBytes));
        }

        Count--;
        MarkDirty(position * _stride, checked((Count - position) * _stride));
    }

    public void CopySlot(int sourcePosition, int destinationPosition)
    {
        ItemIndices.RefAt(destinationPosition) = ItemIndices.RefAt(sourcePosition);
        Packed.AsSpan(sourcePosition * _stride, _stride).CopyTo(Packed.AsSpan(destinationPosition * _stride, _stride));
        MarkDirty(destinationPosition * _stride, _stride);
    }

    public void AppendSegment(RenderBatchSegment other)
    {
        EnsureCapacity(Count + other.Count);
        var oldCount = Count;
        other.ItemIndices.AsSpan(0, other.Count).CopyTo(ItemIndices.AsSpan(oldCount, other.Count));
        var appendedBytes = checked(other.Count * _stride);
        other.Packed.AsSpan(0, appendedBytes).CopyTo(Packed.AsSpan(checked(oldCount * _stride), appendedBytes));
        Count += other.Count;
        MarkDirty(oldCount * _stride, checked(other.Count * _stride));
    }

    public void SetPayload(int position, ReadOnlySpan<byte> payload)
    {
        var destination = Packed.AsSpan(position * _stride, _stride);
        if (destination.SequenceEqual(payload))
        {
            return;
        }

        payload.CopyTo(destination);
        MarkDirty(position * _stride, _stride);
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
        Packed = new byte[checked(next * _stride)];
        oldItems.AsSpan(0, Count).CopyTo(ItemIndices);
        oldPacked.AsSpan(0, checked(Count * _stride)).CopyTo(Packed);
        GpuOffset = Pipeline.Allocate(next);
        Pipeline.ClearDirty(this);
        MarkDirty(0, checked(Count * _stride));
    }

}
