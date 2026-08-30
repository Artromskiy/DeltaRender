namespace Delta.Render.RenderGraph;

public enum RenderBatchOrderMode : byte
{
    Ordered,
    Unordered,
}

public readonly record struct RenderBatchItemId(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderBatchVersion(ulong Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct RenderBatchPipelineHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderBatchMaterialHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderBatchOrderKey(long ZIndex, ulong StableSequence);

public readonly record struct RenderBatchKey(
    RenderBatchPipelineHandle Pipeline,
    RenderBatchMaterialHandle Material,
    PixelRect Clip)
{
    public bool IsValid => Pipeline.IsValid && Material.IsValid && !Clip.IsEmpty;
}

/// <summary>
/// Borrowed update payload. The bytes are copied synchronously by <see cref="RenderBatcher.TryApply"/>.
/// The caller must keep the span valid only for that call.
/// </summary>
public readonly ref struct RenderBatchItemChange
{
    public RenderBatchItemChange(
        RenderBatchItemId id,
        RenderBatchVersion version,
        RenderBatchOrderKey order,
        RenderBatchKey key,
        ReadOnlySpan<byte> instanceData)
    {
        Id = id;
        Version = version;
        Order = order;
        Key = key;
        InstanceData = instanceData;
    }

    public RenderBatchItemId Id { get; }

    public RenderBatchVersion Version { get; }

    public RenderBatchOrderKey Order { get; }

    public RenderBatchKey Key { get; }

    public ReadOnlySpan<byte> InstanceData { get; }
}
