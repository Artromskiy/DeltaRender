using System.Reflection;
using Delta.Render.Core;
using Delta.Text;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextAtlasCacheTests
{
    [Fact]
    public async Task InsertUploadsOnceAndCacheHitReusesPlacement()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(16, 16, 2, 4));
        var glyph = Glyph(7, GlyphAtlasMode.Grayscale, 2, 3);

        var first = cache.GetOrAdd("font-a", in glyph, out var firstPlacement);
        var second = cache.GetOrAdd("font-a", in glyph, out var secondPlacement);

        Assert.Equal(first, second);
        Assert.Equal(firstPlacement, secondPlacement);
        Assert.Equal(1, device.UploadCount);
        Assert.Equal(new TextUvRect(0, 0, 0.125f, 0.1875f), firstPlacement.Uv);
        Assert.Equal(new TextPixelBounds(1, -2, 2, 3), firstPlacement.Bounds);
    }

    [Fact]
    public async Task PageRolloverAndEvictionRejectStaleHandle()
    {
        var device = new FakeAtlasDevice();
        await using var cache = new TextAtlasCache(device, new TextAtlasCacheOptions(4, 4, 2, 1));
        var first = Glyph(1, GlyphAtlasMode.Grayscale, 2, 2);
        var second = Glyph(2, GlyphAtlasMode.Grayscale, 2, 2);

        var stale = cache.GetOrAdd("font-a", in first, out _);
        _ = cache.GetOrAdd("font-a", in second, out _);

        Assert.Equal(2, cache.PageCount);
        Assert.False(cache.TryGet(stale, out _));
        Assert.Equal(2, device.UploadCount);
    }

    [Fact]
    public async Task DisposedCacheRejectsNewAndExistingHandles()
    {
        var device = new FakeAtlasDevice();
        var cache = new TextAtlasCache(device);
        var glyph = Glyph(3, GlyphAtlasMode.Grayscale, 1, 1);
        var handle = cache.GetOrAdd("font-a", in glyph, out _);

        await cache.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(handle, out _));
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrAdd("font-a", in glyph, out _));
        Assert.Single(device.Pages);
        Assert.True(device.Pages[0].IsDisposed);
    }

    private static PositionedGlyphBitmap Glyph(uint id, GlyphAtlasMode mode, int width, int height)
    {
        var font = new FontKey("Test", "Regular", "fixture-font");
        var request = new GlyphAtlasRequest(font, new[] { id }, 16, 0, 4, mode);
        var stride = checked(width * (mode == GlyphAtlasMode.Msdf ? 3 : 1));
        var pixels = new byte[checked(stride * height)];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i + 1);
        }

        var constructor = typeof(GlyphBitmap).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(GlyphAtlasRequest), typeof(uint), typeof(int), typeof(int), typeof(int), typeof(float), typeof(float), typeof(float), typeof(ReadOnlyMemory<byte>) }, null);
        if (constructor is null)
        {
            throw new InvalidOperationException("Delta.Text GlyphBitmap constructor was not found.");
        }

        var bitmap = (GlyphBitmap)constructor.Invoke(new object[] { request, id, width, height, stride, 1f, 2f, 3f, (ReadOnlyMemory<byte>)pixels });
        return new PositionedGlyphBitmap(default, bitmap);
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
