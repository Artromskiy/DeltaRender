using System.Buffers.Binary;
using Delta.Maths;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Shader.UI;
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
        Assert.Equal(48u, pushConstantSize);

        Span<byte> packed = stackalloc byte[48];
        Assert.Equal(48, UiVisualShaderContract.Pack(shaderKind, in visual, new PixelExtent(800, 600), packed));
        Assert.Equal(800f, ReadFloat(packed, 0));
        Assert.Equal(600f, ReadFloat(packed, 4));
        Assert.Equal(10f, ReadFloat(packed, 16));
        Assert.Equal(40f, ReadFloat(packed, 28));
        Assert.Equal(0.3f, ReadFloat(packed, 40));
        Assert.Equal(0.4f, ReadFloat(packed, 44));
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
            "Visual kind SolidRectangle requires the matching generated DeltaShader.UI solid-rectangle ABI.",
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
        Assert.Equal(96u, pushConstantSize);

        Span<byte> packed = stackalloc byte[96];
        Assert.Equal(96, UiVisualShaderContract.Pack(shaderKind, in visual, new PixelExtent(800, 600), packed));
        Assert.Equal(800f, ReadFloat(packed, 0));
        Assert.Equal(600f, ReadFloat(packed, 4));
        Assert.Equal(1f, ReadFloat(packed, 64));
        Assert.Equal(2f, ReadFloat(packed, 68));
        Assert.Equal(3f, ReadFloat(packed, 72));
        Assert.Equal(4f, ReadFloat(packed, 76));
        Assert.Equal(2.5f, ReadFloat(packed, 80));
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
