using Delta.Shader.Contract;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>Describes one prepared text shader and its generated packing mode.</summary>
public readonly record struct TextShaderVariant(
    IGraphicsShaderProgram Program,
    GlyphImageMode Mode)
{
    /// <summary>Gets whether the descriptor can use the generated text packer path.</summary>
    public bool IsValid => Program is not null && (Mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf);
}
