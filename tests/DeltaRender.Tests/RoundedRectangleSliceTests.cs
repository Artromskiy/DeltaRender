using System.Buffers.Binary;
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
                "main",
                RoundedRectangleSliceGraphicsShaderProgram.VertexAbi),
            new ShaderArtifact(
                MinimalSpirv,
                "main",
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
        Assert.Equal(96u, instanceStride);
        Assert.Equal(8u, pushConstantSize);
        Assert.Equal(new ShaderBinding(0, 0), instanceBinding);

        Span<byte> packed = stackalloc byte[96 * 9];
        Assert.Equal(96 * 9, UiVisualShaderContract.PackInstances(shaderKind, in visual, 1f, instanceStride, packed));
    }

    [Fact]
    public void PairwiseSymmetricRadiiPackSevenInstances()
    {
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(20, 30, 180, 120),
            new UiVisualPaint(
                new float4(0.2f, 0.4f, 0.8f, 1),
                new float4(0, 0, 0, 1),
                2,
                new float4(8, 20, 20, 8)),
            UiClipId.None,
            UiResourceId.Empty);

        Span<byte> packed = stackalloc byte[96 * 9];
        Assert.Equal(96 * 7, UiVisualShaderContract.PackInstances(
            UiRectangleShaderKind.RoundedSlice,
            in visual,
            1f,
            96,
            packed));
    }

    [Fact]
    public void ClipAwareSliceArtifactPacksClipIntoEverySlice()
    {
        var program = new GraphicsShaderProgram(
            new ShaderArtifact(
                MinimalSpirv,
                "main",
                ClipAwareRoundedRectangleSliceGraphicsShaderProgram.VertexAbi),
            new ShaderArtifact(
                MinimalSpirv,
                "main",
                ClipAwareRoundedRectangleSliceGraphicsShaderProgram.FragmentAbi));
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
        var clip = new PixelRect(12, 14, 50, 60);

        Assert.True(UiVisualShaderContract.TryDescribeInstance(
            program,
            visual.Kind,
            out var shaderKind,
            out _,
            out var instanceStride,
            out var pushConstantSize,
            out _,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.ClipAwareRoundedSlice, shaderKind);
        Assert.Equal(112u, instanceStride);
        Assert.Equal(8u, pushConstantSize);

        Span<byte> packed = stackalloc byte[112 * 9];
        var written = UiVisualShaderContract.PackInstances(shaderKind, in visual, in clip, 1f, instanceStride, packed);
        Assert.Equal(112 * 9, written);
        for (var index = 0; index < 9; index++)
        {
            var offset = index * 112 + 96;
            Assert.Equal(12f, ReadFloat(packed, offset));
            Assert.Equal(14f, ReadFloat(packed, offset + 4));
            Assert.Equal(50f, ReadFloat(packed, offset + 8));
            Assert.Equal(60f, ReadFloat(packed, offset + 12));
        }
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
}
