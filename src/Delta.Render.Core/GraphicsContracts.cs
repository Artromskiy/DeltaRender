namespace Delta.Render.Core;

// Host values for the initial fullscreen shader ABI: vec2 resolution at byte
// 0, float time at byte 8, and four bytes of alignment padding at byte 12.
public readonly record struct GraphicsFrameParameters(
    float ResolutionX,
    float ResolutionY,
    float TimeSeconds)
{
    public bool IsValid => ResolutionX > 0 && ResolutionY > 0 &&
                           float.IsFinite(ResolutionX) && float.IsFinite(ResolutionY) &&
                           float.IsFinite(TimeSeconds);
}

public readonly record struct UiClipRect(float X, float Y, float Width, float Height)
{
    public static UiClipRect Unbounded => new(0, 0, float.PositiveInfinity, float.PositiveInfinity);

    public bool IsUnbounded => X == 0 && Y == 0 && float.IsPositiveInfinity(Width) && float.IsPositiveInfinity(Height);

    public bool IsValid => IsUnbounded ||
                           Width > 0 && Height > 0 &&
                           float.IsFinite(X) && float.IsFinite(Y) &&
                           float.IsFinite(Width) && float.IsFinite(Height);

    public bool TryGetScissor(WindowMetrics metrics, out UiScissorRect scissor)
    {
        if (!IsValid || metrics.Width == 0 || metrics.Height == 0)
        {
            scissor = default;
            return false;
        }

        var left = IsUnbounded ? 0 : Math.Max(0, (int)MathF.Ceiling(X));
        var top = IsUnbounded ? 0 : Math.Max(0, (int)MathF.Ceiling(Y));
        var right = IsUnbounded ? (int)metrics.Width : Math.Min((int)metrics.Width, (int)MathF.Floor(X + Width));
        var bottom = IsUnbounded ? (int)metrics.Height : Math.Min((int)metrics.Height, (int)MathF.Floor(Y + Height));
        if (right <= left || bottom <= top)
        {
            scissor = default;
            return false;
        }

        scissor = new UiScissorRect(left, top, (uint)(right - left), (uint)(bottom - top));
        return true;
    }
}

public readonly record struct UiScissorRect(int X, int Y, uint Width, uint Height);

public readonly record struct UiRenderResourceHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct UiRenderClipId(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct UiRenderClipEntry(
    UiRenderClipId Id,
    UiClipRect Bounds,
    UiRenderClipId Parent);

public readonly record struct UiRenderRange(int Start, int Count)
{
    public bool IsValid => Start >= 0 && Count >= 0;
}

public readonly record struct UiRenderDrawDelta(
    UiRenderRange Commands,
    UiRenderRange Clips,
    UiRenderRange TextRuns,
    uint BaseVersion,
    uint NextVersion)
{
    public bool IsEmpty => Commands.Count == 0 && Clips.Count == 0 && TextRuns.Count == 0;
}

public readonly record struct UiQuad(
    float X,
    float Y,
    float Width,
    float Height,
    float Red,
    float Green,
    float Blue,
    float Alpha)
{
    public UiClipRect Clip { get; init; } = UiClipRect.Unbounded;

    public UiRenderClipId ClipId { get; init; }

    public UiRenderResourceHandle Resource { get; init; }

    public ulong OwnerId { get; init; }

    public int ZIndex { get; init; }

    public uint Order { get; init; }

    public bool IsValid => Width > 0 && Height > 0 &&
                           float.IsFinite(X) && float.IsFinite(Y) &&
                           float.IsFinite(Width) && float.IsFinite(Height) &&
                           float.IsFinite(Red) && float.IsFinite(Green) &&
                           float.IsFinite(Blue) && float.IsFinite(Alpha) &&
                           Clip.IsValid;
}

public readonly ref struct UiDrawList
{
    public UiDrawList(ReadOnlySpan<UiQuad> quads) => Quads = quads;
    public UiDrawList(ReadOnlyMemory<UiQuad> quads) => Quads = quads.Span;
    public ReadOnlySpan<UiQuad> Quads { get; }
    public int Count => Quads.Length;
    public bool IsEmpty => Quads.IsEmpty;
}

public interface IUiDrawListProvider
{
    ReadOnlyMemory<UiQuad> CurrentDrawList { get; }
}

public interface IGraphicsPipeline : IAsyncDisposable
{
}
