using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Delta.Render;
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
    public void SubmissionBatchingAcceptsLargerOrderedScratchThanBatchScratch()
    {
        var record = new TextSubmissionRecord(
            new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 12, 1),
            TextAnchor.ScreenPixels(new TextScreenAnchor(0, 0)),
            new TextRun(new[] { Glyph(1, 0, 0) }),
            UiClipRect.Unbounded,
            1,
            0);
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[256];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[1];

        Assert.True(TextSubmissionBatching.TryBuild(
            new TextSubmissionFrame(new[] { record }),
            new TextProjectionContext(128, 128, 1),
            null,
            ordered,
            batches,
            out var orderedCount,
            out var batchCount,
            out var rejected));
        Assert.Equal(1, orderedCount);
        Assert.Equal(1, batchCount);
        Assert.Equal(0, rejected);
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
    public void UiRenderBatchAdapterPreservesCanonicalPayloadAndBorrowLifetime()
    {
        using var adapter = new UiRenderBatchAdapter();
        var clip = new UiClipRect(3, 4, 20, 21);
        var glyphs = new[] { Glyph(8, 11, 13) };
        var text = new[]
        {
            new TextSubmissionRecord(
                new TextSubmissionHandle(TextSubmissionOwnerKind.Entity, 42, 7),
                TextAnchor.ScreenPixels(new TextScreenAnchor(17.5f, 23.25f)),
                new TextRun(glyphs),
                clip,
                9,
                4)
        };
        var rectangles = new[] { new UiQuad(1, 2, 3, 4, 0.1f, 0.2f, 0.3f, 0.4f) { Clip = clip } };
        var payload = new byte[] { 2, 4, 8, 16 };
        var dirty = new[] { RenderRecordChange.Upsert(42, 11, payload) };

        var frame = adapter.Replace(rectangles, text, dirty);
        var batch = adapter.Borrow(in frame);

        Assert.Equal(rectangles[0], batch.Rectangles[0]);
        Assert.Equal(text[0], batch.TextSubmissions[0]);
        Assert.Equal(text[0].Glyphs, batch.TextSubmissions[0].Glyphs);
        Assert.Equal(11, batch.TextSubmissions[0].Glyphs.Glyphs.Span[0].PixelBounds.X);
        Assert.Equal(13, batch.TextSubmissions[0].Glyphs.Glyphs.Span[0].PixelBounds.Y);
        Assert.Equal(dirty[0], batch.DirtyRecords[0]);
        Assert.Equal(payload, batch.DirtyRecords[0].Payload.ToArray());

        var nextFrame = adapter.Replace(
            ReadOnlySpan<UiQuad>.Empty,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty);

        Assert.Throws<InvalidOperationException>(() => adapter.Borrow(in frame));
        Assert.True(adapter.Borrow(in nextFrame).IsEmpty);
    }

    [Fact]
    public void UiRenderBatchAdapterPreservesResourceClipIdentityAndDrawDelta()
    {
        using var adapter = new UiRenderBatchAdapter();
        var rootClip = new UiRenderClipEntry(
            new UiRenderClipId(1),
            new UiClipRect(0, 0, 320, 180),
            default);
        var nestedClip = new UiRenderClipEntry(
            new UiRenderClipId(2),
            new UiClipRect(10, 20, 100, 80),
            rootClip.Id);
        var rectangle = new UiQuad(10, 20, 100, 80, 1, 1, 1, 1)
        {
            Kind = UiRenderDrawKind.Image,
            Clip = nestedClip.Bounds,
            ClipId = nestedClip.Id,
            Resource = new UiRenderResourceHandle(17, 3),
            OwnerId = 42,
            ZIndex = 5,
            Order = 9
        };
        var delta = new UiRenderDrawDelta(
            new UiRenderRange(1, 1),
            new UiRenderRange(0, 2),
            new UiRenderRange(3, 1),
            7,
            8);

        var frame = adapter.Replace(
            [rectangle],
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            [rootClip, nestedClip],
            in delta);
        var batch = adapter.Borrow(in frame);

        Assert.Equal(rectangle, batch.Rectangles[0]);
        Assert.Equal(UiRenderDrawKind.Image, batch.Rectangles[0].Kind);
        Assert.Equal(new UiRenderResourceHandle(17, 3), batch.Rectangles[0].Resource);
        Assert.Equal(nestedClip.Id, batch.Rectangles[0].ClipId);
        Assert.Equal(rootClip, batch.Clips[0]);
        Assert.Equal(nestedClip, batch.Clips[1]);
        Assert.Equal(delta, batch.Delta);

        var nextDelta = new UiRenderDrawDelta(default, default, default, 8, 8);
        var nextFrame = adapter.Replace(
            ReadOnlySpan<UiQuad>.Empty,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            ReadOnlySpan<UiRenderClipEntry>.Empty,
            in nextDelta);

        Assert.Throws<InvalidOperationException>(() => adapter.Borrow(in frame));
        Assert.True(adapter.Borrow(in nextFrame).IsEmpty);
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

    [Fact]
    public void UiRenderFrameSourcePreservesBatchAndAtlasBackingWithoutWarmFrameAllocations()
    {
        using var source = new TestFrameSource();
        var rectangles = new[] { new UiQuad(1, 2, 3, 4, 1, 1, 1, 1) };
        var pages = new ITextAtlasPage[]
        {
            new TestAtlasPage(new TextAtlasPageDescription(new TextAtlasPageId(7), 64, 64, TextAtlasFormat.R8Unorm))
        };
        source.Prepare(rectangles, ReadOnlySpan<TextSubmissionRecord>.Empty, ReadOnlySpan<RenderRecordChange>.Empty, pages);

        var first = source.BorrowFrame();
        Assert.Equal(rectangles[0], first.Batch.Rectangles[0]);
        Assert.Same(pages[0], first.AtlasPages[0]);
        var second = source.BorrowFrame();
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(first.Batch.Rectangles),
            ref MemoryMarshal.GetReference(second.Batch.Rectangles)));
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(first.AtlasPages),
            ref MemoryMarshal.GetReference(second.AtlasPages)));

        _ = source.BorrowFrame().Batch.Rectangles.Length;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var observedCount = 0;
        for (var index = 0; index < 32; index++)
        {
            var view = source.BorrowFrame();
            observedCount += view.Batch.Rectangles.Length + view.AtlasPages.Length;
        }

        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(64, observedCount);
        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    [Fact]
    public void UiRenderFrameSourceKeepsBatchAndPagesFromTheSamePreparedFrame()
    {
        using var source = new TestFrameSource();
        var firstPages = new ITextAtlasPage[]
        {
            new TestAtlasPage(new TextAtlasPageDescription(new TextAtlasPageId(8), 64, 64, TextAtlasFormat.R8Unorm))
        };
        var secondPages = new ITextAtlasPage[]
        {
            new TestAtlasPage(new TextAtlasPageDescription(new TextAtlasPageId(9), 64, 64, TextAtlasFormat.Rgba8Unorm))
        };
        var firstToken = source.Prepare(new[] { new UiQuad(1, 1, 2, 2, 1, 0, 0, 1) },
            ReadOnlySpan<TextSubmissionRecord>.Empty, ReadOnlySpan<RenderRecordChange>.Empty, firstPages);
        var first = source.BorrowFrame();
        Assert.Equal(firstToken, first.Batch.Frame);
        Assert.Equal(new TextAtlasPageId(8), first.AtlasPages[0].Description.Id);

        source.Prepare(new[] { new UiQuad(3, 3, 4, 4, 0, 1, 0, 1) },
            ReadOnlySpan<TextSubmissionRecord>.Empty, ReadOnlySpan<RenderRecordChange>.Empty, secondPages);
        var second = source.BorrowFrame();
        Assert.NotEqual(first.Batch.Frame, second.Batch.Frame);
        Assert.Equal(new TextAtlasPageId(9), second.AtlasPages[0].Description.Id);
        Assert.Equal(3, second.Batch.Rectangles[0].X);
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

    private sealed class TestFrameSource : IUiRenderFrameSource, IDisposable
    {
        private readonly UiRenderBatchAdapter _adapter = new();
        private UiRenderFrameToken _token;
        private ITextAtlasPage[] _pages = [];

        public UiRenderFrameToken Prepare(
            ReadOnlySpan<UiQuad> rectangles,
            ReadOnlySpan<TextSubmissionRecord> textSubmissions,
            ReadOnlySpan<RenderRecordChange> dirtyRecords,
            ITextAtlasPage[] pages)
        {
            _pages = pages;
            _token = _adapter.Replace(rectangles, textSubmissions, dirtyRecords);
            return _token;
        }

        public UiRenderFrameView BorrowFrame()
        {
            var batch = _adapter.Borrow(in _token);
            return new UiRenderFrameView(batch, _pages);
        }

        public void Dispose() => _adapter.Dispose();
    }

    private sealed class TestAtlasPage(TextAtlasPageDescription description) : ITextAtlasPage
    {
        public TextAtlasPageDescription Description { get; } = description;
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

}
