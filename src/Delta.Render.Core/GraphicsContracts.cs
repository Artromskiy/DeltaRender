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

public interface IGraphicsPipeline : IAsyncDisposable
{
}
