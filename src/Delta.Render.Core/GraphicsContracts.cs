using Delta.Shader.Abstractions;

namespace Delta.Render.Core;

public readonly record struct GraphicsShaderProgram
{
    public GraphicsShaderProgram(ShaderArtifact vertex, ShaderArtifact fragment)
    {
        Vertex = vertex ?? throw new ArgumentNullException(nameof(vertex));
        Fragment = fragment ?? throw new ArgumentNullException(nameof(fragment));
        if (vertex.Stage != ShaderStage.Vertex || fragment.Stage != ShaderStage.Fragment)
        {
            throw new ArgumentException("Graphics shader programs require vertex and fragment Delta.Shader artifacts.");
        }
    }

    public ShaderArtifact Vertex { get; }
    public ShaderArtifact Fragment { get; }
}

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
    public bool IsValid => Width > 0 && Height > 0 &&
                           float.IsFinite(X) && float.IsFinite(Y) &&
                           float.IsFinite(Width) && float.IsFinite(Height) &&
                           float.IsFinite(Red) && float.IsFinite(Green) &&
                           float.IsFinite(Blue) && float.IsFinite(Alpha);
}

public readonly ref struct UiDrawList
{
    public UiDrawList(ReadOnlySpan<UiQuad> quads) => Quads = quads;
    public ReadOnlySpan<UiQuad> Quads { get; }
    public int Count => Quads.Length;
    public bool IsEmpty => Quads.IsEmpty;
}

public interface IGraphicsPipeline : IAsyncDisposable
{
}
