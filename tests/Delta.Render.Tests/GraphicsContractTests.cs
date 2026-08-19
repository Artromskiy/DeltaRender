using Delta.Render.Core;
using Delta.Shader.Abstractions;
using Xunit;

namespace Delta.Render.Tests;

public sealed class GraphicsContractTests
{
    [Fact]
    public void Graphics_program_requires_paired_vertex_and_fragment_stages()
    {
        var bytes = new byte[4];
        var program = new GraphicsShaderProgram(
            Artifact(bytes, ShaderStage.Vertex),
            Artifact(bytes, ShaderStage.Fragment));

        Assert.Equal(ShaderStage.Vertex, program.Vertex.Stage);
        Assert.Equal(ShaderStage.Fragment, program.Fragment.Stage);
        Assert.Equal("main", program.Vertex.EntryPoint);

        Assert.Throws<ArgumentException>(() => new GraphicsShaderProgram(
            Artifact(bytes, ShaderStage.Fragment),
            Artifact(bytes, ShaderStage.Vertex)));
    }

    [Fact]
    public void Graphics_frame_parameters_validate_resolution_and_time()
    {
        Assert.True(new GraphicsFrameParameters(960, 540, 1.25f).IsValid);
        Assert.False(new GraphicsFrameParameters(0, 540, 1.25f).IsValid);
        Assert.False(new GraphicsFrameParameters(960, float.NaN, 1.25f).IsValid);
    }

    [Fact]
    public void Frame_session_exposes_graphics_without_event_pump_ownership()
    {
        var members = typeof(IRenderWindowFrameSession).GetMethods()
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IRenderWindowFrameSession.CreateGraphicsPipeline), members);
        Assert.Contains(nameof(IRenderWindowFrameSession.DrawFullscreenTriangle), members);
        Assert.DoesNotContain("PollEvents", members);
    }

    private static ShaderArtifact Artifact(byte[] spirv, ShaderStage stage)
        => new(spirv, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = stage,
            EntryPointName = "main"
        });
}
