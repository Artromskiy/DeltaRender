using System;
using System.Collections.Generic;
using System.Numerics;
using Delta;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Text.Contract;

namespace Delta.Render.Text;
using Maths = global::Delta.Maths;

internal sealed class TextAtlas : IDisposable
{
    private const int DefaultMaxPages = 8;

    private readonly IRenderFrameSession _session;
    private readonly ITextService _textService;
    private readonly GlyphImageMode _mode;
    private readonly GlyphImageEncoding _encoding;
    private readonly RenderTextureFormat _format;
    private readonly int _bytesPerPixel;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _padding;
    private readonly float _distanceRange;
    private readonly ColorGlyphOptions? _colorOptions;
    private readonly List<GlyphKey> _evictionKeys = new();
    private readonly Dictionary<GlyphKey, GlyphPlacement> _glyphs = new();
    private AtlasPage?[] _pages = new AtlasPage[4];
    private int _pageCount;
    private ulong _useStamp;
    private ulong _epoch = 1;
    private bool _disposed;

    internal TextAtlas(
        IRenderFrameSession session,
        ITextService textService,
        GlyphImageMode mode,
        GlyphImageEncoding encoding,
        RenderTextureFormat format,
        int bytesPerPixel,
        uint width,
        uint height,
        uint padding,
        float distanceRange,
        ColorGlyphOptions? colorOptions)
    {
        _session = session;
        _textService = textService;
        _mode = mode;
        _encoding = encoding;
        _format = format;
        _bytesPerPixel = bytesPerPixel;
        _width = width;
        _height = height;
        _padding = padding;
        _distanceRange = distanceRange;
        _colorOptions = colorOptions;

        try
        {
            _pages[0] = CreatePage();
            _pageCount = 1;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int PageCount => _pageCount;
    internal uint Width => _width;
    internal uint Height => _height;
    internal int BytesPerPixel => _bytesPerPixel;
    internal ulong Epoch => _epoch;

    internal RenderTextureHandle GetTexture(int pageIndex) => GetPage(pageIndex).Texture;

    internal byte[] GetPixels(int pageIndex) => GetPage(pageIndex).Pixels;

    internal bool IsDirty(int pageIndex) => GetPage(pageIndex).Dirty;

    internal void MarkUploaded(int pageIndex) => GetPage(pageIndex).Dirty = false;

    internal void MarkDirty(int pageIndex) => GetPage(pageIndex).Dirty = true;

    internal void TouchPage(int pageIndex, ulong useStamp)
        => GetPage(pageIndex).LastUse = useStamp;

    internal ulong NextUseStamp()
    {
        ThrowIfDisposed();
        if (_useStamp == ulong.MaxValue)
        {
            _useStamp = 1;
            for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
            {
                GetPage(pageIndex).LastUse = 0;
            }

            return _useStamp;
        }

        return ++_useStamp;
    }

    internal GlyphPlacement GetOrCreateGlyph(in ShapedRun run, in ShapedGlyph glyph, ulong useStamp)
    {
        ThrowIfDisposed();
        var key = new GlyphKey(run.Font, glyph.GlyphId, run.PixelsPerEm, _mode, _encoding, _distanceRange, _padding, _colorOptions);
        if (_glyphs.TryGetValue(key, out var placement))
        {
            if (!placement.IsEmpty)
            {
                GetPage(placement.PageIndex).LastUse = useStamp;
            }

            return placement;
        }

        var image = _textService.GenerateGlyphImage(new GlyphImageRequest(
            run.Font,
            glyph.GlyphId,
            run.PixelsPerEm,
            _mode,
            _distanceRange,
            _colorOptions));
        ValidateImage(image, key);
        if (image.IsEmpty)
        {
            placement = GlyphPlacement.Empty;
            _glyphs.Add(key, placement);
            return placement;
        }

        var width = checked((uint)image.Width);
        var height = checked((uint)image.Height);
        if (width > _width || height > _height)
        {
            throw new InvalidOperationException("A glyph image does not fit in the configured text atlas.");
        }

        var pageIndex = FindPage(width, height);
        if (pageIndex < 0)
        {
            pageIndex = CreatePageSlot(useStamp);
        }

        var page = GetPage(pageIndex);
        PreparePlacement(page, width, height);
        var destinationX = page.CursorX;
        var destinationY = page.CursorY;

        CopyImage(image, page.Pixels, destinationX, destinationY);
        placement = new GlyphPlacement(
            pageIndex,
            image.PlaneBounds,
            new Vector4(
                (float)destinationX / _width,
                (float)destinationY / _height,
                (float)(destinationX + width) / _width,
                (float)(destinationY + height) / _height));
        _glyphs.Add(key, placement);
        page.CursorX = checked(destinationX + width + _padding);
        page.RowHeight = Maths.Max(page.RowHeight, height);
        page.LastUse = useStamp;
        page.Dirty = true;
        return placement;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var pageIndex = _pageCount - 1; pageIndex >= 0; pageIndex--)
        {
            var page = _pages[pageIndex];
            if (page is not null && page.Texture.IsValid)
            {
                _session.Release(page.Texture);
                page.Texture = default;
            }
        }
    }

    private int FindPage(uint width, uint height)
    {
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            if (CanPlace(GetPage(pageIndex), width, height))
            {
                return pageIndex;
            }
        }

        return -1;
    }

    private bool CanPlace(AtlasPage page, uint width, uint height)
    {
        var destinationY = page.CursorY;
        if (page.CursorX > _width - width)
        {
            destinationY = checked(destinationY + page.RowHeight + _padding);
        }

        return destinationY <= _height - height;
    }

    private void PreparePlacement(AtlasPage page, uint width, uint height)
    {
        if (page.CursorX > _width - width)
        {
            page.CursorX = 0;
            page.CursorY = checked(page.CursorY + page.RowHeight + _padding);
            page.RowHeight = 0;
        }

        if (page.CursorY > _height - height)
        {
            throw new InvalidOperationException("The selected text atlas page cannot fit the glyph image.");
        }
    }

    private int CreatePageSlot(ulong useStamp)
    {
        if (_pageCount < DefaultMaxPages)
        {
            EnsurePageCapacity(_pageCount + 1);
            _pages[_pageCount] = CreatePage();
            return _pageCount++;
        }

        return RecyclePage(useStamp);
    }

    private int RecyclePage(ulong useStamp)
    {
        var pageIndexToRecycle = -1;
        var oldestUseStamp = ulong.MaxValue;
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = GetPage(pageIndex);
            if (page.LastUse == useStamp)
            {
                continue;
            }

            if (pageIndexToRecycle < 0 || page.LastUse < oldestUseStamp)
            {
                pageIndexToRecycle = pageIndex;
                oldestUseStamp = page.LastUse;
            }
        }

        if (pageIndexToRecycle < 0)
        {
            throw new InvalidOperationException("The text atlas is full and every page is in use by the current frame.");
        }

        _evictionKeys.Clear();
        foreach (var entry in _glyphs)
        {
            if (entry.Value.PageIndex == pageIndexToRecycle)
            {
                _evictionKeys.Add(entry.Key);
            }
        }

        foreach (var key in _evictionKeys)
        {
            _glyphs.Remove(key);
        }

        var pageToRecycle = GetPage(pageIndexToRecycle);
        Array.Clear(pageToRecycle.Pixels);
        pageToRecycle.CursorX = 0;
        pageToRecycle.CursorY = 0;
        pageToRecycle.RowHeight = 0;
        pageToRecycle.LastUse = useStamp;
        pageToRecycle.Dirty = true;
        _epoch = _epoch == ulong.MaxValue ? 1 : _epoch + 1;
        return pageIndexToRecycle;
    }

    private AtlasPage CreatePage()
    {
        var pixels = new byte[checked((int)((ulong)_width * _height * (uint)_bytesPerPixel))];
        var page = new AtlasPage(pixels);
        try
        {
            page.Texture = _session.CreateTexture(new RenderTextureDescription(
                _width,
                _height,
                _format,
                RenderTextureUsage.Sampled | RenderTextureUsage.TransferDestination));
            return page;
        }
        catch
        {
            if (page.Texture.IsValid)
            {
                _session.Release(page.Texture);
            }

            throw;
        }
    }

    private void EnsurePageCapacity(int required)
    {
        if (required <= _pages.Length)
        {
            return;
        }

        Array.Resize(ref _pages, Maths.Max(required, checked(_pages.Length * 2)));
    }

    private void CopyImage(GlyphImage image, byte[] destinationPixels, uint destinationX, uint destinationY)
    {
        var sourceBytesPerPixel = image.Encoding == GlyphImageEncoding.MsdfRgb8 ? 3 : _bytesPerPixel;
        var sourceRowBytes = checked(image.Width * sourceBytesPerPixel);
        var destinationRowBytes = checked((int)_width * _bytesPerPixel);
        var source = image.Pixels.Span;
        for (var row = 0; row < image.Height; row++)
        {
            var sourceRow = source.Slice(row * sourceRowBytes, sourceRowBytes);
            var destinationOffset = checked(((int)destinationY + row) * destinationRowBytes + (int)destinationX * _bytesPerPixel);
            var destinationRow = destinationPixels.AsSpan(destinationOffset, checked(image.Width * _bytesPerPixel));
            if (sourceBytesPerPixel == _bytesPerPixel)
            {
                sourceRow.CopyTo(destinationRow);
                continue;
            }

            for (var pixel = 0; pixel < image.Width; pixel++)
            {
                var sourcePixel = sourceRow.Slice(pixel * 3, 3);
                var destinationPixel = destinationRow.Slice(pixel * 4, 4);
                sourcePixel.CopyTo(destinationPixel);
                destinationPixel[3] = byte.MaxValue;
            }
        }
    }

    private void ValidateImage(GlyphImage image, GlyphKey key)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Font != key.Font || image.GlyphId != key.GlyphId || image.PixelsPerEm != key.PixelsPerEm ||
            image.Encoding != key.Encoding || image.DistanceRange != key.DistanceRange)
        {
            throw new InvalidOperationException("The text service returned a glyph image that does not match its request.");
        }

        if (image.Width < 0 || image.Height < 0 || image.Width > _width || image.Height > _height)
        {
            throw new InvalidOperationException("The text service returned invalid glyph image dimensions.");
        }

        var expectedLength = checked((long)image.Width * image.Height * _bytesPerPixel);
        if (image.Pixels.Length != expectedLength)
        {
            throw new InvalidOperationException("The text service returned a glyph image with an invalid pixel payload length.");
        }

        if (!IsFinite(image.PlaneBounds))
        {
            throw new InvalidOperationException("The text service returned non-finite glyph plane bounds.");
        }
    }

    private AtlasPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)_pageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return _pages[pageIndex] ?? throw new InvalidOperationException("The text atlas page is unavailable.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsFinite(TextBounds value)
        => float.IsFinite(value.Left) && float.IsFinite(value.Top) && float.IsFinite(value.Right) && float.IsFinite(value.Bottom);

    private sealed class AtlasPage(byte[] pixels)
    {
        public RenderTextureHandle Texture;
        public byte[] Pixels { get; } = pixels;
        public uint CursorX;
        public uint CursorY;
        public uint RowHeight;
        public ulong LastUse;
        public bool Dirty;
    }
}

internal readonly record struct GlyphKey(
    FontInstanceId Font,
    uint GlyphId,
    float PixelsPerEm,
    GlyphImageMode Mode,
    GlyphImageEncoding Encoding,
    float DistanceRange,
    uint Padding,
    ColorGlyphOptions? Color);

internal readonly record struct GlyphPlacement(int PageIndex, TextBounds PlaneBounds, Vector4 UvRect)
{
    internal static GlyphPlacement Empty => new(-1, default, default);

    internal bool IsEmpty => PageIndex < 0;
}
