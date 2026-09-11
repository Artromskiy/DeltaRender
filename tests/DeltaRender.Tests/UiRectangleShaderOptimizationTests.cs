using Xunit;

namespace Delta.Render.Tests;

public sealed class UiRectangleShaderOptimizationTests
{
    private static readonly string[] FragmentShaders =
    [
        // The gradient artifact intentionally branches for optional resource outlines.
        "CachedMaskRoundedRectangleFragment.frag.glsl",
        "InnerShadowRoundedRectangleFragment.frag.glsl",
        "RoundedOuterGlowOnlyRectangleFragment.frag.glsl",
        "RoundedOuterShadowOnlyRectangleFragment.frag.glsl",
        "RoundedRectangleFragment.frag.glsl",
        "RoundedStrokeRectangleFragment.frag.glsl",
        "SolidImageRectangleFragment.frag.glsl",
        "SolidOuterGlowOnlyRectangleFragment.frag.glsl",
        "SolidOuterShadowOnlyRectangleFragment.frag.glsl",
        "SolidRectangleFragment.frag.glsl",
        "SolidStrokeRectangleFragment.frag.glsl",
    ];

    [Fact]
    public void GeneratedRoundedRectangleUsesOneBranchlessPerQuadrantDistance()
    {
        var source = File.ReadAllText(ShaderPath("RoundedRectangleFragment.frag.glsl"));

        Assert.Contains("delta_helper_GetCornerRadius", source, StringComparison.Ordinal);
        Assert.DoesNotContain("delta_helper_GetCornerData", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "length("));
    }

    [Fact]
    public void GeneratedUiFragmentsHaveNoDynamicBranches()
    {
        foreach (var fileName in FragmentShaders)
        {
            var branches = Count(File.ReadAllText(ShaderPath(fileName)), "if (");
            Assert.Equal(0, branches);
        }
    }

    private static string ShaderPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "DeltaShader", "DeltaRender.UIShaders", fileName);

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
