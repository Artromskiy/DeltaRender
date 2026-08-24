using System.Diagnostics.CodeAnalysis;
using Delta.Text;

namespace Delta.Render.Core;

public readonly record struct TextGlyphCacheKey(
    string FontIdentity,
    uint GlyphId,
    uint PixelSize,
    TextRenderMode Mode,
    float PxRange,
    float Smoothing)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(FontIdentity) && GlyphId != 0 && PixelSize > 0 &&
                           Mode is TextRenderMode.Sdf or TextRenderMode.Msdf &&
                           float.IsFinite(PxRange) && PxRange > 0 && float.IsFinite(Smoothing) && Smoothing >= 0;
}

public readonly record struct TextGlyphCacheHandle(TextAtlasPageId Page, uint Generation, uint Slot)
{
    public bool IsValid => Page.IsValid && Generation != 0 && Slot != 0;
}

public readonly record struct TextGlyphPlacement(
    TextGlyphCacheHandle Handle,
    TextUvRect Uv,
    TextPixelBounds Bounds,
    float AdvanceX,
    TextRenderMode Mode,
    float PxRange,
    float Smoothing)
{
    public TextGlyphInstance ToInstance(int x, int y, TextColor color, UiClipRect clip, uint pipelineId = 0) =>
        new(Handle.Page, Uv, Bounds with { X = checked(Bounds.X + x), Y = checked(Bounds.Y + y) },
            color, clip, Mode, PxRange, Smoothing, pipelineId);
}

public readonly record struct TextAtlasCacheOptions(uint PageWidth = 256, uint PageHeight = 256, int MaxPages = 8, int MaxEntries = 4096)
{
    public bool IsValid => PageWidth > 0 && PageHeight > 0 && MaxPages > 0 && MaxEntries > 0;
}

/// <summary>
/// Renderer-owned bounded CPU glyph cache. Shaping and rasterization remain outside Render.
/// </summary>
public sealed class TextAtlasCache : IAsyncDisposable
{
    private sealed class Entry
    {
        public required TextGlyphCacheKey Key { get; init; }
        public required TextGlyphPlacement Placement { get; init; }
        public required uint Generation { get; init; }
        public long LastUse { get; set; }
    }

    private readonly ITextAtlasDevice _device;
    private readonly TextAtlasCacheOptions _options;
    private readonly Dictionary<TextGlyphCacheKey, Entry> _entries = new();
    private readonly List<ITextAtlasPage> _pages = new();
    private uint _nextPageId = 1;
    private uint _nextSlot = 1;
    private uint _generation = 1;
    private long _clock;
    private uint _cursorX;
    private uint _cursorY;
    private uint _rowHeight;
    private bool _disposed;

    public TextAtlasCache(ITextAtlasDevice device, TextAtlasCacheOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (options.PageWidth == 0 && options.PageHeight == 0 && options.MaxPages == 0 && options.MaxEntries == 0)
        {
            options = new TextAtlasCacheOptions(256, 256, 8, 4096);
        }

        if (!options.IsValid)
        {
            throw new ArgumentException("Atlas cache options are invalid.", nameof(options));
        }

        _device = device;
        _options = options;
    }

    public int EntryCount => _entries.Count;
    public int PageCount => _pages.Count;

    /// <summary>
    /// Consumes Delta.Text's positioned glyph handoff and copies its pixels into
    /// renderer-owned atlas storage. Delta.Text remains the owner of shaping and rasterization.
    /// </summary>
    public TextGlyphCacheHandle GetOrAdd(string fontIdentity, in PositionedGlyphBitmap glyph, out TextGlyphPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontIdentity);
        var bitmap = glyph.Bitmap;
        var mode = bitmap.Request.Mode switch
        {
            GlyphAtlasMode.Grayscale => TextRenderMode.Sdf,
            GlyphAtlasMode.Msdf => TextRenderMode.Msdf,
            _ => throw new ArgumentException("MTSDF glyphs are not supported by this renderer slice.", nameof(glyph))
        };
        var key = new TextGlyphCacheKey(fontIdentity, bitmap.GlyphId, checked((uint)bitmap.Request.PixelSize), mode,
            bitmap.Request.DistanceRange, 0);
        if (!key.IsValid || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Stride <= 0 ||
            bitmap.Pixels.Length < checked(bitmap.Stride * bitmap.Height) || !float.IsFinite(bitmap.AdvanceX))
        {
            throw new ArgumentException("Delta.Text glyph bitmap is invalid.", nameof(glyph));
        }

        var pixels = NormalizePixels(bitmap, mode, out var rowPitch);
        return GetOrAdd(key, (uint)bitmap.Width, (uint)bitmap.Height, rowPitch, pixels,
            checked((int)MathF.Round(bitmap.BearingX)), checked((int)MathF.Round(bitmap.BearingY)), bitmap.AdvanceX, out placement);
    }

    private TextGlyphCacheHandle GetOrAdd(TextGlyphCacheKey key, uint width, uint height, uint rowPitch,
        ReadOnlyMemory<byte> pixels, int bearingX, int bearingY, float advanceX, out TextGlyphPlacement placement)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUse = ++_clock;
            placement = existing.Placement;
            return new TextGlyphCacheHandle(placement.Handle.Page, existing.Generation, placement.Handle.Slot);
        }

        EvictIfNeeded();
        var page = EnsurePlacement(width, height, key.Mode, out var x, out var y);
        var range = new TextAtlasDirtyRange(x, y, width, height, rowPitch, pixels);
        if (!_device.UploadAtlasDirtyRanges(page, new[] { range }))
        {
            throw new InvalidOperationException("The atlas device rejected the glyph upload.");
        }

        placement = new TextGlyphPlacement(
            new TextGlyphCacheHandle(page.Description.Id, _generation, _nextSlot++),
            new TextUvRect((float)x / page.Description.Width, (float)y / page.Description.Height,
                (float)width / page.Description.Width, (float)height / page.Description.Height),
            new TextPixelBounds(bearingX, -bearingY, checked((int)width), checked((int)height)),
            advanceX, key.Mode, key.PxRange, key.Smoothing);
        _entries.Add(key, new Entry { Key = key, Placement = placement, Generation = _generation, LastUse = ++_clock });
        return placement.Handle;
    }

    public bool TryGet(TextGlyphCacheHandle handle, [NotNullWhen(true)] out TextGlyphPlacement? placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var entry in _entries.Values)
        {
            if (entry.Placement.Handle == handle && entry.Generation == handle.Generation)
            {
                entry.LastUse = ++_clock;
                placement = entry.Placement;
                return true;
            }
        }

        placement = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
        foreach (var page in _pages)
        {
            await page.DisposeAsync().ConfigureAwait(false);
        }
        _pages.Clear();
    }

    private ITextAtlasPage EnsurePlacement(uint width, uint height, TextRenderMode mode, out uint x, out uint y)
    {
        var requiredWidth = checked(width + 1);
        var requiredHeight = checked(height + 1);
        if (requiredWidth > _options.PageWidth || requiredHeight > _options.PageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Glyph bitmap does not fit in an atlas page.");
        }

        if (_pages.Count == 0 || _cursorX + requiredWidth > _options.PageWidth)
        {
            _cursorX = 0;
            _cursorY = checked(_cursorY + _rowHeight);
            _rowHeight = 0;
        }

        if (_pages.Count == 0 || _cursorY + requiredHeight > _options.PageHeight)
        {
            if (_pages.Count >= _options.MaxPages)
            {
                throw new InvalidOperationException("The bounded atlas cache has no available page space.");
            }

            var description = new TextAtlasPageDescription(new TextAtlasPageId(_nextPageId++), _options.PageWidth, _options.PageHeight,
                mode == TextRenderMode.Msdf ? TextAtlasFormat.Rgba8Unorm : TextAtlasFormat.R8Unorm);
            _pages.Add(_device.CreateAtlasPage(description));
            _cursorX = 0;
            _cursorY = 0;
            _rowHeight = 0;
        }

        x = _cursorX;
        y = _cursorY;
        _cursorX = checked(_cursorX + requiredWidth);
        _rowHeight = Math.Max(_rowHeight, requiredHeight);
        return _pages[^1];
    }

    private static byte[] NormalizePixels(GlyphBitmap bitmap, TextRenderMode mode, out uint rowPitch)
    {
        var bytesPerPixel = mode == TextRenderMode.Msdf ? 4 : 1;
        rowPitch = checked((uint)(bitmap.Width * bytesPerPixel));
        var normalized = new byte[checked((int)(rowPitch * (uint)bitmap.Height))];
        if (mode == TextRenderMode.Sdf)
        {
            for (var row = 0; row < bitmap.Height; row++)
            {
                bitmap.Pixels.Span.Slice(row * bitmap.Stride, bitmap.Width).CopyTo(normalized.AsSpan(row * (int)rowPitch, bitmap.Width));
            }
            return normalized;
        }

        for (var row = 0; row < bitmap.Height; row++)
        {
            var source = bitmap.Pixels.Span.Slice(row * bitmap.Stride, checked(bitmap.Width * 3));
            var target = normalized.AsSpan(row * (int)rowPitch, checked(bitmap.Width * 4));
            for (var pixel = 0; pixel < bitmap.Width; pixel++)
            {
                source.Slice(pixel * 3, 3).CopyTo(target.Slice(pixel * 4, 3));
                target[pixel * 4 + 3] = byte.MaxValue;
            }
        }
        return normalized;
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count < _options.MaxEntries)
        {
            return;
        }

        var oldest = _entries.Values.MinBy(entry => entry.LastUse);
        if (oldest is not null)
        {
            _entries.Remove(oldest.Key);
            _generation = _generation == uint.MaxValue ? 1 : _generation + 1;
        }
    }
}
