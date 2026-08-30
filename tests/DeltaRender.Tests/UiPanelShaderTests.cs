using Xunit;

namespace Delta.Render.Tests;

public sealed class UiPanelShaderTests
{
    [Fact]
    public void GeneratedVertexShaderPreservesTwoTriangleLocalVertexTable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-panel.vert.glsl");
        var source = File.ReadAllText(path);

        Assert.Contains("vec2 local = vec2(0, 0)", source, StringComparison.Ordinal);
        Assert.Contains("vertexIndex == 1u || vertexIndex == 2u || vertexIndex == 4u", source, StringComparison.Ordinal);
        Assert.Contains("vertexIndex == 2u || vertexIndex == 4u || vertexIndex == 5u", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedVertexShaderUsesTopLeftPixelToClipMapping()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-panel.vert.glsl");
        var source = File.ReadAllText(path);

        var normalized = source.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains("pixel.y/pushConstants.member_Resolution.y*2-1", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("1-pixel.y/pushConstants.member_Resolution.y*2", normalized, StringComparison.Ordinal);
    }
}
