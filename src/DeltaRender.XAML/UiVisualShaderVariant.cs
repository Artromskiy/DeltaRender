using Delta.Shader.Contract;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>Generated payload family selected for a visual shader variant.</summary>
public enum UiVisualShaderPath : byte
{
    Standard,
    SolidStrokeEffect,
    RoundedStrokeEffect,
    OuterShadowOnlyEffect,
    OuterGlowOnlyEffect,
    InnerShadowEffect,
    InnerGlowEffect,
    CachedMask,
    SolidLinearGradient,
    SolidImage,
}

/// <summary>Describes a prepared visual shader and the renderer-owned payload it packs.</summary>
public readonly record struct UiVisualShaderVariant(
    IGraphicsShaderProgram Program,
    UiVisualKind Kind,
    UiVisualShaderPath Path = UiVisualShaderPath.Standard)
{
    /// <summary>Gets whether the descriptor names a supported generated UI payload.</summary>
    public bool IsValid => Program is not null &&
        Kind is (UiVisualKind.SolidRectangle or UiVisualKind.RoundedRectangle or UiVisualKind.Border) &&
        Path is UiVisualShaderPath.Standard or UiVisualShaderPath.SolidStrokeEffect or UiVisualShaderPath.RoundedStrokeEffect or UiVisualShaderPath.OuterShadowOnlyEffect or UiVisualShaderPath.OuterGlowOnlyEffect or UiVisualShaderPath.InnerShadowEffect or UiVisualShaderPath.InnerGlowEffect or UiVisualShaderPath.CachedMask;
}
