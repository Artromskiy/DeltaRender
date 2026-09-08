using System.Numerics;
using Delta.Shader.Contract;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>Generated text payload family selected for a text shader variant.</summary>
public enum TextShaderPath : byte
{
    Standard,
    Outline,
    OutlineGlow,
}

/// <summary>Neutral outline, outer-shadow and glow values selected from a typed UI effect resource.</summary>
public readonly record struct TextEffectValues(
    Vector4 OutlineColor,
    float OutlineWidth,
    Vector4 GlowColor,
    float GlowRadius,
    float GlowIntensity,
    Vector4 OuterShadowColor,
    Vector2 OuterShadowOffset,
    float OuterShadowWidth,
    float OuterShadowBlurRadius,
    float OuterShadowSpread,
    float OuterShadowIntensity)
{
    /// <summary>Gets an empty no-effect payload.</summary>
    public static TextEffectValues Empty => default;

    internal bool IsValid => IsFinite(OutlineColor) && float.IsFinite(OutlineWidth) && OutlineWidth >= 0 &&
        IsFinite(GlowColor) && float.IsFinite(GlowRadius) && GlowRadius >= 0 &&
        float.IsFinite(GlowIntensity) && GlowIntensity >= 0 &&
        IsFinite(OuterShadowColor) && IsFinite(OuterShadowOffset) &&
        float.IsFinite(OuterShadowWidth) && OuterShadowWidth >= 0 &&
        float.IsFinite(OuterShadowBlurRadius) && OuterShadowBlurRadius >= 0 &&
        float.IsFinite(OuterShadowSpread) && float.IsFinite(OuterShadowIntensity) && OuterShadowIntensity >= 0;

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
        (Mode is (GlyphImageMode.Sdf or GlyphImageMode.Msdf) && Path is TextShaderPath.Standard or TextShaderPath.OutlineGlow) ||
        (Mode == GlyphImageMode.Sdf && Path == TextShaderPath.Outline);
}
