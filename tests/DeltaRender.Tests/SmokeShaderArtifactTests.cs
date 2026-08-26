using Delta.Render.FullscreenShaders;
using Delta.Render.UiShaders;
using Delta.Shader.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class SmokeShaderArtifactTests
{
    [Theory]
    [InlineData("fullscreen-rounded-rectangle.vert", ShaderStage.Vertex)]
    [InlineData("fullscreen-rounded-rectangle.frag", ShaderStage.Fragment)]
    [InlineData("ui-panel.vert", ShaderStage.Vertex)]
    [InlineData("ui-panel.frag", ShaderStage.Fragment)]
    public void CheckedInSmokeArtifactUsesCurrentManifestContract(string stem, ShaderStage expectedStage)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphics");
        var manifestPath = Path.Combine(root, stem + ".shader.json");
        var spirvPath = Path.Combine(root, stem + ".spv");
        Assert.True(File.Exists(manifestPath));
        var pairStem = stem[..stem.LastIndexOf('.')];
        var pairVertexPath = Path.Combine(root, pairStem + ".vert.spv");
        var pairFragmentPath = Path.Combine(root, pairStem + ".frag.spv");
        var program = pairStem switch
        {
            "fullscreen-rounded-rectangle" => FullscreenUiGraphicsShaderProgram.CreateProgram(
                File.ReadAllBytes(pairVertexPath), File.ReadAllBytes(pairFragmentPath)),
            "ui-panel" => UiPanelGraphicsShaderProgram.CreateProgram(
                File.ReadAllBytes(pairVertexPath), File.ReadAllBytes(pairFragmentPath)),
            _ => throw new InvalidOperationException($"Unknown graphics fixture pair: {pairStem}")
        };

        var artifact = expectedStage == ShaderStage.Vertex ? program.Vertex : program.Fragment;
        Assert.NotEmpty(artifact.Spirv.ToArray());
        Assert.Equal(expectedStage, artifact.Stage);
        Assert.Equal("main", artifact.EntryPoint);
        Assert.Equal(expectedStage, artifact.Abi.Stage);
    }
}
