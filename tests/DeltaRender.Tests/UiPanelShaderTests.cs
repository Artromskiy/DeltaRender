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
        Assert.Contains("uint(gl_VertexIndex)== 1u || uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u", source, StringComparison.Ordinal);
        Assert.Contains("uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u || uint(gl_VertexIndex)== 5u", source, StringComparison.Ordinal);
    }
}
