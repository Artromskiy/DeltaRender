using Delta.Render.Core;
using Delta.Shader.Abstractions;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextSubmissionContractTests
{
    [Fact]
    public void EntityAndXamlHandlesShareGenerationSafeNeutralShape()
    {
        var entity = new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 7, 3);
        var xaml = new TextSubmissionHandle(TextSubmissionOwnerKind.XamlElement, 9, 4);

        Assert.True(entity.IsValid);
        Assert.True(xaml.IsValid);
        Assert.NotEqual(entity, xaml);
        Assert.False(new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 7, 0).IsValid);
    }

    [Fact]
    public void ScreenRecordsAreResolvedAndGroupedWithoutPerGlyphApi()
    {
        var glyphs = new[]
        {
            Glyph(1, 0, 0),
            Glyph(1, 12, 0)
        };
        var record = new TextSubmissionRecord(
            new TextSubmissionHandle(TextSubmissionOwnerKind.XamlElement, 1, 1),
            TextAnchor.ScreenPixels(new TextScreenAnchor(10, 20)),
            new TextRun(glyphs),
            new UiClipRect(0, 0, 100, 100),
            1,
            0);
        var records = new[] { record };
        var ordered = new TextGlyphInstance[2];
        var batches = new TextBatchRange[2];

        var ok = TextSubmissionBatching.TryBuild(
            new TextSubmissionFrame(records),
            new TextProjectionContext(128, 128, 1),
            null,
            ordered,
            batches,
            out var orderedCount,
            out var batchCount,
            out var rejected);

        Assert.True(ok);
        Assert.Equal(2, orderedCount);
        Assert.Equal(1, batchCount);
        Assert.Equal(0, rejected);
        Assert.Equal(10, ordered[0].PixelBounds.X);
        Assert.Equal(20, ordered[0].PixelBounds.Y);
    }

    [Fact]
    public void WorldRecordsRequireProjectionAndClipIsIntersected()
    {
        var glyph = Glyph(2, 0, 0, new UiClipRect(0, 0, 50, 50));
        var record = new TextSubmissionRecord(
            new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 2, 1),
            TextAnchor.WorldSpace(new TextWorldAnchor(1, 2, 3)),
            new TextRun(new[] { glyph }),
            new UiClipRect(10, 10, 20, 20),
            2,
            0);
        var ordered = new TextGlyphInstance[1];
        var batches = new TextBatchRange[1];

        Assert.True(TextSubmissionBatching.TryBuild(
            new TextSubmissionFrame(new[] { record }),
            new TextProjectionContext(128, 128, 2),
            new FixedProjection(30, 40),
            ordered,
            batches,
            out var count,
            out _,
            out var rejected));
        Assert.Equal(1, count);
        Assert.Equal(0, rejected);
        Assert.Equal(new UiClipRect(10, 10, 20, 20), ordered[0].Clip);
        Assert.Equal(30, ordered[0].PixelBounds.X);
        Assert.Equal(40, ordered[0].PixelBounds.Y);

        Assert.True(TextSubmissionBatching.TryBuild(
            new TextSubmissionFrame(new[] { record }),
            new TextProjectionContext(128, 128, 2),
            null,
            ordered,
            batches,
            out count,
            out _,
            out rejected));
        Assert.Equal(0, count);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void InvalidAndEmptyRecordsAreRejectedWithoutThrowing()
    {
        var invalid = new TextSubmissionRecord(
            new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 4, 1),
            TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)),
            new TextRun(ReadOnlyMemory<TextGlyphInstance>.Empty),
            UiClipRect.Unbounded,
            1,
            0);
        var ordered = new TextGlyphInstance[1];
        var batches = new TextBatchRange[1];

        Assert.True(TextSubmissionBatching.TryBuild(
            new TextSubmissionFrame(new[] { invalid }),
            new TextProjectionContext(64, 64, 1),
            null,
            ordered,
            batches,
            out var count,
            out var batchCount,
            out var rejected));
        Assert.Equal(0, count);
        Assert.Equal(0, batchCount);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void DirtyChangesAndLifetimeGenerationRejectStaleRecords()
    {
        var owner = new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 8, 4);
        var record = new TextSubmissionRecord(owner, TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)),
            new TextRun(new[] { Glyph(3, 0, 0) }), UiClipRect.Unbounded, 9, 0);

        Assert.True(record.Matches(owner, 9));
        Assert.False(record.Matches(new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 8, 5), 9));
        Assert.True(new TextSubmissionChange(TextSubmissionChangeKind.Upserted, owner, 9, record).IsValid);
        Assert.True(new TextSubmissionChange(TextSubmissionChangeKind.Removed, owner, 10, default).IsValid);
        Assert.False(new TextSubmissionChange(TextSubmissionChangeKind.Upserted, owner, 10, record).IsValid);
    }

    [Fact]
    public void SubmissionBatchesPreserveSnapshotOrderInsteadOfMergingAcrossRuns()
    {
        var first = new TextSubmissionRecord(new TextSubmissionHandle(TextSubmissionOwnerKind.XamlElement, 1, 1),
            TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)), new TextRun(new[] { Glyph(4, 0, 0) }),
            UiClipRect.Unbounded, 1, 0);
        var middle = first with
        {
            Anchor = TextAnchor.ScreenPixels(new TextScreenAnchor(10, 0)),
            Glyphs = new TextRun(new[] { Glyph(5, 0, 0) }),
            Order = 1
        };
        var last = first with { Anchor = TextAnchor.ScreenPixels(new TextScreenAnchor(20, 0)), Order = 2 };
        var ordered = new TextGlyphInstance[3];
        var batches = new TextBatchRange[3];

        Assert.True(TextSubmissionBatching.TryBuild(new TextSubmissionFrame(new[] { first, middle, last }),
            new TextProjectionContext(64, 64, 1), null, ordered, batches,
            out var glyphCount, out var batchCount, out var rejected));
        Assert.Equal(3, glyphCount);
        Assert.Equal(3, batchCount);
        Assert.Equal(0, rejected);
        Assert.Equal(0, batches[0].Start);
        Assert.Equal(1, batches[1].Start);
    }

    [Fact]
    public void TextBatchingPreservesABADrawOrder()
    {
        var source = new[] { Glyph(4, 0, 0), Glyph(5, 10, 0), Glyph(4, 20, 0) };
        var ordered = new TextGlyphInstance[3];
        var batches = new TextBatchRange[3];

        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var glyphCount, out var batchCount));
        Assert.Equal(3, glyphCount);
        Assert.Equal(3, batchCount);
        Assert.Equal(new TextAtlasPageId(4), batches[0].Key.AtlasPage);
        Assert.Equal(new TextAtlasPageId(5), batches[1].Key.AtlasPage);
        Assert.Equal(new TextAtlasPageId(4), batches[2].Key.AtlasPage);
    }

    [Fact]
    public void TextSubmissionExtensionReachesExistingUiTextSubmitSeam()
    {
        using var session = new RecordingSession();
        using var uiPipeline = new FakePipeline();
        using var textPipeline = new FakePipeline();
        var uiQuads = Array.Empty<UiQuad>();
        var uiDrawList = new UiDrawList(uiQuads);
        var records = new[]
        {
            new TextSubmissionRecord(new TextSubmissionHandle(TextSubmissionOwnerKind.XamlElement, 1, 1),
                TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)), new TextRun(new[] { Glyph(6, 0, 0) }),
                UiClipRect.Unbounded, 1, 0)
        };
        var ordered = new TextGlyphInstance[1];
        var batches = new TextBatchRange[1];

        var submitted = session.SubmitTextSubmission(
            uiPipeline,
            new GraphicsFrameParameters(64, 64, 0),
            in uiDrawList,
            textPipeline,
            new TextFrameParameters(64, 64, 0, new TextColor(1, 1, 1, 1), new TextColor(0, 0, 0, 1), 0),
            ReadOnlySpan<ITextAtlasPage>.Empty,
            new TextSubmissionFrame(records),
            new TextProjectionContext(64, 64, 1),
            null,
            ordered,
            batches,
            ReadOnlySpan<RenderRecordChange>.Empty);

        Assert.True(submitted);
        Assert.Equal(1, session.SubmitCount);
        Assert.Single(session.SubmittedGlyphs);
        Assert.Equal(new TextAtlasPageId(6), session.SubmittedGlyphs[0].AtlasPage);
    }

    [Fact]
    public void UiRenderBatchAdapterPreservesRectanglesTextAndDirtySelection()
    {
        using var adapter = new UiRenderBatchAdapter();
        var rectangles = new[] { new UiQuad(1, 2, 3, 4, 1, 0, 0, 1) };
        var text = new[]
        {
            new TextSubmissionRecord(
                new TextSubmissionHandle(TextSubmissionOwnerKind.XamlElement, 2, 1),
                TextAnchor.ScreenPixels(new TextScreenAnchor(4, 5)),
                new TextRun(new[] { Glyph(8, 0, 0) }),
                UiClipRect.Unbounded,
                1,
                0)
        };
        var dirty = new[] { RenderRecordChange.Remove(3, 9) };

        var frame = adapter.Replace(rectangles, text, dirty);
        var batch = adapter.Borrow(in frame);

        Assert.Equal(1, batch.Rectangles.Length);
        Assert.Equal(rectangles[0], batch.Rectangles[0]);
        Assert.Equal(1, batch.TextSubmissions.Length);
        Assert.Equal(1, batch.DirtyRecords.Length);
        Assert.Equal(dirty[0], batch.DirtyRecords[0]);

        var emptyFrame = adapter.Replace(ReadOnlySpan<UiQuad>.Empty, ReadOnlySpan<TextSubmissionRecord>.Empty, ReadOnlySpan<RenderRecordChange>.Empty);
        Assert.True(adapter.Borrow(in emptyFrame).IsEmpty);
        Assert.NotEqual(frame, emptyFrame);
    }

    [Fact]
    public void UiRenderBatchSubmissionUsesTheCombinedUiTextAndDirtySeam()
    {
        using var session = new RecordingSession();
        using var uiPipeline = new FakePipeline();
        using var textPipeline = new FakePipeline();
        using var adapter = new UiRenderBatchAdapter();
        var frame = adapter.Replace(
            new[] { new UiQuad(1, 2, 3, 4, 1, 1, 1, 1) },
            new[]
            {
                new TextSubmissionRecord(
                    new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 3, 1),
                    TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)),
                    new TextRun(new[] { Glyph(9, 0, 0) }),
                    UiClipRect.Unbounded,
                    1,
                    0)
            },
            new[] { RenderRecordChange.Remove(4, 5) });
        var batch = adapter.Borrow(in frame);
        var ordered = new TextGlyphInstance[1];
        var ranges = new TextBatchRange[1];

        Assert.True(session.Submit(
            uiPipeline,
            new GraphicsFrameParameters(64, 64, 0),
            textPipeline,
            new TextFrameParameters(64, 64, 0, new TextColor(1, 1, 1, 1), new TextColor(0, 0, 0, 1), 0),
            ReadOnlySpan<ITextAtlasPage>.Empty,
            in batch,
            new TextProjectionContext(64, 64, 1),
            null,
            ordered,
            ranges));

        Assert.Equal(1, session.SubmitCount);
        Assert.Single(session.SubmittedQuads);
        Assert.Single(session.SubmittedDirtyRecords);
        Assert.Single(session.SubmittedGlyphs);
    }

    [Fact]
    public void UiRenderBatchRejectsStaleAndDisposedFrameTokens()
    {
        using var adapter = new UiRenderBatchAdapter();
        var frame = adapter.Replace(
            ReadOnlySpan<UiQuad>.Empty,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty);

        _ = adapter.Borrow(in frame);
        _ = adapter.Replace(
            ReadOnlySpan<UiQuad>.Empty,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty);
        Assert.Throws<InvalidOperationException>(() => adapter.Borrow(in frame));

        adapter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => adapter.Borrow(in frame));
    }

    private static TextGlyphInstance Glyph(uint page, int x, int y, UiClipRect? clip = null) =>
        new(new TextAtlasPageId(page), new TextUvRect(0, 0, 0.1f, 0.1f), new TextPixelBounds(x, y, 10, 10),
            new TextColor(1, 1, 1, 1), clip ?? UiClipRect.Unbounded, TextRenderMode.Sdf, 4, 0.01f, 11);

    private sealed class FixedProjection(float x, float y) : ITextWorldProjection
    {
        public bool TryProject(in TextWorldAnchor anchor, in TextProjectionContext context, out TextScreenAnchor screen)
        {
            screen = new TextScreenAnchor(x, y);
            return true;
        }
    }

    private sealed class FakePipeline : IGraphicsPipeline, IDisposable
    {
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSession : IRenderWindowFrameSession, IDisposable
    {
        public void Dispose() { }
        public int SubmitCount { get; private set; }
        public UiQuad[] SubmittedQuads { get; private set; } = Array.Empty<UiQuad>();
        public RenderRecordChange[] SubmittedDirtyRecords { get; private set; } = Array.Empty<RenderRecordChange>();
        public TextGlyphInstance[] SubmittedGlyphs { get; private set; } = Array.Empty<TextGlyphInstance>();
        public RenderWindowId WindowId => new(Guid.Empty);

        public RenderFrameState BeginFrame() => RenderFrameState.Ready(0, new WindowMetrics(64, 64, 1));
        public bool EndFrame(in RenderFrameState frameState, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public bool EndFrame(in RenderFrameState frameState, IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters, ReadOnlySpan<UiQuad> uiQuads, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public bool EndFrame(in RenderFrameState frameState, IGraphicsPipeline uiPipeline, in GraphicsFrameParameters uiParameters, ReadOnlySpan<UiQuad> uiQuads, IGraphicsPipeline textPipeline, in TextFrameParameters textParameters, ReadOnlySpan<ITextAtlasPage> atlasPages, in TextDrawList textDrawList, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public bool EndFrame(in RenderFrameState frameState, IGraphicsPipeline uiPipeline, in GraphicsFrameParameters uiParameters, ReadOnlySpan<UiQuad> uiQuads, IGraphicsPipeline textPipeline, in TextFrameParameters textParameters, ReadOnlySpan<TextGlyphInstance> textGlyphs, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public bool EndFrame(in RenderFrameState frameState, IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters, in UiDrawList drawList, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public bool SubmitFrame(IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters, in UiDrawList drawList, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;

        public bool SubmitFrame(IGraphicsPipeline uiPipeline, in GraphicsFrameParameters uiParameters, in UiDrawList uiDrawList, IGraphicsPipeline textPipeline, in TextFrameParameters textParameters, ReadOnlySpan<ITextAtlasPage> atlasPages, in TextDrawList textDrawList, ReadOnlySpan<RenderRecordChange> dirtyRecords)
        {
            SubmitCount++;
            SubmittedQuads = uiDrawList.Quads.ToArray();
            SubmittedDirtyRecords = dirtyRecords.ToArray();
            SubmittedGlyphs = textDrawList.Glyphs.ToArray();
            return true;
        }

        public bool SubmitFrame(IGraphicsPipeline uiPipeline, in GraphicsFrameParameters uiParameters, in UiDrawList uiDrawList, IGraphicsPipeline textPipeline, in TextFrameParameters textParameters, in TextDrawList textDrawList, ReadOnlySpan<RenderRecordChange> dirtyRecords) => false;
        public IGraphicsPipeline CreateTextPipeline(in GraphicsShaderProgram shaderProgram) => throw new NotSupportedException();
        public IGraphicsPipeline CreateGraphicsPipeline(in GraphicsShaderProgram shaderProgram) => throw new NotSupportedException();
        public ITextAtlasDevice CreateTextAtlasDevice() => throw new NotSupportedException();
        public bool DrawFullscreenTriangle(IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters) => false;
        public bool Resize(WindowMetrics metrics) => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
