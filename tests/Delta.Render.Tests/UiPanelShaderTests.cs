using Xunit;

namespace Delta.Render.Tests;

public sealed class UiPanelShaderTests
{
    [Fact]
    public void Generated_vertex_shader_preserves_two_triangle_local_vertex_table()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-panel.vert.glsl");
        var source = File.ReadAllText(path);

        Assert.Contains("vec2 local = vec2(0, 0)", source);
        Assert.Contains("uint(gl_VertexIndex)== 1u || uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u", source);
        Assert.Contains("uint(gl_VertexIndex)== 2u || uint(gl_VertexIndex)== 4u || uint(gl_VertexIndex)== 5u", source);
    }
}
