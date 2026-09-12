using System.Numerics;
using Delta.Shader.Contract;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>Generated text payload family selected for a text shader variant.</summary>
public enum TextShaderPath : byte
{
    Standard,
    Gradient,
    Stroke,
    OuterGlowOnly,
    OuterShadow,
    InnerShadow,
    InnerGlowOnly,
}

/// <summary>Resolved per-run text gradient geometry and up to four normalized stops.</summary>
public readonly record struct TextGradientValues(
    Vector4 Line,
    Vector4 Stop0,
    Vector4 Stop1,
    Vector4 Stop2,
    Vector4 Stop3,
    Vector4 StopPositions,
    float StopCount,
    float Radial)
{
    public static TextGradientValues Empty => default;

    internal bool IsValid => IsFinite(Line) && IsFinite(Stop0) && IsFinite(Stop1) && IsFinite(Stop2) &&
        IsFinite(Stop3) && IsFinite(StopPositions) && float.IsFinite(StopCount) && StopCount is >= 2 and <= 4 &&
        (Radial is 0 or 1) && IsFinite(Radial);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(float value) => float.IsFinite(value);
}

/// <summary>Neutral stroke, outer and inner shadow/glow values selected from a typed UI effect resource.</summary>
public readonly record struct TextEffectValues(
    Vector4 StrokeColor,
    float StrokeWidth,
    Vector4 OuterGlowColor,
    float OuterGlowRadius,
    float OuterGlowIntensity,
    Vector4 OuterShadowColor,
    Vector2 OuterShadowOffset,
    float OuterShadowWidth,
    float OuterShadowBlurRadius,
    float OuterShadowSpread,
    float OuterShadowIntensity)
{
    public Vector4 InnerShadowColor { get; init; }
    public Vector2 InnerShadowOffset { get; init; }
    public float InnerShadowWidth { get; init; }
    public float InnerShadowBlurRadius { get; init; }
    public float InnerShadowSpread { get; init; }
    public float InnerShadowIntensity { get; init; }
    public Vector4 InnerGlowColor { get; init; }
    public float InnerGlowRadius { get; init; }
    public float InnerGlowSpread { get; init; }
    public float InnerGlowIntensity { get; init; }

    /// <summary>Gets an empty no-effect payload.</summary>
    public static TextEffectValues Empty => default;

    internal bool IsValid => IsFinite(StrokeColor) && float.IsFinite(StrokeWidth) && StrokeWidth >= 0 &&
        IsFinite(OuterGlowColor) && float.IsFinite(OuterGlowRadius) && OuterGlowRadius >= 0 &&
        float.IsFinite(OuterGlowIntensity) && OuterGlowIntensity >= 0 &&
        IsFinite(OuterShadowColor) && IsFinite(OuterShadowOffset) &&
        float.IsFinite(OuterShadowWidth) && OuterShadowWidth >= 0 &&
        float.IsFinite(OuterShadowBlurRadius) && OuterShadowBlurRadius >= 0 &&
        float.IsFinite(OuterShadowSpread) && float.IsFinite(OuterShadowIntensity) && OuterShadowIntensity >= 0 &&
        IsFinite(InnerShadowColor) && IsFinite(InnerShadowOffset) &&
        float.IsFinite(InnerShadowWidth) && InnerShadowWidth >= 0 &&
        float.IsFinite(InnerShadowBlurRadius) && InnerShadowBlurRadius >= 0 &&
        float.IsFinite(InnerShadowSpread) && float.IsFinite(InnerShadowIntensity) && InnerShadowIntensity >= 0 &&
        IsFinite(InnerGlowColor) && float.IsFinite(InnerGlowRadius) && InnerGlowRadius >= 0 &&
        float.IsFinite(InnerGlowSpread) && float.IsFinite(InnerGlowIntensity) && InnerGlowIntensity >= 0;

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}

/// <summary>Describes one prepared text shader and its generated packing mode.</summary>
public readonly record struct TextShaderVariant(
    IGraphicsShaderProgram Program,
    GlyphImageMode Mode,
    TextShaderPath Path = TextShaderPath.Standard)
{
    /// <summary>Gets whether the descriptor can use the generated text packer path.</summary>
    public bool IsValid => Program is not null &&
        Mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf &&
        Path is TextShaderPath.Standard or TextShaderPath.Gradient or TextShaderPath.Stroke or TextShaderPath.OuterGlowOnly or
            TextShaderPath.OuterShadow or TextShaderPath.InnerShadow or TextShaderPath.InnerGlowOnly;
}
