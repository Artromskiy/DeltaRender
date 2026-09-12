using Delta.Render.Text;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextShaderDistanceConventionTests
{
    private static readonly string[] FragmentShaders =
    [
        "SdfTextFragment.frag.glsl",
        "SdfTextStrokeFragment.frag.glsl",
        "SdfTextOuterShadowFragment.frag.glsl",
        "SdfTextOuterGlowOnlyFragment.frag.glsl",
        "MsdfTextFragment.frag.glsl",
        "MsdfTextStrokeFragment.frag.glsl",
        "MsdfTextOuterShadowFragment.frag.glsl",
        "MsdfTextOuterGlowOnlyFragment.frag.glsl",
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
            Assert.Contains("delta_helper_SignedDistance", source, StringComparison.Ordinal);
            Assert.Contains("(arg_sample - 0.5) * (2.0 * arg_distanceRange)", source, StringComparison.Ordinal);
        }

        const float distanceRange = 4f;
        Assert.Equal(-distanceRange, Decode(Encode(-distanceRange, distanceRange), distanceRange));
        Assert.Equal(distanceRange, Decode(Encode(distanceRange, distanceRange), distanceRange));
    }

    [Fact]
    public void GeneratedTextShadersPremultiplyEffectivePaintAndGlyphColor()
    {
        foreach (var fileName in FragmentShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Contains("vec4 delta_helper_PremultiplyProduct(vec4 arg_color, vec4 arg_glyphColor)", source, StringComparison.Ordinal);
            Assert.Contains("float alpha = arg_color.w * arg_glyphColor.w", source, StringComparison.Ordinal);
            Assert.Contains("alpha * arg_color.xyz * arg_glyphColor.xyz", source, StringComparison.Ordinal);
            Assert.Contains("delta_helper_PremultiplyProduct(arg_", source, StringComparison.Ordinal);
            Assert.DoesNotContain(" * GlyphColor * ", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedTextFragmentsHaveNoDynamicBranches()
    {
        foreach (var fileName in FragmentShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.DoesNotContain("if (", source, StringComparison.Ordinal);
            Assert.DoesNotContain("else", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedTextShadowsTranslateGeometryAndSampleOriginalUv()
    {
        foreach (var fileName in ShadowVertexShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Contains("member_PixelMin + arg_offset", source, StringComparison.Ordinal);
            Assert.Contains("arg_glyph.member_PixelMax - arg_glyph.member_PixelMin", source, StringComparison.Ordinal);
            Assert.DoesNotContain("member_PixelMax + arg_offset", source, StringComparison.Ordinal);
            Assert.Contains("member_UvRect.xy", source, StringComparison.Ordinal);
            Assert.Contains("member_UvRect.zw", source, StringComparison.Ordinal);
        }

        foreach (var fileName in ShadowFragmentShaders)
        {
            var source = File.ReadAllText(ShaderPath(fileName));
            Assert.Matches(@"texture\(Atlas,\s+interstage_slot_0\.xy\)", source);
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
        var shadowOutside = Maths.Max(-outsideDistance, 0f);
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
        var t = Maths.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
