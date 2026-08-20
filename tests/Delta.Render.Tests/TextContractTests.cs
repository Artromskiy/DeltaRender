using Delta.Render.Core;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextContractTests
{
    private static TextGlyphInstance Glyph(uint page, int x, int y, UiClipRect? clip = null, uint pipeline = 1) =>
        new(new TextAtlasPageId(page), new TextUvRect(0, 0, 0.1f, 0.1f), new TextPixelBounds(x, y, 10, 10),
            new TextColor(1, 1, 1, 1), clip ?? UiClipRect.Unbounded, TextRenderMode.Sdf, 4, 0.01f, pipeline);

    [Fact]
    public void Empty_draw_list_has_no_batches()
    {
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[1];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[1];
        Assert.True(TextBatching.TryBuild(ReadOnlySpan<TextGlyphInstance>.Empty, ordered, batches, out var count, out var batchCount));
        Assert.Equal(0, count);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public void Batching_groups_multiple_pages_pipeline_and_clips_without_per_glyph_draws()
    {
        var clip = new UiClipRect(0, 0, 50, 50);
        var source = new[] { Glyph(1, 0, 0, clip), Glyph(2, 10, 0, clip), Glyph(1, 20, 0, clip), Glyph(1, 30, 0, clip, 2) };
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[4];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[4];
        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var count, out var batchCount));
        Assert.Equal(4, count);
        Assert.Equal(3, batchCount);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(new TextAtlasPageId(1), batches[0].Key.AtlasPage);
        Assert.Equal(new TextAtlasPageId(2), batches[1].Key.AtlasPage);
    }

    [Fact]
    public void Clip_visibility_covers_empty_partial_and_fully_clipped_glyphs_and_resize()
    {
        var metrics = new WindowMetrics(100, 80, 1);
        Assert.False(Glyph(1, 0, 0, new UiClipRect(200, 200, 10, 10)).IsVisible(metrics));
        Assert.True(Glyph(1, 0, 0, new UiClipRect(5, 5, 3, 3)).IsVisible(metrics));
        Assert.False(Glyph(1, 90, 70).IsVisible(new WindowMetrics(50, 40, 2)));
        Assert.False(Glyph(1, 0, 0).IsVisible(new WindowMetrics(0, 0, 1)));
    }

    [Fact]
    public void Text_run_and_invalid_glyphs_are_explicit()
    {
        var run = new TextRun(new[] { Glyph(1, 0, 0) });
        Assert.False(run.IsEmpty);
        Assert.True(run.Glyphs.Span[0].IsValid);
        Assert.False(Glyph(0, 0, 0).IsValid);
        Assert.False((Glyph(1, 0, 0) with { Mode = (TextRenderMode)99 }).IsValid);
    }

    [Fact]
    public void Atlas_dirty_range_validates_pitch_and_source_size()
    {
        var valid = new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[4]);
        Assert.True(valid.IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 0, new byte[4]).IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[3]).IsValid);
    }

    [Fact]
    public void Shader_artifact_contract_reports_sampled_resource_blocker_without_new_manifest()
    {
        var bytes = new byte[20];
        var vertex = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = Delta.Shader.Abstractions.ShaderStage.Vertex
        });
        var fragment = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = Delta.Shader.Abstractions.ShaderStage.Fragment
        });
        var diagnostic = TextShaderArtifactContract.Validate(new GraphicsShaderProgram(vertex, fragment));
        Assert.Equal(TextShaderArtifactStatus.SampledImageAbiUnavailable, diagnostic.Status);
    }
}
