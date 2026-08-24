using Delta.Render.Core;
using Delta.Render.Text;
using Delta.Text;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextAtlasCacheTests
{
    [Fact]
    public async Task InsertUploadsOnceAndCacheHitReusesPlacement()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(64, 64, 2, 4));
        var glyph = Glyph(7, GlyphAtlasMode.Grayscale);

        var first = cache.GetOrAdd(in glyph, out var firstPlacement);
        var second = cache.GetOrAdd(in glyph, out var secondPlacement);

        Assert.Equal(first, second);
        Assert.Equal(firstPlacement, secondPlacement);
        Assert.Equal(1, device.UploadCount);
        Assert.True(firstPlacement.Uv.IsValid);
        var positioned = glyph.Glyph;
        var instance = firstPlacement.ToInstance(in positioned, 10.25f, 20.5f, new TextColor(1, 1, 1, 1), UiClipRect.Unbounded);
        Assert.Equal((int)MathF.Round(10.25f + positioned.X + positioned.OffsetX + firstPlacement.BearingX), instance.PixelBounds.X);
        Assert.Equal((int)MathF.Round(20.5f + positioned.Y + positioned.OffsetY - firstPlacement.BearingY), instance.PixelBounds.Y);
    }

    [Fact]
    public async Task PageRolloverAndEvictionRejectStaleHandle()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(8, 8, 1, 1));
        var first = Glyph(0, GlyphAtlasMode.Grayscale);

        var stale = cache.GetOrAdd(in first, out _);
        for (uint id = 1; id < 20; id++)
        {
            var glyph = Glyph(id, GlyphAtlasMode.Grayscale);
            _ = cache.GetOrAdd(in glyph, out _);
        }

        Assert.Equal(1, cache.PageCount);
        Assert.False(cache.TryGet(stale, out _));
        Assert.Equal(20, device.UploadCount);
    }

    [Fact]
    public async Task GrayscaleAndMsdfUseSeparateAtlasPageFormats()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(16, 16, 2, 4));
        var grayscale = Glyph(0, GlyphAtlasMode.Grayscale);
        var msdf = Glyph(0, GlyphAtlasMode.Msdf, padding: 2);

        _ = cache.GetOrAdd(in grayscale, out _);
        _ = cache.GetOrAdd(in msdf, out _);

        Assert.Contains(device.Pages, page => page.Description.Format == TextAtlasFormat.R8Unorm);
        Assert.Contains(device.Pages, page => page.Description.Format == TextAtlasFormat.Rgba8Unorm);
    }

    [Fact]
    public async Task DisposedCacheRejectsNewAndExistingHandles()
    {
        var device = new FakeAtlasDevice();
        var cache = new TextAtlasCache(device);
        var glyph = Glyph(3, GlyphAtlasMode.Grayscale);
        var handle = cache.GetOrAdd(in glyph, out _);

        await cache.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(handle, out _));
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrAdd(in glyph, out _));
        Assert.Throws<ObjectDisposedException>(() => cache.BorrowPages());
        Assert.Single(device.Pages);
        Assert.True(device.Pages[0].IsDisposed);
    }

    [Fact]
    public async Task BorrowedPagesExposeLiveIdsAndFormatsWithoutRepeatedCopies()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(16, 16, 2, 4));
        var grayscale = Glyph(0, GlyphAtlasMode.Grayscale);
        var msdf = Glyph(0, GlyphAtlasMode.Msdf, padding: 2);
        _ = cache.GetOrAdd(in grayscale, out _);
        _ = cache.GetOrAdd(in msdf, out _);

        var first = cache.BorrowPages();
        _ = cache.BorrowPages().Count;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
        {
            var repeated = cache.BorrowPages();
            _ = repeated.Count;
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(2, first.Count);
        Assert.Same(first.Pages[0], device.Pages[0]);
        Assert.Same(first.Pages[1], device.Pages[1]);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RecentlyHitPageWinsOverOlderPageDuringRecycle()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(4, 5, 2, 10));
        var firstGlyph = Glyph(1, GlyphAtlasMode.Grayscale);
        var secondGlyph = Glyph(2, GlyphAtlasMode.Grayscale);
        var thirdGlyph = Glyph(3, GlyphAtlasMode.Grayscale);

        var firstHandle = cache.GetOrAdd(in firstGlyph, out _);
        var secondHandle = cache.GetOrAdd(in secondGlyph, out _);
        Assert.True(cache.TryGet(firstHandle, out _));

        _ = cache.GetOrAdd(in thirdGlyph, out _);

        Assert.True(cache.TryGet(firstHandle, out _));
        Assert.False(cache.TryGet(secondHandle, out _));
    }

    private static PositionedGlyphBitmap Glyph(uint id, GlyphAtlasMode mode, int padding = 1)
    {
        var font = new FontKey("Test", "Regular", "fixture-font");
        var request = new GlyphAtlasRequest(font, new[] { id }, 16, padding, 4, mode);
        var channels = mode switch
        {
            GlyphAtlasMode.Grayscale => 1,
            GlyphAtlasMode.Msdf => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        const int width = 3;
        const int height = 4;
        var pixels = new byte[width * height * channels];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index + 1);
        }

        var bitmap = GlyphBitmap.Create(request, id, width, height, width * channels, 1.25f, 2f, 3f, pixels);
        var positioned = new PositionedGlyph(id, 0, 1.25f, 2.5f, bitmap.AdvanceX, 0, 0.5f, -0.25f);
        return new PositionedGlyphBitmap(positioned, bitmap);
    }

    private sealed class FakeAtlasDevice : ITextAtlasDevice
    {
        public List<FakeAtlasPage> Pages { get; } = new();
        public int UploadCount { get; private set; }
        public TextAtlasUploadStatistics AtlasUploadStatistics => new(0, UploadCount, 0);

        public ITextAtlasPage CreateAtlasPage(in TextAtlasPageDescription description)
        {
            var page = new FakeAtlasPage(description);
            Pages.Add(page);
            return page;
        }

        public bool UploadAtlasPage(ITextAtlasPage page, ReadOnlySpan<byte> pixels, uint sourceRowPitch) => false;

        public bool UploadAtlasDirtyRanges(ITextAtlasPage page, ReadOnlySpan<TextAtlasDirtyRange> ranges)
        {
            if (page is not FakeAtlasPage fake || fake.IsDisposed || ranges.IsEmpty)
            {
                return false;
            }

            UploadCount++;
            return true;
        }
    }

    private sealed class FakeAtlasPage(TextAtlasPageDescription description) : ITextAtlasPage
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
