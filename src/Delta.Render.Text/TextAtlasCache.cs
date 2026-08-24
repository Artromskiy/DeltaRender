using System.Diagnostics.CodeAnalysis;
using Delta.Render.Core;
using Delta.Text;

namespace Delta.Render.Text;

public readonly record struct TextGlyphCacheKey(
    FontKey Font,
    uint GlyphId,
    uint PixelSize,
    int Padding,
    TextRenderMode Mode,
    float PxRange,
    float Smoothing)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Font.Family) && !string.IsNullOrWhiteSpace(Font.Style) &&
                           !string.IsNullOrWhiteSpace(Font.SourceId) && PixelSize > 0 && Padding >= 0 &&
                           Mode is TextRenderMode.Sdf or TextRenderMode.Msdf &&
                           float.IsFinite(PxRange) && PxRange > 0 && float.IsFinite(Smoothing) && Smoothing >= 0;
}

public readonly record struct TextGlyphCacheHandle(TextAtlasPageId Page, uint Generation, uint Slot)
{
    public bool IsValid => Page.IsValid && Generation != 0 && Slot != 0;
}

/// <summary>
/// Borrowed read-only view of the cache's live atlas pages. The view is valid
/// until the cache mutates or is disposed and must not be retained between frames.
/// </summary>
public readonly ref struct TextAtlasPageView
{
    private readonly ReadOnlySpan<ITextAtlasPage> _pages;

    internal TextAtlasPageView(ITextAtlasPage[] pages, int count) => _pages = pages.AsSpan(0, count);

    public ReadOnlySpan<ITextAtlasPage> Pages => _pages;
    public int Count => _pages.Length;
    public bool IsEmpty => _pages.IsEmpty;
}

public readonly record struct TextGlyphPlacement(
    TextGlyphCacheHandle Handle,
    GlyphAtlasRegion AtlasRegion,
    float BearingX,
    float BearingY,
    int Width,
    int Height,
    float AdvanceX,
    TextRenderMode Mode,
    float PxRange,
    float Smoothing)
{
    public TextGlyphInstance ToInstance(in PositionedGlyph glyph, float originX, float originY,
        TextColor color, UiClipRect clip, uint pipelineId = 0)
    {
        if (Handle.Page != AtlasRegion.AtlasPage)
        {
            throw new InvalidOperationException("Glyph placement atlas page does not match its cache handle.");
        }

        var x = checked((int)MathF.Round(originX + glyph.X + glyph.OffsetX + BearingX));
        var y = checked((int)MathF.Round(originY + glyph.Y + glyph.OffsetY - BearingY));
        return new TextGlyphInstance(
            AtlasRegion.AtlasPage,
            AtlasRegion.Uv,
            new TextPixelBounds(x, y, Width, Height),
            color,
            clip,
            Mode,
            PxRange,
            Smoothing,
            pipelineId);
    }
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
    private sealed class PageState
    {
        public required ITextAtlasPage Page { get; init; }
        public required TextAtlasFormat Format { get; init; }
        public uint CursorX { get; set; }
        public uint CursorY { get; set; }
        public uint RowHeight { get; set; }
        public long LastUse { get; set; }
        public uint Generation { get; set; } = 1;
    }

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
    private readonly List<PageState> _pages = new();
    private ITextAtlasPage[] _pageView = Array.Empty<ITextAtlasPage>();
    private uint _nextPageId = 1;
    private uint _nextSlot = 1;
    private long _clock;
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

    /// <summary>Gets a borrowed page view without copying. The view expires on cache mutation/disposal.</summary>
    public TextAtlasPageView BorrowPages()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TextAtlasPageView(_pageView, _pages.Count);
    }

    /// <summary>Consumes Delta.Text's positioned glyph handoff and copies pixels into owned storage.</summary>
    public TextGlyphCacheHandle GetOrAdd(in PositionedGlyphBitmap glyph, out TextGlyphPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bitmap = glyph.Bitmap;
        if (bitmap is null)
        {
            throw new ArgumentException("The Delta.Text glyph bitmap is missing.", nameof(glyph));
        }

        var mode = bitmap.Request.Mode switch
        {
            GlyphAtlasMode.Grayscale => TextRenderMode.Sdf,
            GlyphAtlasMode.Msdf => TextRenderMode.Msdf,
            _ => throw new ArgumentException("MTSDF glyphs are not supported by this renderer slice.", nameof(glyph))
        };
        var key = new TextGlyphCacheKey(bitmap.Request.Font, bitmap.GlyphId, checked((uint)bitmap.Request.PixelSize),
            bitmap.Request.Padding, mode, bitmap.Request.DistanceRange, 0);
        if (!key.IsValid || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Stride <= 0 ||
            bitmap.Pixels.Length < checked(bitmap.Stride * bitmap.Height) || !float.IsFinite(bitmap.AdvanceX))
        {
            throw new ArgumentException("Delta.Text glyph bitmap is invalid.", nameof(glyph));
        }

        var pixels = NormalizePixels(bitmap, mode, out var rowPitch);
        return GetOrAdd(key, (uint)bitmap.Width, (uint)bitmap.Height, rowPitch, pixels,
            bitmap.BearingX, bitmap.BearingY, bitmap.AdvanceX, out placement);
    }

    private TextGlyphCacheHandle GetOrAdd(TextGlyphCacheKey key, uint width, uint height, uint rowPitch,
        ReadOnlyMemory<byte> pixels, float bearingX, float bearingY, float advanceX, out TextGlyphPlacement placement)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUse = ++_clock;
            TouchPage(existing.Placement.Handle.Page, _clock);
            placement = existing.Placement;
            return new TextGlyphCacheHandle(placement.Handle.Page, existing.Generation, placement.Handle.Slot);
        }

        EvictIfNeeded();
        var format = key.Mode == TextRenderMode.Msdf ? TextAtlasFormat.Rgba8Unorm : TextAtlasFormat.R8Unorm;
        var page = EnsurePlacement(width, height, format, out var x, out var y);
        var range = new TextAtlasDirtyRange(x, y, width, height, rowPitch, pixels);
        if (!_device.UploadAtlasDirtyRanges(page.Page, new[] { range }))
        {
            throw new InvalidOperationException("The atlas device rejected the glyph upload.");
        }

        placement = new TextGlyphPlacement(
            new TextGlyphCacheHandle(page.Page.Description.Id, page.Generation, _nextSlot++),
            new GlyphAtlasRegion(page.Page.Description.Id, new TextUvRect((float)x / page.Page.Description.Width, (float)y / page.Page.Description.Height,
                (float)width / page.Page.Description.Width, (float)height / page.Page.Description.Height)),
            bearingX, bearingY, checked((int)width), checked((int)height),
            advanceX, key.Mode, key.PxRange, key.Smoothing);
        page.LastUse = ++_clock;
        _entries.Add(key, new Entry { Key = key, Placement = placement, Generation = page.Generation, LastUse = _clock });
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
                TouchPage(entry.Placement.Handle.Page, _clock);
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
            await page.Page.DisposeAsync().ConfigureAwait(false);
        }
        _pages.Clear();
        _pageView = Array.Empty<ITextAtlasPage>();
    }

    private PageState EnsurePlacement(uint width, uint height, TextAtlasFormat format, out uint x, out uint y)
    {
        var requiredWidth = checked(width + 1);
        var requiredHeight = checked(height + 1);
        if (requiredWidth > _options.PageWidth || requiredHeight > _options.PageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Glyph bitmap does not fit in an atlas page.");
        }

        foreach (var page in _pages)
        {
            if (page.Format != format)
            {
                continue;
            }

            if (TryPlace(page, requiredWidth, requiredHeight, out x, out y))
            {
                return page;
            }
        }

        if (_pages.Count < _options.MaxPages)
        {
            var description = new TextAtlasPageDescription(new TextAtlasPageId(_nextPageId++), _options.PageWidth, _options.PageHeight, format);
            var page = new PageState { Page = _device.CreateAtlasPage(description), Format = format };
            _pages.Add(page);
            var pages = new ITextAtlasPage[_pages.Count];
            for (var index = 0; index < _pages.Count; index++)
            {
                pages[index] = _pages[index].Page;
            }
            _pageView = pages;
            TryPlace(page, requiredWidth, requiredHeight, out x, out y);
            return page;
        }

        var recyclable = _pages.Where(page => page.Format == format).MinBy(page => page.LastUse);
        if (recyclable is null)
        {
            throw new InvalidOperationException("The bounded atlas cache has no reusable page for this pixel format.");
        }

        foreach (var key in _entries.Where(pair => pair.Value.Placement.Handle.Page == recyclable.Page.Description.Id).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(key);
        }

        recyclable.Generation = recyclable.Generation == uint.MaxValue ? 1 : recyclable.Generation + 1;
        recyclable.CursorX = 0;
        recyclable.CursorY = 0;
        recyclable.RowHeight = 0;
        TryPlace(recyclable, requiredWidth, requiredHeight, out x, out y);
        return recyclable;
    }

    private void TouchPage(TextAtlasPageId pageId, long timestamp)
    {
        for (var index = 0; index < _pages.Count; index++)
        {
            if (_pages[index].Page.Description.Id == pageId)
            {
                _pages[index].LastUse = timestamp;
                return;
            }
        }
    }

    private static bool TryPlace(PageState page, uint requiredWidth, uint requiredHeight, out uint x, out uint y)
    {
        if (page.CursorX + requiredWidth > page.Page.Description.Width)
        {
            page.CursorX = 0;
            page.CursorY = checked(page.CursorY + page.RowHeight);
            page.RowHeight = 0;
        }

        if (page.CursorY + requiredHeight > page.Page.Description.Height)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = page.CursorX;
        y = page.CursorY;
        page.CursorX = checked(page.CursorX + requiredWidth);
        page.RowHeight = Math.Max(page.RowHeight, requiredHeight);
        return true;
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
        }
    }
}
