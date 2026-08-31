using Delta.Maths;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Shader.UI;
using Delta.XAML.Contract;
using Xunit;

namespace DeltaRender.Tests;

public sealed class RoundedRectangleSliceTests
{
    private static readonly byte[] MinimalSpirv =
    [
        0x03, 0x02, 0x23, 0x07,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void GeneratedSliceArtifactPacksNineInstancesWithIndependentRadii()
    {
        var program = new GraphicsShaderProgram(
            new ShaderArtifact(
                MinimalSpirv,
                "rounded-rectangle-slice",
                RoundedRectangleSliceGraphicsShaderProgram.VertexAbi),
            new ShaderArtifact(
                MinimalSpirv,
                "rounded-rectangle-slice",
                RoundedRectangleSliceGraphicsShaderProgram.FragmentAbi));
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(20, 30, 180, 120),
            new UiVisualPaint(
                new float4(0.2f, 0.4f, 0.8f, 1),
                new float4(0, 0, 0, 1),
                2,
                new float4(8, 12, 16, 20)),
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(UiVisualShaderContract.TryDescribeInstance(
            program,
            visual.Kind,
            out var shaderKind,
            out var instanceBinding,
            out var instanceStride,
            out var pushConstantSize,
            out _,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.RoundedSlice, shaderKind);
        Assert.Equal(112u, instanceStride);
        Assert.Equal(8u, pushConstantSize);
        Assert.Equal(new ShaderBinding(0, 0), instanceBinding);

        Span<byte> packed = stackalloc byte[112 * 9];
        Assert.Equal(112 * 9, UiVisualShaderContract.PackInstances(shaderKind, in visual, instanceStride, packed));
    }
}
