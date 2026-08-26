using DeltaRender;
using DeltaRender.Text;
using DeltaText.Contract;
using Xunit;

namespace DeltaRender.Tests;

public sealed class TextAtlasCacheTests
{
    [Fact]
    public void CacheKeyIncludesCanonicalRasterIdentity()
    {
        var font = new FontInstanceId(7, 2);
        var sdf = new TextGlyphCacheKey(font, 0, 16, TextRenderMode.Sdf, 4);
        var differentGlyph = sdf with { GlyphId = 1 };
        var differentSize = sdf with { PixelsPerEm = 18 };
        var differentMode = sdf with { Mode = TextRenderMode.Msdf };

        Assert.True(sdf.IsValid);
        Assert.NotEqual(sdf, differentGlyph);
        Assert.NotEqual(sdf, differentSize);
        Assert.NotEqual(sdf, differentMode);
    }

    [Fact]
    public async Task EmptyCacheBorrowIsAllocationFreeAndDisposeIsIdempotent()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
        {
            var view = cache.BorrowPages();
            Assert.True(view.IsEmpty);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
        await cache.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => cache.BorrowPages());
        Assert.Empty(device.Pages);
    }

    [Fact]
    public async Task StaleAndDisposedHandlesAreRejectedWithoutLegacyBitmapTypes()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device);
        var handle = new TextGlyphCacheHandle(new TextAtlasPageId(1), 1, 1);

        Assert.False(cache.TryGet(handle, out _));
        await cache.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(handle, out _));
    }

    [Fact]
    public void PositionedCanonicalGlyphKeepsFractionalShapingOffsetsUntilPixelConversion()
    {
        var placement = new TextGlyphPlacement(
            new TextGlyphCacheHandle(new TextAtlasPageId(1), 1, 1),
            new GlyphAtlasRegion(new TextAtlasPageId(1), new TextUvRect(0, 0, 0.1f, 0.1f)),
            1.25f,
            2f,
            3,
            4,
            0,
            TextRenderMode.Sdf,
            4,
            0);
        var glyph = new ShapedGlyph(0, 0, 1, 0, 0.5f, -0.25f, GlyphSafety.None);

        var instance = placement.ToInstance(in glyph, 10.25f, 20.5f, new TextColor(1, 1, 1, 1), UiClipRect.Unbounded);

        Assert.Equal((int)MathF.Round(10.25f + 0.5f + 1.25f), instance.PixelBounds.X);
        Assert.Equal((int)MathF.Round(20.5f - 0.25f - 2f), instance.PixelBounds.Y);
    }

    private sealed class FakeAtlasDevice : ITextAtlasDevice
    {
        public List<FakeAtlasPage> Pages { get; } = [];

        public TextAtlasUploadStatistics AtlasUploadStatistics => default;

        public ITextAtlasPage CreateAtlasPage(in TextAtlasPageDescription description)
        {
            var page = new FakeAtlasPage(description);
            Pages.Add(page);
            return page;
        }

        public bool UploadAtlasPage(ITextAtlasPage page, ReadOnlySpan<byte> pixels, uint sourceRowPitch) => false;

        public bool UploadAtlasDirtyRanges(ITextAtlasPage page, ReadOnlySpan<TextAtlasDirtyRange> ranges) => false;
    }

    private sealed class FakeAtlasPage(TextAtlasPageDescription description) : ITextAtlasPage
    {
        public TextAtlasPageDescription Description { get; } = description;

        public bool IsDisposed => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
