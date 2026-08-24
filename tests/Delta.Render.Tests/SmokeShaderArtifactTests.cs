using System.Text.Json;
using Delta.Shader.Abstractions;
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
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphics");
        var manifestPath = Path.Combine(root, stem + ".shader.json");
        var spirvPath = Path.Combine(root, stem + ".spv");
        var manifest = JsonSerializer.Deserialize<ShaderAbiManifest>(File.ReadAllText(manifestPath));

        Assert.NotNull(manifest);
        Assert.Equal(ShaderAbiManifest.CurrentVersion, manifest.Version);
        Assert.Equal(expectedStage, manifest.Stage);
        Assert.Equal("main", manifest.EntryPointName);
        Assert.False(string.IsNullOrWhiteSpace(manifest.SourceEntryPointName));

        var spirv = File.ReadAllBytes(spirvPath);
        Assert.NotEmpty(spirv);
        var artifact = new ShaderArtifact(spirv, manifest);
        Assert.Equal(expectedStage, artifact.Stage);
        Assert.Equal("main", artifact.EntryPoint);
    }
}
