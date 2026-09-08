using Delta.Shader.Contract;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>Describes a prepared visual shader and the renderer-owned payload it packs.</summary>
public readonly record struct UiVisualShaderVariant(
    IGraphicsShaderProgram Program,
    UiVisualKind Kind)
{
    /// <summary>Gets whether the descriptor names a supported generated UI payload.</summary>
    public bool IsValid => Program is not null && Kind is
        UiVisualKind.SolidRectangle or UiVisualKind.RoundedRectangle or UiVisualKind.Border;
}
