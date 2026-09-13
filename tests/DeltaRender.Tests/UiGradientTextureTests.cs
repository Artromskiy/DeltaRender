using Delta;
using Delta.Render.RenderGraph;
using Delta.Render.UIShaders;
using Delta.Render.XAML;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiGradientTextureTests
{
    [Fact]
    public void DescriptionUsesSampledRgba8OneDimensionalLut()
    {
        var description = UiGradientTexture.Description;

        Assert.Equal(UiGradientTexture.Width, description.Width);
        Assert.Equal(UiGradientTexture.Height, description.Height);
        Assert.Equal(RenderTextureFormat.Rgba8Unorm, description.Format);
        Assert.Equal(
            RenderTextureUsage.Sampled | RenderTextureUsage.TransferDestination,
            description.Usage);
    }

    [Fact]
    public void PixelsInterpolateStopsAcrossTheLut()
    {
        var gradient = new UiLinearGradientResource(
            new UiResourceId(Guid.Parse("00000000-0000-0000-0000-000000000021")),
            new float2(0, 0),
            new float2(1, 0),
            PaintUnits.Percent,
            [
                new UiLinearGradientStop(0f, new float4(1, 0, 0, 0.25f)),
                new UiLinearGradientStop(1f, new float4(0, 0, 1, 0.75f)),
            ]);

        var pixels = UiGradientTexture.CreatePixels(gradient);

        Assert.Equal((int)(UiGradientTexture.Width * UiGradientTexture.BytesPerPixel), pixels.Length);
        Assert.Equal(new byte[] { 255, 0, 0, 64 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 191 }, pixels[^4..]);

        var middle = checked((int)(UiGradientTexture.Width / 2 * UiGradientTexture.BytesPerPixel));
        Assert.InRange(pixels[middle], (byte)127, (byte)128);
        Assert.Equal((byte)0, pixels[middle + 1]);
        Assert.InRange(pixels[middle + 2], (byte)127, (byte)128);
        Assert.InRange(pixels[middle + 3], (byte)127, (byte)128);
    }

    [Fact]
    public void CurrentAnalyticGradientAbiHasNoTextureBinding()
    {
        var program = SolidLinearGradientGraphicsShaderProgram.CreateProgram(
            MinimalSpirv,
            MinimalSpirv);

        Assert.False(UiVisualShaderContract.TryGetSampledFragmentTextureBinding(program, out _));
    }

    private static readonly byte[] MinimalSpirv =
    [
        0x03, 0x02, 0x23, 0x07,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];
}
