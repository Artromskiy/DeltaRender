using Delta.Render.Core;
using Delta.Shader.Abstractions;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextContractTests
{
    private static TextGlyphInstance Glyph(uint page, int x, int y, UiClipRect? clip = null, uint pipeline = 1) =>
        new(new TextAtlasPageId(page), new TextUvRect(0, 0, 0.1f, 0.1f), new TextPixelBounds(x, y, 10, 10),
            new TextColor(1, 1, 1, 1), clip ?? UiClipRect.Unbounded, TextRenderMode.Sdf, 4, 0.01f, pipeline);

    [Fact]
    public void EmptyDrawListHasNoBatches()
    {
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[1];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[1];
        Assert.True(TextBatching.TryBuild(ReadOnlySpan<TextGlyphInstance>.Empty, ordered, batches, out var count, out var batchCount));
        Assert.Equal(0, count);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public void BatchingPreservesMultiplePagesPipelineAndClipsWithoutPerGlyphDraws()
    {
        var clip = new UiClipRect(0, 0, 50, 50);
        var source = new[] { Glyph(1, 0, 0, clip), Glyph(2, 10, 0, clip), Glyph(1, 20, 0, clip), Glyph(1, 30, 0, clip, 2) };
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[4];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[4];
        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var count, out var batchCount));
        Assert.Equal(4, count);
        Assert.Equal(4, batchCount);
        Assert.Equal(1, batches[0].Count);
        Assert.Equal(new TextAtlasPageId(1), batches[0].Key.AtlasPage);
        Assert.Equal(new TextAtlasPageId(2), batches[1].Key.AtlasPage);
    }

    [Fact]
    public void ClipVisibilityCoversEmptyPartialAndFullyClippedGlyphsAndResize()
    {
        var metrics = new WindowMetrics(100, 80, 1);
        Assert.False(Glyph(1, 0, 0, new UiClipRect(200, 200, 10, 10)).IsVisible(metrics));
        Assert.True(Glyph(1, 0, 0, new UiClipRect(5, 5, 3, 3)).IsVisible(metrics));
        Assert.False(Glyph(1, 90, 70).IsVisible(new WindowMetrics(50, 40, 2)));
        Assert.False(Glyph(1, 0, 0).IsVisible(new WindowMetrics(0, 0, 1)));
    }

    [Fact]
    public void TextRunAndInvalidGlyphsAreExplicit()
    {
        var run = new TextRun(new[] { Glyph(1, 0, 0) });
        Assert.False(run.IsEmpty);
        Assert.True(run.Glyphs.Span[0].IsValid);
        Assert.False(Glyph(0, 0, 0).IsValid);
        Assert.False((Glyph(1, 0, 0) with { Mode = (TextRenderMode)99 }).IsValid);
    }

    [Fact]
    public void AtlasDirtyRangeValidatesPitchAndSourceSize()
    {
        var valid = new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[4]);
        Assert.True(valid.IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 0, new byte[4]).IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[3]).IsValid);
    }

    [Fact]
    public void ShaderArtifactContractReportsInvalidWhenManifestIsIncomplete()
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
        Assert.Equal(TextShaderArtifactStatus.Invalid, diagnostic.Status);
    }

    [Fact]
    public void ShaderArtifactContractAcceptsSameBindingNumberInDifferentSets()
    {
        var bytes = new byte[20];
        var vertex = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Version = Delta.Shader.Abstractions.ShaderAbiManifest.CurrentVersion,
            Stage = Delta.Shader.Abstractions.ShaderStage.Vertex,
            EntryPointName = "main",
            PushConstants = new[]
            {
                new Delta.Shader.Abstractions.ShaderAbiPushConstant
                {
                    Size = 16
                }
            },
            Resources = new[]
            {
                new Delta.Shader.Abstractions.ShaderAbiResource
                {
                    Name = "glyphs",
                    Category = "storage-buffer",
                    Stage = Delta.Shader.Abstractions.ShaderStage.Vertex,
                    Set = 0,
                    Binding = 0,
                    Access = Delta.Shader.Abstractions.ShaderResourceAccess.ReadOnly,
                    Layout = "std430",
                    ReadOnly = true
                }
            }
        });
        var fragment = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Version = Delta.Shader.Abstractions.ShaderAbiManifest.CurrentVersion,
            Stage = Delta.Shader.Abstractions.ShaderStage.Fragment,
            EntryPointName = "main",
            PushConstants = new[]
            {
                new Delta.Shader.Abstractions.ShaderAbiPushConstant
                {
                    Size = 16
                }
            },
            Resources = new[]
            {
                new Delta.Shader.Abstractions.ShaderAbiResource
                {
                    Name = "atlas",
                    Category = "sampled-texture",
                    Stage = Delta.Shader.Abstractions.ShaderStage.Fragment,
                    Set = 1,
                    Binding = 0,
                    Access = Delta.Shader.Abstractions.ShaderResourceAccess.ReadOnly
                }
            }
        });

        var valid = TextShaderArtifactContract.TryDescribe(new GraphicsShaderProgram(vertex, fragment), out var layout, out var diagnostic);
        Assert.True(valid);
        Assert.Equal(TextShaderArtifactStatus.Ready, diagnostic.Status);
        Assert.Equal(0u, layout.VertexStorageSet);
        Assert.Equal(0u, layout.VertexStorageBinding);
        Assert.Equal(1u, layout.TextureSet);
        Assert.Equal(0u, layout.TextureBinding);
        Assert.Equal(16u, layout.PushConstantSize);
    }

    [Fact]
    public void Gray8AtlasFixtureExposesPageAndGlyphContracts()
    {
        var fixture = AtlasFixture.Load();

        Assert.Equal(new TextAtlasPageDescription(new TextAtlasPageId(1), 256, 256, TextAtlasFormat.R8Unorm), fixture.Description);
        Assert.Equal(3, fixture.Summary.Glyphs.Length);
        Assert.All(fixture.Summary.Glyphs, glyph =>
        {
            Assert.Equal(0, glyph.PageIndex);
            Assert.True(glyph.U1 > glyph.U0);
            Assert.True(glyph.V1 > glyph.V0);
            Assert.True(glyph.Width > 0);
            Assert.True(glyph.Height > 0);
        });
        Assert.Contains(fixture.Pixels, pixel => pixel != 0);
    }

    [Fact]
    public void TextBatchingPreservesGroupedInstanceOrderAndBatchCounts()
    {
        var clipA = new UiClipRect(0, 0, 32, 32);
        var clipB = new UiClipRect(8, 8, 16, 16);
        var source = new[]
        {
            new TextGlyphInstance(new TextAtlasPageId(1), new TextUvRect(0, 0, 0.2f, 0.2f), new TextPixelBounds(0, 0, 10, 10), new TextColor(1, 1, 1, 1), clipA, TextRenderMode.Sdf, 4, 0.01f, 7),
            new TextGlyphInstance(new TextAtlasPageId(1), new TextUvRect(0.2f, 0, 0.2f, 0.2f), new TextPixelBounds(10, 0, 10, 10), new TextColor(1, 1, 1, 1), clipA, TextRenderMode.Sdf, 4, 0.01f, 7),
            new TextGlyphInstance(new TextAtlasPageId(2), new TextUvRect(0, 0, 0.2f, 0.2f), new TextPixelBounds(0, 12, 10, 10), new TextColor(1, 1, 1, 1), clipB, TextRenderMode.Sdf, 4, 0.01f, 7)
        };

        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[source.Length];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[source.Length];

        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var orderedCount, out var batchCount));
        Assert.Equal(3, orderedCount);
        Assert.Equal(2, batchCount);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(1, batches[1].Count);
        Assert.Equal(source[0].BatchKey, batches[0].Key);
        Assert.Equal(source[2].BatchKey, batches[1].Key);
    }
}
