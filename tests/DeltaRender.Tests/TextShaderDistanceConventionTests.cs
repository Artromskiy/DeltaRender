using Delta.Render.Text;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextShaderDistanceConventionTests
{
    private static readonly string[] FragmentShaders =
    [
        "SdfTextFragment.frag.glsl",
        "SdfTextStrokeFragment.frag.glsl",
        "SdfTextOuterGlowFragment.frag.glsl",
        "SdfTextOuterShadowFragment.frag.glsl",
        "SdfTextStrokeOuterGlowFragment.frag.glsl",
        "MsdfTextFragment.frag.glsl",
        "MsdfTextStrokeFragment.frag.glsl",
        "MsdfTextOuterGlowFragment.frag.glsl",
        "MsdfTextOuterShadowFragment.frag.glsl",
        "MsdfTextStrokeOuterGlowFragment.frag.glsl",
    ];

    private static readonly string[] ShadowFragmentShaders =
    [
        "SdfTextOuterShadowFragment.frag.glsl",
        "MsdfTextOuterShadowFragment.frag.glsl",
    ];

    private static readonly string[] ShadowVertexShaders =
    [
        "SdfTextOuterShadowVertex.vert.glsl",
        "MsdfTextOuterShadowVertex.vert.glsl",
    ];

    [Fact]
    public void GeneratedTextShadersDecodeTheFullSdfDistanceRange()
    {
        foreach (var fileName in FragmentShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Contains("2.0 * pushConstants.member_DistanceRange", source, StringComparison.Ordinal);
        }

        const float distanceRange = 4f;
        Assert.Equal(-distanceRange, Decode(Encode(-distanceRange, distanceRange), distanceRange));
        Assert.Equal(distanceRange, Decode(Encode(distanceRange, distanceRange), distanceRange));
    }

    [Fact]
    public void GeneratedTextShadowsTranslateGeometryAndSampleOriginalUv()
    {
        foreach (var fileName in ShadowVertexShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Contains("member_PixelMin + arg_offset", source, StringComparison.Ordinal);
            Assert.Contains("member_PixelMax + arg_offset", source, StringComparison.Ordinal);
            Assert.Contains("member_UvRect.xy", source, StringComparison.Ordinal);
            Assert.Contains("member_UvRect.zw", source, StringComparison.Ordinal);
        }

        foreach (var fileName in ShadowFragmentShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Contains("texture(Atlas, Uv)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("shadowUvOffset", source, StringComparison.Ordinal);
            Assert.DoesNotContain("UvBounds", source, StringComparison.Ordinal);
            Assert.DoesNotContain("fillCoverage", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OuterShadowGeometryDoesNotChangeGlyphStoragePacking()
    {
        Assert.Equal(48u, SdfTextOuterShadowGraphicsShaderProgram.VertexAbi.Resources[0].Layout.ArrayStride);
        Assert.Equal(48u, MsdfTextOuterShadowGraphicsShaderProgram.VertexAbi.Resources[0].Layout.ArrayStride);

        GlyphInstance glyph = default;
        Span<byte> packed = stackalloc byte[48];
        Assert.Equal(48, SdfTextOuterShadowGraphicsShaderProgram.PackSdfTextOuterShadowVertexGlyphsElement(in glyph, packed));
        Assert.Equal(48, MsdfTextOuterShadowGraphicsShaderProgram.PackMsdfTextOuterShadowVertexGlyphsElement(in glyph, packed));
    }

    [Fact]
    public void FarFieldOutsideTheConfiguredEffectHasZeroCoverage()
    {
        const float distanceRange = 4f;
        const float blurRadius = 2f;
        const float edge = 0.25f;
        var outsideDistance = Decode(Encode(-distanceRange, distanceRange), distanceRange);
        var shadowOutside = MathF.Max(-outsideDistance, 0f);
        var coverage = 1f - SmoothStep(0f, blurRadius + edge, shadowOutside);

        Assert.Equal(0f, coverage);
    }

    private static string ShaderPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "DeltaShader", "DeltaRender.Text", fileName);

    private static float Encode(float signedDistance, float distanceRange) =>
        0.5f + signedDistance / (2f * distanceRange);

    private static float Decode(float sample, float distanceRange) =>
        (sample - 0.5f) * (2f * distanceRange);

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        var t = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
