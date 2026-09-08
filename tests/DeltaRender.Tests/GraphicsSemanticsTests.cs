using Delta.Graphics.Semantics;
using Delta.Shader;
using Xunit;

namespace Delta.Render.Tests;

public sealed class GraphicsSemanticsTests
{
    [Fact]
    public void CommonSemanticsHaveOneAssemblyIdentity()
    {
        Type[] types =
        [
            typeof(Uv0),
            typeof(Uv1),
            typeof(VertexColor)
        ];

        Assert.All(types, type => Assert.Equal("DeltaRender.Graphics.Semantics", type.Assembly.GetName().Name));
        Assert.Single(types.Select(type => type.Assembly).Distinct());
    }

    [Fact]
    public void ShaderBuiltinsHaveDeltaShaderIdentity()
    {
        Assert.Equal("DeltaShader", typeof(Position).Assembly.GetName().Name);
        Assert.Equal("DeltaShader", typeof(FragmentColor).Assembly.GetName().Name);
    }
}
