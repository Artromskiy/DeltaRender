using System.Numerics;
using Delta.Shader.Contract;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>Generated text payload family selected for a text shader variant.</summary>
public enum TextShaderPath : byte
{
    Standard,
    OutlineGlow,
}

/// <summary>Neutral outline/glow values selected from a typed UI effect resource.</summary>
public readonly record struct TextEffectValues(
    Vector4 OutlineColor,
    float OutlineWidth,
    Vector4 GlowColor,
    float GlowRadius,
    float GlowIntensity)
{
    /// <summary>Gets an empty no-effect payload.</summary>
    public static TextEffectValues Empty => default;

    internal bool IsValid => IsFinite(OutlineColor) && float.IsFinite(OutlineWidth) && OutlineWidth >= 0 &&
        IsFinite(GlowColor) && float.IsFinite(GlowRadius) && GlowRadius >= 0 &&
        float.IsFinite(GlowIntensity) && GlowIntensity >= 0;

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>Describes one prepared text shader and its generated packing mode.</summary>
public readonly record struct TextShaderVariant(
    IGraphicsShaderProgram Program,
    GlyphImageMode Mode,
    TextShaderPath Path = TextShaderPath.Standard)
{
    /// <summary>Gets whether the descriptor can use the generated text packer path.</summary>
    public bool IsValid => Program is not null &&
        Mode is (GlyphImageMode.Sdf or GlyphImageMode.Msdf) &&
        Path is TextShaderPath.Standard or TextShaderPath.OutlineGlow;
}
