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
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(32, 32, 1, 1));
        var first = Glyph(1, GlyphAtlasMode.Grayscale);

        var stale = cache.GetOrAdd(in first, out _);
        for (uint id = 2; id < 20; id++)
        {
            var glyph = Glyph(id, GlyphAtlasMode.Grayscale);
            _ = cache.GetOrAdd(in glyph, out _);
        }

        Assert.Equal(1, cache.PageCount);
        Assert.False(cache.TryGet(stale, out _));
        Assert.Equal(19, device.UploadCount);
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
        Assert.Single(device.Pages);
        Assert.True(device.Pages[0].IsDisposed);
    }

    private static PositionedGlyphBitmap Glyph(uint id, GlyphAtlasMode mode)
    {
        var font = new FontKey("Noto Sans", "Regular", "fixture-noto-sans");
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "NotoSans-Regular.ttf");
        using var face = FontFace.LoadFile(font, path);
        var codepoint = (uint)('A' + id % 20);
        var glyphId = face.GetGlyphId(codepoint);
        var request = new GlyphAtlasRequest(font, new[] { glyphId }, 16, 1, 4, mode);
        var result = new GlyphAtlasGenerator().TryGenerateGlyph(face, request, glyphId);
        if (!result.Succeeded || result.Bitmap is null)
        {
            throw new InvalidOperationException("Delta.Text fixture glyph generation failed.");
        }

        var positioned = new PositionedGlyph(glyphId, 0, 1.25f, 2.5f, result.Bitmap.AdvanceX, 0, 0.5f, -0.25f);
        return new PositionedGlyphBitmap(positioned, result.Bitmap);
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
