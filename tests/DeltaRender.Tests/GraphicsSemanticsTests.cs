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

        Assert.All(types, type => Assert.Equal("Delta.Graphics.Semantics", type.Assembly.GetName().Name));
        Assert.Equal(1, types.Select(type => type.Assembly).Distinct().Count());
    }

    [Fact]
    public void ShaderBuiltinsHaveDeltaShaderIdentity()
    {
        Assert.Equal("Delta.Shader", typeof(Position).Assembly.GetName().Name);
        Assert.Equal("Delta.Shader", typeof(FragmentColor).Assembly.GetName().Name);
    }
}
