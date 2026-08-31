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
        RasterPass = new SegmentRasterPass(this, viewport);
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
        ItemIndices[Count] = itemIndex;
        payload.CopyTo(Packed.AsSpan(Count * _stride));
        Count++;
        MarkDirty((Count - 1) * _stride, _stride);
    }

    public void Insert(int position, int itemIndex, ReadOnlySpan<byte> payload)
    {
        EnsureCapacity(Count + 1);
        for (var index = Count; index > position; index--)
        {
            ItemIndices[index] = ItemIndices[index - 1];
            Packed.AsSpan((index - 1) * _stride, _stride).CopyTo(Packed.AsSpan(index * _stride, _stride));
        }

        ItemIndices[position] = itemIndex;
        payload.CopyTo(Packed.AsSpan(position * _stride, _stride));
        Count++;
        MarkDirty(position * _stride, checked((Count - position) * _stride));
    }

    public RenderBatchSegment Split(int position)
    {
        var right = new RenderBatchSegment(Pipeline, Material, Key, _viewport);
        right.EnsureCapacity(Count - position);
        for (var index = position; index < Count; index++)
        {
            var rightIndex = index - position;
            right.ItemIndices[rightIndex] = ItemIndices[index];
            Packed.AsSpan(index * _stride, _stride).CopyTo(right.Packed.AsSpan(rightIndex * _stride, _stride));
        }

        right.Count = Count - position;
        right.MarkDirty(0, checked(right.Count * _stride));
        Count = position;
        Pipeline.ClearDirty(this);
        return right;
    }

    public void RemoveAt(int position)
    {
        for (var index = position; index < Count - 1; index++)
        {
            ItemIndices[index] = ItemIndices[index + 1];
            Packed.AsSpan((index + 1) * _stride, _stride).CopyTo(Packed.AsSpan(index * _stride, _stride));
        }

        Count--;
        MarkDirty(position * _stride, checked((Count - position) * _stride));
    }

    public void CopySlot(int sourcePosition, int destinationPosition)
    {
        ItemIndices[destinationPosition] = ItemIndices[sourcePosition];
        Packed.AsSpan(sourcePosition * _stride, _stride).CopyTo(Packed.AsSpan(destinationPosition * _stride, _stride));
        MarkDirty(destinationPosition * _stride, _stride);
    }

    public void AppendSegment(RenderBatchSegment other)
    {
        EnsureCapacity(Count + other.Count);
        for (var index = 0; index < other.Count; index++)
        {
            ItemIndices[Count + index] = other.ItemIndices[index];
            other.Packed.AsSpan(index * _stride, _stride).CopyTo(Packed.AsSpan((Count + index) * _stride, _stride));
        }

        var oldCount = Count;
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

    private sealed class SegmentRasterPass(RenderBatchSegment segment, PixelExtent viewport) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(new RenderViewport(0, 0, viewport.Width, viewport.Height));
            commands.SetScissor(segment.Key.Clip);
            var material = segment.Material;
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
