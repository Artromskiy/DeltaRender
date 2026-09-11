using System.Buffers.Binary;
using Delta;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Render.UIShaders;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiVisualShaderContractTests
{
    [Fact]
    public void GeneratedLinearGradientArtifactAcceptsAndPacksCopiedStops()
    {
        var program = SolidLinearGradientGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(10, 20, 30, 40),
            UiVisualPaint.Solid(new float4(1, 1, 1, 1)) with { CornerRadii = new float4(2, 3, 4, 5) },
            UiClipId.None,
            new UiResourceId(Guid.Parse("00000000-0000-0000-0000-000000000011")));
        var stops = new[]
        {
            new UiLinearGradientStop(0f, new float4(1, 0, 0, 1)),
            new UiLinearGradientStop(0.5f, new float4(0, 1, 0, 1)),
            new UiLinearGradientStop(1f, new float4(0, 0, 1, 1)),
        };
        var resource = new UiLinearGradientResource(
            visual.Resource,
            new float2(0, 0),
            new float2(40, 0),
            PaintUnits.Device,
            stops)
        {
            IsRelativeToBounds = true,
            AngleDegrees = 110f,
            OutlineColor = new float4(1, 0.54f, 0, 1),
            OutlineWidth = 1f,
        };
        var registry = new UiDisplayListResourceRegistry();
        registry.RegisterLinearGradient(resource);
        Assert.True(registry.TryResolveLinearGradient(resource.Resource, out var registered));
        Assert.True(registered.IsRelativeToBounds);
        Assert.Equal(110f, registered.AngleDegrees);
        Assert.Equal(resource.OutlineColor, registered.OutlineColor);
        Assert.Equal(resource.OutlineWidth, registered.OutlineWidth);

        Assert.True(UiVisualShaderContract.TryDescribe(
            program,
            visual.Kind,
            UiVisualShaderPath.SolidLinearGradient,
            out var shaderKind,
            out var pushConstantSize,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.SolidLinearGradient, shaderKind);
        Assert.Equal(8u, pushConstantSize);

        stops[0] = new UiLinearGradientStop(0f, new float4(0, 0, 0, 1));
        Span<byte> packed = stackalloc byte[176];
        var effect = default(UiEffectResource);
        var mask = default(float4);
        Assert.Equal(176, UiVisualShaderContract.PackInstance(
            shaderKind,
            in visual,
            in effect,
            in mask,
            1f,
            new UiLinearGradientResource(
                resource.Resource,
                resource.Start,
                resource.End,
                resource.Units,
                new[]
                {
                    new UiLinearGradientStop(0f, new float4(1, 0, 0, 1)),
                    new UiLinearGradientStop(0.5f, new float4(0, 1, 0, 1)),
                    new UiLinearGradientStop(1f, new float4(0, 0, 1, 1)),
                })
            {
                IsRelativeToBounds = resource.IsRelativeToBounds,
                AngleDegrees = resource.AngleDegrees,
                OutlineColor = resource.OutlineColor,
                OutlineWidth = resource.OutlineWidth,
            },
            packed));
        Assert.Equal(2f, ReadFloat(packed, 32));
        Assert.Equal(1f, ReadFloat(packed, 48));
        Assert.Equal(1f, ReadFloat(packed, 144));
        Assert.Equal(1f, ReadFloat(packed, 160));

        var resizedVisual = visual with { Bounds = new float4(10, 20, 60, 40) };
        Span<byte> resizedPacked = stackalloc byte[176];
        Assert.Equal(176, UiVisualShaderContract.PackInstance(
            shaderKind,
            in resizedVisual,
            in effect,
            in mask,
            1f,
            resource,
            resizedPacked));
        Assert.NotEqual(ReadFloat(packed, 16), ReadFloat(resizedPacked, 16));
    }

    [Fact]
    public void GeneratedImageArtifactAcceptsAndPacksFullUvRect()
    {
        var program = SolidImageGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var visual = new UiVisualDraw(
            UiVisualKind.Image,
            default,
            new float4(10, 20, 30, 40),
            new float4(1, 1, 1, 1),
            UiClipId.None,
            new UiResourceId(Guid.Parse("00000000-0000-0000-0000-000000000012")));

        Assert.True(UiVisualShaderContract.TryDescribe(
            program,
            visual.Kind,
            UiVisualShaderPath.SolidImage,
            out var shaderKind,
            out var pushConstantSize,
            out var diagnostic), diagnostic);
        Assert.Equal(UiRectangleShaderKind.SolidImage, shaderKind);
        Assert.Equal(8u, pushConstantSize);

        Span<byte> packed = stackalloc byte[48];
        Assert.Equal(48, UiVisualShaderContract.PackInstance(shaderKind, in visual, packed));
        Assert.Equal(1f, ReadFloat(packed, 44));
    }

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
                new float4(1, 2, 3, 4),
                UiEffectSet.None),
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

        Span<byte> packed = stackalloc byte[48];
        Assert.Equal(48, UiVisualShaderContract.PackInstance(shaderKind, in visual, packed));
        Assert.Equal(10f, ReadFloat(packed, 0));
        Assert.Equal(20f, ReadFloat(packed, 4));
        Assert.Equal(1f, ReadFloat(packed, 32));
        Assert.Equal(2f, ReadFloat(packed, 36));
        Assert.Equal(3f, ReadFloat(packed, 40));
        Assert.Equal(4f, ReadFloat(packed, 44));
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
