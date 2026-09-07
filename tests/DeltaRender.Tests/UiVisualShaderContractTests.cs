using System.Buffers.Binary;
using Delta;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Render.UIShaders;
using Delta.Render.UIShaders.Shaders;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiVisualShaderContractTests
{
    [Fact]
    public void GeneratedSolidRectangleArtifactIsAcceptedAndPacked()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var visual = new UiVisualDraw(
            UiVisualKind.SolidRectangle,
            default,
            new float4(10, 20, 30, 40),
            new float4(0.1f, 0.2f, 0.3f, 0.4f),
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(UiVisualShaderContract.TryDescribe(
            program,
            visual.Kind,
            out var shaderKind,
            out var pushConstantSize,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.Solid, shaderKind);
        Assert.Equal(8u, pushConstantSize);

        Span<byte> frame = stackalloc byte[8];
        Assert.Equal(8, UiVisualShaderContract.PackFrame(shaderKind, new PixelExtent(800, 600), frame));
        Assert.Equal(800f, ReadFloat(frame, 0));
        Assert.Equal(600f, ReadFloat(frame, 4));

        Span<byte> packed = stackalloc byte[32];
        Assert.Equal(32, UiVisualShaderContract.PackInstance(shaderKind, in visual, packed));
        Assert.Equal(10f, ReadFloat(packed, 0));
        Assert.Equal(20f, ReadFloat(packed, 4));
        Assert.Equal(0.4f, ReadFloat(packed, 28));
    }

    [Fact]
    public void UnknownVisualProgramIsRejectedDeterministically()
    {
        var vertex = new ShaderArtifact(MinimalSpirv, "main", new ShaderAbi(ShaderStage.Vertex));
        var fragment = new ShaderArtifact(MinimalSpirv, "main", new ShaderAbi(ShaderStage.Fragment));
        var program = new GraphicsShaderProgram(vertex, fragment);

        Assert.False(UiVisualShaderContract.TryDescribe(
            program,
            UiVisualKind.SolidRectangle,
            out _,
            out _,
            out var diagnostic));
        Assert.Equal(
            "Visual kind SolidRectangle requires the matching generated DeltaRender.UIShaders solid-rectangle ABI.",
            diagnostic);
    }

    [Fact]
    public void GeneratedRoundedRectangleArtifactAcceptsAndPacksIndependentCornerRadii()
    {
        var program = RoundedRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(10, 20, 30, 40),
            new UiVisualPaint(
                new float4(0.1f, 0.2f, 0.3f, 0.4f),
                new float4(0.5f, 0.6f, 0.7f, 0.8f),
                2.5f,
                new float4(1, 2, 3, 4)),
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(UiVisualShaderContract.TryDescribe(
            program,
            visual.Kind,
            out var shaderKind,
            out var pushConstantSize,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.Rounded, shaderKind);
        Assert.Equal(8u, pushConstantSize);

        Span<byte> frame = stackalloc byte[8];
        Assert.Equal(8, UiVisualShaderContract.PackFrame(shaderKind, new PixelExtent(800, 600), frame));
        Assert.Equal(800f, ReadFloat(frame, 0));
        Assert.Equal(600f, ReadFloat(frame, 4));

        Span<byte> packed = stackalloc byte[80];
        Assert.Equal(80, UiVisualShaderContract.PackInstance(shaderKind, in visual, packed));
        Assert.Equal(10f, ReadFloat(packed, 0));
        Assert.Equal(20f, ReadFloat(packed, 4));
        Assert.Equal(1f, ReadFloat(packed, 48));
        Assert.Equal(2f, ReadFloat(packed, 52));
        Assert.Equal(3f, ReadFloat(packed, 56));
        Assert.Equal(4f, ReadFloat(packed, 60));
        Assert.Equal(2.5f, ReadFloat(packed, 64));
    }

    [Fact]
    public void SolidArtifactUsesScissorClipBoundary()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var visual = new UiVisualDraw(
            UiVisualKind.SolidRectangle,
            default,
            new float4(10, 20, 30, 40),
            new float4(0.1f, 0.2f, 0.3f, 0.4f),
            UiClipId.None,
            UiResourceId.Empty);
        var clip = new PixelRect(12, 14, 50, 60);

        Assert.True(UiVisualShaderContract.TryDescribeInstance(
            program,
            visual.Kind,
            out var shaderKind,
            out var instanceBinding,
            out var instanceStride,
            out var pushConstantSize,
            out _,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.Solid, shaderKind);
        Assert.Equal(new ShaderBinding(0, 0), instanceBinding);
        Assert.Equal(32u, instanceStride);
        Assert.Equal(8u, pushConstantSize);

        Span<byte> packed = stackalloc byte[32];
        Assert.Equal(32, UiVisualShaderContract.PackInstances(shaderKind, in visual, in clip, instanceStride, packed));
        Assert.Equal(0.1f, ReadFloat(packed, 16));
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static readonly byte[] MinimalSpirv =
    [
        0x03, 0x02, 0x23, 0x07,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];
}
