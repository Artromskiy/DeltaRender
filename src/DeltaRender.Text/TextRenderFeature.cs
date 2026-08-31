using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Shader.Text;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>
/// Reusable graph feature for positioned text. The caller supplies already shaped
/// text and owns the feature between queueing runs and graph execution.
/// </summary>
/// <remarks>
/// This adapter consumes DeltaText values synchronously during graph build. It
/// owns bounded format-specific atlas pages, sampler, instance buffer and cache; the
/// session owns the native resources. It does not shape text or retain strings.
/// </remarks>
public sealed class TextRenderFeature : IRenderFeature, IDisposable
{
    private const int InitialInstanceCapacity = 256;
    private const int DefaultMaxAtlasPages = 8;
    private const uint DefaultAtlasSize = 2048;
    private const uint DefaultPadding = 1;

    private readonly IRenderFrameSession _session;
    private readonly ITextService _textService;
    private readonly TextUploadPass _uploadPass;
    private readonly TextDrawPass _drawPass;
    private readonly RasterPipelineDescription _pipeline;
    private readonly RasterPassDescription _rasterPassDescription;
    private readonly GlyphImageMode _mode;
    private readonly GlyphImageEncoding _encoding;
    private readonly RenderTextureFormat _atlasFormat;
    private readonly int _atlasBytesPerPixel;
    private readonly uint _atlasWidth;
    private readonly uint _atlasHeight;
    private readonly uint _padding;
    private readonly float _distanceRange;
    private readonly ColorGlyphOptions? _colorOptions;
    private readonly List<GlyphKey> _evictionKeys = new();
    private readonly Dictionary<TextRunCacheKey, CachedRun> _runCache = new();
    private readonly ShaderBinding _instanceBinding;
    private readonly uint _instanceStride;
    private readonly ShaderBinding _atlasBinding;
    private readonly uint _pushConstantSize;
    private readonly byte[] _pushConstantBytes;
    private PendingRun[] _pendingRuns = new PendingRun[8];
    private readonly Dictionary<GlyphKey, GlyphPlacement> _glyphs = new();
    private AtlasPage?[] _pages;
    private RenderGraphTextureHandle[] _pageGraphHandles;
    private int[] _uploadPageIndices;

    private GlyphInstance[] _instances;
    private byte[] _instanceBytes;
    private byte[] _uploadedInstanceBytes;
    private TextBatch[] _batches;
    private TextBatch[] _localRunBatches = new TextBatch[8];
    private int[] _runBatchStarts = new int[8];
    private int[] _runBatchCounts = new int[8];
    private RenderBufferHandle _instanceBuffer;
    private RenderSamplerHandle _sampler;
    private int _pageCount;
    private int _uploadPageCount;
    private int _pendingRunCount;
    private int _instanceCount;
    private int _batchCount;
    private int _localRunBatchCount;
    private int _uploadedInstanceByteCount;
    private ulong _atlasUseStamp;
    private ulong _atlasEpoch = 1;
    private PixelExtent _viewport;
    private bool _instancePayloadDirty;
    private bool _instanceBufferNeedsUpload = true;
    private bool _disposed;

    /// <summary>
    /// Creates a reusable text feature with renderer-owned atlas and instance resources.
    /// </summary>
    public TextRenderFeature(
        IRenderFrameSession session,
        ITextService textService,
        IGraphicsShaderProgram shaderProgram,
        PixelExtent viewport,
        GlyphImageMode mode = GlyphImageMode.Sdf,
        uint atlasWidth = DefaultAtlasSize,
        uint atlasHeight = DefaultAtlasSize,
        uint padding = DefaultPadding,
        float distanceRange = 4f,
        ColorGlyphOptions? colorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(textService);
        ArgumentNullException.ThrowIfNull(shaderProgram);
        if (!session.Target.IsValid)
        {
            throw new ArgumentException("Text rendering requires a graphics session with a valid target.", nameof(session));
        }

        if (viewport.IsEmpty)
        {
            throw new ArgumentException("The text viewport must be non-empty.", nameof(viewport));
        }

        if (atlasWidth == 0 || atlasHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasWidth), "The text atlas dimensions must be non-zero.");
        }

        if (!float.IsFinite(distanceRange) || distanceRange < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceRange), "The text distance range must be finite and non-negative.");
        }

        (_encoding, _atlasFormat, _atlasBytesPerPixel) = DescribeImageFormat(mode);
        _instanceBinding = FindBinding(shaderProgram, ShaderResourceKind.StorageBuffer, ShaderStageMask.Vertex, "vertex instance buffer");
        _instanceStride = FindInstanceStride(shaderProgram, _instanceBinding);
        _atlasBinding = FindTextureBinding(shaderProgram, "fragment atlas texture");
        _pushConstantSize = FindPushConstantSize(shaderProgram);
        _pushConstantBytes = new byte[checked((int)_pushConstantSize)];

        _session = session;
        _textService = textService;
        _mode = mode;
        _atlasWidth = atlasWidth;
        _atlasHeight = atlasHeight;
        _padding = padding;
        _distanceRange = mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf ? distanceRange : 0;
        _colorOptions = colorOptions;
        _viewport = viewport;
        _pages = new AtlasPage[4];
        _pageGraphHandles = new RenderGraphTextureHandle[4];
        _uploadPageIndices = new int[4];
        _instances = new GlyphInstance[InitialInstanceCapacity];
        _instanceBytes = new byte[checked((int)((ulong)InitialInstanceCapacity * _instanceStride))];
        _uploadedInstanceBytes = new byte[_instanceBytes.Length];
        _batches = new TextBatch[16];
        _uploadPass = new TextUploadPass(this);
        _drawPass = new TextDrawPass(this);
        _pipeline = new RasterPipelineDescription(
            shaderProgram,
            topology: PrimitiveTopology.TriangleList,
            cullMode: RasterCullMode.None,
            blendMode: RenderBlendMode.Alpha);
        _rasterPassDescription = new RasterPassDescription("DeltaRender.Text.Draw", _pipeline);

        RenderBufferHandle instanceBuffer = default;
        RenderSamplerHandle sampler = default;
        try
        {
            _pages.RefAt(0) = CreateAtlasPage();
            _pageCount = 1;
            sampler = session.CreateSampler(new RenderSamplerDescription());
            instanceBuffer = session.CreateBuffer(new RenderBufferDescription(
                checked((ulong)InitialInstanceCapacity * _instanceStride),
                RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
            _sampler = sampler;
            _instanceBuffer = instanceBuffer;
        }
        catch
        {
            if (instanceBuffer.IsValid)
            {
                session.Release(instanceBuffer);
            }

            if (sampler.IsValid)
            {
                session.Release(sampler);
            }

            for (var index = _pageCount - 1; index >= 0; index--)
            {
                var page = _pages.RefAt(index);
                if (page is not null && page.Texture.IsValid)
                {
                    session.Release(page.Texture);
                }
            }
            throw;
        }
    }

    /// <summary>Queues one already shaped text value for the next graph build.</summary>
    /// <remarks>
    /// The shaped value and its glyph memory must remain valid until graph
    /// execution completes. Call <see cref="Clear"/> after execution to reuse
    /// the feature for another frame.
    /// </remarks>
    public void AddRun(ShapedText text, float originX, float originY, Vector4 color, PixelRect clip)
        => QueueCompositeRun(text, originX, originY, color, clip, mergeWithPrevious: true);

    internal RasterPipelineDescription CompositePipeline => _pipeline;

    internal int QueueCompositeRun(
        ShapedText text,
        float originX,
        float originY,
        Vector4 color,
        PixelRect clip,
        bool mergeWithPrevious,
        uint producerRunId = 0,
        uint producerRunGeneration = 0,
        uint producerRunVersion = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (!float.IsFinite(originX) || !float.IsFinite(originY))
        {
            throw new ArgumentException("Text origin must contain finite components.", nameof(originX));
        }

        if (!IsFinite(color))
        {
            throw new ArgumentException("Text color must contain finite components.", nameof(color));
        }

        EnsurePendingCapacity(_pendingRunCount + 1);
        _pendingRuns.RefAt(_pendingRunCount) = new PendingRun(
            text,
            originX,
            originY,
            color,
            clip,
            mergeWithPrevious,
            new TextRunCacheKey(producerRunId, producerRunGeneration),
            producerRunVersion);
        return _pendingRunCount++;
    }

    /// <summary>Removes queued runs while retaining atlas/cache and GPU capacity.</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        Array.Clear(_pendingRuns, 0, _pendingRunCount);
        _pendingRunCount = 0;
        _instanceCount = 0;
        _batchCount = 0;
    }

    /// <summary>Updates the viewport after the owning session target is resized.</summary>
    public void Resize(PixelExtent viewport)
    {
        ThrowIfDisposed();
        if (viewport.IsEmpty)
        {
            throw new ArgumentException("The text viewport must be non-empty.", nameof(viewport));
        }

        _viewport = viewport;
    }

    /// <inheritdoc />
    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!PrepareComposite(graph))
        {
            return;
        }

        var target = graph.ImportTarget(_session.Target);

        var raster = graph.AddRasterPass(_rasterPassDescription, _drawPass);
        graph.UseColorAttachment(
            raster,
            0,
            new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
        ConfigureCompositePass(graph, raster);
    }

    internal bool PrepareComposite(IRenderGraphBuilder graph)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        BuildInstances();
        if (_instanceCount == 0)
        {
            return false;
        }

        var instances = graph.ImportBuffer(_instanceBuffer);
        _instanceGraphHandle = instances;
        EnsureUploadPageCapacity(_pageCount);
        _uploadPageCount = 0;
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = _pages.RefAt(pageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
            _pageGraphHandles.RefAt(pageIndex) = graph.ImportTexture(page.Texture);
            if (page.Dirty)
            {
                _uploadPageIndices.RefAt(_uploadPageCount++) = pageIndex;
            }
        }

        if (_instancePayloadDirty || _uploadPageCount > 0)
        {
            var upload = graph.AddTransferPass("DeltaRender.Text.Upload", _uploadPass);
            if (_instancePayloadDirty)
            {
                graph.UseBuffer(upload, instances, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }

            for (var uploadIndex = 0; uploadIndex < _uploadPageCount; uploadIndex++)
            {
                graph.UseTexture(upload, _pageGraphHandles.RefAt(_uploadPageIndices.RefAt(uploadIndex)), RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }
        }

        return true;
    }

    internal void ConfigureCompositePass(IRenderGraphBuilder graph, RenderGraphPassHandle pass)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.UseBuffer(pass, _instanceGraphHandle, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
        for (var batchIndex = 0; batchIndex < _batchCount; batchIndex++)
        {
            graph.UseTexture(pass, _pageGraphHandles.RefAt(_batches.RefAt(batchIndex).PageIndex), RenderResourceAccess.Read, RenderPipelineStages.Fragment);
        }
    }

    internal void RecordCompositeRuns(IRasterCommandContext commands, int firstRun, int runCount)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(commands);
        if (firstRun < 0 || runCount <= 0 || firstRun > _pendingRunCount - runCount)
        {
            throw new ArgumentOutOfRangeException(nameof(firstRun));
        }

        commands.SetViewport(new RenderViewport(0, 0, _viewport.Width, _viewport.Height));
        var pushConstantBytes = _pushConstantBytes.AsSpan(0, checked((int)_pushConstantSize));
        var packedPushConstantSize = PackTextParameters(pushConstantBytes);
        if (packedPushConstantSize != pushConstantBytes.Length)
        {
            throw new InvalidOperationException("The generated text packer wrote a byte count different from ShaderAbi push-constant size.");
        }

        commands.PushConstants(pushConstantBytes);
        commands.BindBuffer(
            _instanceBinding,
            _instanceGraphHandle,
            0,
            checked((ulong)_instanceCount * _instanceStride));

        var firstBatch = -1;
        var lastBatch = 0;
        for (var runIndex = firstRun; runIndex < firstRun + runCount; runIndex++)
        {
            var batchStart = _runBatchStarts.RefAt(runIndex);
            var batchCount = _runBatchCounts.RefAt(runIndex);
            if (batchCount == 0)
            {
                continue;
            }

            firstBatch = firstBatch < 0 ? batchStart : Math.Min(firstBatch, batchStart);
            lastBatch = Math.Max(lastBatch, checked(batchStart + batchCount));
        }

        if (firstBatch < 0)
        {
            return;
        }

        for (var batchIndex = firstBatch; batchIndex < lastBatch; batchIndex++)
        {
            var batch = _batches.RefAt(batchIndex);
            commands.BindTexture(
                _atlasBinding,
                _pageGraphHandles.RefAt(batch.PageIndex),
                _sampler);
            commands.SetScissor(batch.Clip);
            commands.Draw(6, checked((uint)batch.Count), 0, checked((uint)batch.Start));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearState();
        var instanceBuffer = _instanceBuffer;
        var sampler = _sampler;
        _instanceBuffer = default;
        _sampler = default;
        if (instanceBuffer.IsValid)
        {
            _session.Release(instanceBuffer);
        }

        if (sampler.IsValid)
        {
            _session.Release(sampler);
        }

        for (var pageIndex = _pageCount - 1; pageIndex >= 0; pageIndex--)
        {
            var page = _pages.RefAt(pageIndex);
            if (page is not null && page.Texture.IsValid)
            {
                _session.Release(page.Texture);
                page.Texture = default;
            }
        }
    }

    private void BuildInstances()
    {
        _instanceCount = 0;
        _batchCount = 0;
        var useStamp = NextAtlasUseStamp();
        EnsureRunBatchCapacity(_pendingRunCount);
        for (var runIndex = 0; runIndex < _pendingRunCount; runIndex++)
        {
            var pending = _pendingRuns.RefAt(runIndex);
            var penX = 0f;
            var penY = 0f;
            var runInstanceStart = _instanceCount;
            _localRunBatchCount = 0;
            if (TryReuseRun(pending, out var cachedRun))
            {
                EnsureInstanceCapacity(_instanceCount + cachedRun.InstanceCount);
                var destinationOffset = checked((int)((ulong)runInstanceStart * _instanceStride));
                cachedRun.PackedBytes.AsSpan(0, cachedRun.PackedByteCount)
                    .CopyTo(_instanceBytes.AsSpan(destinationOffset, cachedRun.PackedByteCount));
                var cachedFirstBatch = AppendCachedBatches(cachedRun, runInstanceStart, pending.MergeWithPrevious, useStamp);
                _instanceCount += cachedRun.InstanceCount;
                _runBatchStarts.RefAt(runIndex) = cachedFirstBatch < 0 ? 0 : cachedFirstBatch;
                _runBatchCounts.RefAt(runIndex) = cachedFirstBatch < 0 ? 0 : _batchCount - cachedFirstBatch;
                continue;
            }

            var firstBatch = -1;
            foreach (var run in pending.Text.Runs.Span)
            {
                var runX = pending.OriginX + penX;
                var runY = pending.OriginY + penY;
                var glyphPenX = 0f;
                var glyphPenY = 0f;
                foreach (var glyph in run.Glyphs.Span)
                {
                    var placement = GetOrCreateGlyph(run, glyph, useStamp);
                    if (!placement.IsEmpty && TryClip(pending.Clip, _viewport, out var clip))
                    {
                        EnsureInstanceCapacity(_instanceCount + 1);
                        var glyphX = runX + glyphPenX + glyph.OffsetX;
                        var glyphY = runY + glyphPenY + glyph.OffsetY;
                        var plane = placement.PlaneBounds;
                        _instances.RefAt(_instanceCount) = new GlyphInstance
                        {
                            PixelMin = new float2(glyphX + plane.Left, glyphY + plane.Top),
                            PixelMax = new float2(glyphX + plane.Right, glyphY + plane.Bottom),
                            UvRect = new float4(placement.UvRect.X, placement.UvRect.Y, placement.UvRect.Z, placement.UvRect.W),
                            Color = new float4(pending.Color.X, pending.Color.Y, pending.Color.Z, pending.Color.W),
                        };
                        var batchIndex = AppendBatch(clip, placement.PageIndex, _instanceCount, pending.MergeWithPrevious || firstBatch >= 0);
                        AppendLocalBatch(clip, placement.PageIndex, _instanceCount - runInstanceStart);
                        firstBatch = firstBatch < 0 ? batchIndex : firstBatch;
                        _instanceCount++;
                    }

                    glyphPenX += glyph.AdvanceX;
                    glyphPenY += glyph.AdvanceY;
                }

                penX += run.AdvanceX;
                penY += run.AdvanceY;
            }

            _runBatchStarts.RefAt(runIndex) = firstBatch < 0 ? 0 : firstBatch;
            _runBatchCounts.RefAt(runIndex) = firstBatch < 0 ? 0 : _batchCount - firstBatch;

            var runInstanceCount = _instanceCount - runInstanceStart;
            if (runInstanceCount > 0)
            {
                var destinationOffset = checked((int)((ulong)runInstanceStart * _instanceStride));
                var runByteCount = checked((int)((ulong)runInstanceCount * _instanceStride));
                var written = PackInstances(
                    _instances.AsSpan(runInstanceStart, runInstanceCount),
                    _instanceBytes.AsSpan(destinationOffset, runByteCount));
                if (written != runByteCount)
                {
                    throw new InvalidOperationException("The generated text packer wrote a byte count different from ShaderAbi stride.");
                }
            }

            StoreCachedRun(pending, runInstanceStart, runInstanceCount);
        }

        if (_instanceCount == 0)
        {
            _instancePayloadDirty = false;
            return;
        }

        var byteCount = checked((int)((ulong)_instanceCount * _instanceStride));
        var bytes = _instanceBytes.AsSpan(0, byteCount);

        _instancePayloadDirty = _instanceBufferNeedsUpload ||
            _uploadedInstanceByteCount != byteCount ||
            !bytes.SequenceEqual(_uploadedInstanceBytes.AsSpan(0, byteCount));
    }

    private GlyphPlacement GetOrCreateGlyph(in ShapedRun run, in ShapedGlyph glyph, ulong useStamp)
    {
        var key = new GlyphKey(run.Font, glyph.GlyphId, run.PixelsPerEm, _mode, _encoding, _distanceRange, _padding, _colorOptions);
        if (_glyphs.TryGetValue(key, out var placement))
        {
            if (!placement.IsEmpty)
            {
                var cachedPage = _pages.RefAt(placement.PageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
                cachedPage.LastUse = useStamp;
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
        if (width > _atlasWidth || height > _atlasHeight)
        {
            throw new InvalidOperationException("A glyph image does not fit in the configured text atlas.");
        }

        var pageIndex = FindPage(width, height);
        if (pageIndex < 0)
        {
            pageIndex = CreateAtlasPageSlot(useStamp);
        }

        var page = _pages.RefAt(pageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
        PreparePagePlacement(page, width, height);
        var destinationX = page.CursorX;
        var destinationY = page.CursorY;

        CopyImage(image, page.Pixels, destinationX, destinationY);
        placement = new GlyphPlacement(
            pageIndex,
            image.PlaneBounds,
            new Vector4(
                (float)destinationX / _atlasWidth,
                (float)destinationY / _atlasHeight,
                (float)(destinationX + width) / _atlasWidth,
                (float)(destinationY + height) / _atlasHeight));
        _glyphs.Add(key, placement);
        page.CursorX = checked(destinationX + width + _padding);
        page.RowHeight = Math.Max(page.RowHeight, height);
        page.LastUse = useStamp;
        page.Dirty = true;
        return placement;
    }

    private ulong NextAtlasUseStamp()
    {
        if (_atlasUseStamp == ulong.MaxValue)
        {
            _atlasUseStamp = 1;
            for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
            {
                var page = _pages.RefAt(pageIndex);
                if (page is not null)
                {
                    page.LastUse = 0;
                }
            }

            return _atlasUseStamp;
        }

        return ++_atlasUseStamp;
    }

    private int FindPage(uint width, uint height)
    {
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = _pages.RefAt(pageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
            if (CanPlace(page, width, height))
            {
                return pageIndex;
            }
        }

        return -1;
    }

    private bool CanPlace(AtlasPage page, uint width, uint height)
    {
        var destinationY = page.CursorY;
        if (page.CursorX > _atlasWidth - width)
        {
            destinationY = checked(destinationY + page.RowHeight + _padding);
        }

        return destinationY <= _atlasHeight - height;
    }

    private void PreparePagePlacement(AtlasPage page, uint width, uint height)
    {
        if (page.CursorX > _atlasWidth - width)
        {
            page.CursorX = 0;
            page.CursorY = checked(page.CursorY + page.RowHeight + _padding);
            page.RowHeight = 0;
        }

        if (page.CursorY > _atlasHeight - height)
        {
            throw new InvalidOperationException("The selected text atlas page cannot fit the glyph image.");
        }
    }

    private int CreateAtlasPageSlot(ulong useStamp)
    {
        if (_pageCount < DefaultMaxAtlasPages)
        {
            EnsurePageCapacity(_pageCount + 1);
            var page = CreateAtlasPage();
            _pages.RefAt(_pageCount) = page;
            return _pageCount++;
        }

        return RecycleAtlasPage(useStamp);
    }

    private int RecycleAtlasPage(ulong useStamp)
    {
        var pageIndexToRecycle = -1;
        var oldestUseStamp = ulong.MaxValue;
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var page = _pages.RefAt(pageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
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

        var pageToRecycle = _pages.RefAt(pageIndexToRecycle) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
        Array.Clear(pageToRecycle.Pixels);
        pageToRecycle.CursorX = 0;
        pageToRecycle.CursorY = 0;
        pageToRecycle.RowHeight = 0;
        pageToRecycle.LastUse = useStamp;
        pageToRecycle.Dirty = true;
        _atlasEpoch = _atlasEpoch == ulong.MaxValue ? 1 : _atlasEpoch + 1;
        return pageIndexToRecycle;
    }

    private AtlasPage CreateAtlasPage()
    {
        var pixels = new byte[checked((int)((ulong)_atlasWidth * _atlasHeight * (uint)_atlasBytesPerPixel))];
        var page = new AtlasPage(pixels);
        try
        {
            page.Texture = _session.CreateTexture(new RenderTextureDescription(
                _atlasWidth,
                _atlasHeight,
                _atlasFormat,
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

        var capacity = GrowCapacity(_pages.Length, required);

        Array.Resize(ref _pages, capacity);
        Array.Resize(ref _pageGraphHandles, capacity);
    }

    private void EnsureUploadPageCapacity(int required)
    {
        if (required <= _uploadPageIndices.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_uploadPageIndices.Length, required);

        Array.Resize(ref _uploadPageIndices, capacity);
    }

    private void CopyImage(GlyphImage image, byte[] destinationPixels, uint destinationX, uint destinationY)
    {
        var sourceBytesPerPixel = image.Encoding == GlyphImageEncoding.MsdfRgb8 ? 3 : _atlasBytesPerPixel;
        var sourceRowBytes = checked(image.Width * sourceBytesPerPixel);
        var destinationRowBytes = checked((int)_atlasWidth * _atlasBytesPerPixel);
        var source = image.Pixels.Span;
        for (var row = 0; row < image.Height; row++)
        {
            var sourceRow = source.Slice(row * sourceRowBytes, sourceRowBytes);
            var destinationOffset = checked(((int)destinationY + row) * destinationRowBytes + (int)destinationX * _atlasBytesPerPixel);
            var destinationRow = destinationPixels.AsSpan(destinationOffset, checked(image.Width * _atlasBytesPerPixel));
            if (sourceBytesPerPixel == _atlasBytesPerPixel)
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

        if (image.Width < 0 || image.Height < 0 || image.Width > _atlasWidth || image.Height > _atlasHeight)
        {
            throw new InvalidOperationException("The text service returned invalid glyph image dimensions.");
        }

        var expectedLength = checked((long)image.Width * image.Height * (_encoding == GlyphImageEncoding.MsdfRgb8 ? 3 : _atlasBytesPerPixel));
        if (image.Pixels.Length != expectedLength)
        {
            throw new InvalidOperationException("The text service returned a glyph image with an invalid pixel payload length.");
        }

        if (!IsFinite(image.PlaneBounds))
        {
            throw new InvalidOperationException("The text service returned non-finite glyph plane bounds.");
        }
    }

    private int AppendBatch(PixelRect clip, int pageIndex, int instance, bool allowMerge, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (allowMerge && _batchCount > 0)
        {
            ref var last = ref _batches.RefAt(_batchCount - 1);
            if (last.PageIndex == pageIndex && last.Clip == clip && last.Start + last.Count == instance)
            {
                last.Count = checked(last.Count + count);
                return _batchCount - 1;
            }
        }

        EnsureBatchCapacity(_batchCount + 1);
        _batches.RefAt(_batchCount) = new TextBatch(pageIndex, clip, instance, count);
        return _batchCount++;
    }

    private void AppendLocalBatch(PixelRect clip, int pageIndex, int instance)
    {
        if (_localRunBatchCount > 0)
        {
            ref var last = ref _localRunBatches.RefAt(_localRunBatchCount - 1);
            if (last.PageIndex == pageIndex && last.Clip == clip && last.Start + last.Count == instance)
            {
                last.Count++;
                return;
            }
        }

        EnsureLocalBatchCapacity(_localRunBatchCount + 1);
        _localRunBatches.RefAt(_localRunBatchCount++) = new TextBatch(pageIndex, clip, instance, 1);
    }

    private int AppendCachedBatches(CachedRun cachedRun, int instanceStart, bool mergeWithPrevious, ulong useStamp)
    {
        var firstBatch = -1;
        for (var batchIndex = 0; batchIndex < cachedRun.BatchCount; batchIndex++)
        {
            var cachedBatch = cachedRun.Batches.RefAt(batchIndex);
            var page = _pages.RefAt(cachedBatch.PageIndex) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
            page.LastUse = useStamp;
            var batch = AppendBatch(
                cachedBatch.Clip,
                cachedBatch.PageIndex,
                checked(instanceStart + cachedBatch.Start),
                mergeWithPrevious || batchIndex > 0,
                cachedBatch.Count);
            firstBatch = firstBatch < 0 ? batch : firstBatch;
        }

        return firstBatch;
    }

    private bool TryReuseRun(in PendingRun pending, [NotNullWhen(true)] out CachedRun? cachedRun)
    {
        if (!pending.CacheKey.IsValid || !_runCache.TryGetValue(pending.CacheKey, out var candidate))
        {
            cachedRun = null;
            return false;
        }

        cachedRun = candidate;

        if (cachedRun.Version != pending.Version ||
            cachedRun.OriginX != pending.OriginX ||
            cachedRun.OriginY != pending.OriginY ||
            cachedRun.Color != pending.Color ||
            cachedRun.Clip != pending.Clip ||
            cachedRun.MergeWithPrevious != pending.MergeWithPrevious ||
            cachedRun.AtlasEpoch != _atlasEpoch)
        {
            return false;
        }

        return true;
    }

    private void StoreCachedRun(in PendingRun pending, int instanceStart, int instanceCount)
    {
        if (!pending.CacheKey.IsValid)
        {
            return;
        }

        if (!_runCache.TryGetValue(pending.CacheKey, out var cachedRun))
        {
            cachedRun = new CachedRun();
            _runCache.Add(pending.CacheKey, cachedRun);
        }

        var byteCount = checked((int)((ulong)instanceCount * _instanceStride));
        if (cachedRun.PackedBytes.Length < byteCount)
        {
            cachedRun.PackedBytes = new byte[byteCount];
        }

        if (byteCount > 0)
        {
            var sourceOffset = checked((int)((ulong)instanceStart * _instanceStride));
            _instanceBytes.AsSpan(sourceOffset, byteCount).CopyTo(cachedRun.PackedBytes);
        }

        if (cachedRun.Batches.Length < _localRunBatchCount)
        {
            cachedRun.Batches = new TextBatch[_localRunBatchCount];
        }

        _localRunBatches.AsSpan(0, _localRunBatchCount).CopyTo(cachedRun.Batches);
        cachedRun.PackedByteCount = byteCount;
        cachedRun.InstanceCount = instanceCount;
        cachedRun.BatchCount = _localRunBatchCount;
        cachedRun.Version = pending.Version;
        cachedRun.OriginX = pending.OriginX;
        cachedRun.OriginY = pending.OriginY;
        cachedRun.Color = pending.Color;
        cachedRun.Clip = pending.Clip;
        cachedRun.MergeWithPrevious = pending.MergeWithPrevious;
        cachedRun.AtlasEpoch = _atlasEpoch;
    }

    private void EnsureInstanceCapacity(int required)
    {
        if (required <= _instances.Length)
        {
            return;
        }

        var capacity = _instances.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }
        var replacement = new GlyphInstance[capacity];
        _instances.AsSpan(0, _instanceCount).CopyTo(replacement);
        var replacementByteCapacity = checked((int)((ulong)capacity * _instanceStride));
        var replacementHandle = _session.CreateBuffer(new RenderBufferDescription(
            checked((ulong)capacity * _instanceStride),
            RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
        var oldHandle = _instanceBuffer;
        _instances = replacement;
        if (_instanceBytes.Length < replacementByteCapacity)
        {
            var previousBytes = _instanceBytes;
            var replacementBytes = new byte[replacementByteCapacity];
            var retainedByteCount = checked((int)((ulong)_instanceCount * _instanceStride));
            previousBytes.AsSpan(0, retainedByteCount).CopyTo(replacementBytes);
            _instanceBytes = replacementBytes;
            _uploadedInstanceBytes = new byte[replacementByteCapacity];
        }

        _instanceBuffer = replacementHandle;
        _instanceBufferNeedsUpload = true;
        if (oldHandle.IsValid)
        {
            _session.Release(oldHandle);
        }
    }

    private void EnsurePendingCapacity(int required)
    {
        if (required <= _pendingRuns.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_pendingRuns.Length, required);
        Array.Resize(ref _pendingRuns, capacity);
    }

    private void EnsureBatchCapacity(int required)
    {
        if (required <= _batches.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_batches.Length, required);
        Array.Resize(ref _batches, capacity);
    }

    private void EnsureLocalBatchCapacity(int required)
    {
        if (required <= _localRunBatches.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_localRunBatches.Length, required);

        Array.Resize(ref _localRunBatches, capacity);
    }

    private void EnsureRunBatchCapacity(int required)
    {
        if (required <= _runBatchStarts.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_runBatchStarts.Length, required);

        Array.Resize(ref _runBatchStarts, capacity);
        Array.Resize(ref _runBatchCounts, capacity);
    }

    private void ClearState()
    {
        Array.Clear(_pendingRuns, 0, _pendingRunCount);
        _pendingRunCount = 0;
        _instanceCount = 0;
        _batchCount = 0;
        Array.Clear(_runBatchStarts, 0, _runBatchStarts.Length);
        Array.Clear(_runBatchCounts, 0, _runBatchCounts.Length);
        _glyphs.Clear();
        _runCache.Clear();
    }

    private static int GrowCapacity(int current, int required)
    {
        var capacity = Math.Max(1, current);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    private static (GlyphImageEncoding Encoding, RenderTextureFormat Format, int BytesPerPixel) DescribeImageFormat(GlyphImageMode mode)
        => mode switch
        {
            GlyphImageMode.Coverage => (GlyphImageEncoding.CoverageR8, RenderTextureFormat.R8Unorm, 1),
            GlyphImageMode.Sdf => (GlyphImageEncoding.SdfR8, RenderTextureFormat.R8Unorm, 1),
            GlyphImageMode.Msdf => (GlyphImageEncoding.MsdfRgb8, RenderTextureFormat.Rgba8Unorm, 4),
            GlyphImageMode.Color => (GlyphImageEncoding.ColorRgba8PremultipliedSrgb, RenderTextureFormat.Rgba8Srgb, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The text feature requires a concrete glyph image mode."),
        };

    private static uint FindInstanceStride(IGraphicsShaderProgram program, ShaderBinding binding)
    {
        ShaderResourceBinding? found = null;
        foreach (var resource in program.Vertex.Abi.Resources)
        {
            if (resource.Binding != binding || resource.Kind != ShaderResourceKind.StorageBuffer ||
                !resource.Stages.HasFlag(ShaderStageMask.Vertex) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible instance buffer.", nameof(program));
            }

            found = resource;
        }

        if (found is null || found.Layout.ArrayStride == 0)
        {
            throw new ArgumentException("The shader manifest has no resolved instance-buffer array stride.", nameof(program));
        }

        return found.Layout.ArrayStride;
    }

    private int PackInstances(ReadOnlySpan<GlyphInstance> values, Span<byte> destination)
    {
        return _mode == GlyphImageMode.Msdf
            ? MsdfTextGraphicsShaderProgram.PackMsdfTextVertexGlyphsElements(values, destination)
            : SdfTextGraphicsShaderProgram.PackSdfTextVertexGlyphsElements(values, destination);
    }

    private int PackTextParameters(Span<byte> destination)
    {
        var parameters = new TextParameters
        {
            Resolution = new float2(_viewport.Width, _viewport.Height),
            TextColor = new float4(1, 1, 1, 1),
            OutlineColor = default,
            OutlineWidth = 0,
            DistanceRange = _distanceRange,
        };

        return _mode == GlyphImageMode.Msdf
            ? MsdfTextGraphicsShaderProgram.PackMsdfTextVertexParameters(in parameters, destination)
            : SdfTextGraphicsShaderProgram.PackSdfTextVertexParameters(in parameters, destination);
    }

    private static uint FindPushConstantSize(IGraphicsShaderProgram program)
    {
        var vertexPushConstants = program.Vertex.Abi.PushConstants;
        var fragmentPushConstants = program.Fragment.Abi.PushConstants;
        if (vertexPushConstants.Count != 1 || fragmentPushConstants.Count != 1)
        {
            throw new ArgumentException("The text shader manifest must expose one push-constant range per graphics stage.", nameof(program));
        }

        var vertex = vertexPushConstants[0];
        var fragment = fragmentPushConstants[0];
        if (vertex.Offset != fragment.Offset || vertex.Size != fragment.Size || vertex.Size == 0)
        {
            throw new ArgumentException("The text shader stages must expose matching non-empty push-constant ranges.", nameof(program));
        }

        return vertex.Size;
    }

    private static ShaderBinding FindBinding(
        IGraphicsShaderProgram program,
        ShaderResourceKind kind,
        ShaderStageMask stage,
        string description)
    {
        var found = FindBinding(program.Vertex.Abi.Resources, kind, stage);
        var fragmentFound = FindBinding(program.Fragment.Abi.Resources, kind, stage);
        if (found.HasValue && fragmentFound.HasValue && found.Value != fragmentFound.Value)
        {
            throw new ArgumentException($"The shader manifest contains multiple {description} resources.", nameof(program));
        }

        found ??= fragmentFound;
        if (!found.HasValue)
        {
            throw new ArgumentException($"The shader manifest has no {description} resource.", nameof(program));
        }

        return found.Value;
    }

    private static ShaderBinding FindTextureBinding(IGraphicsShaderProgram program, string description)
    {
        var found = FindTextureBinding(program.Vertex.Abi.Resources);
        var fragmentFound = FindTextureBinding(program.Fragment.Abi.Resources);
        if (found.HasValue && fragmentFound.HasValue && found.Value != fragmentFound.Value)
        {
            throw new ArgumentException($"The shader manifest contains multiple {description} resources.", nameof(program));
        }

        found ??= fragmentFound;
        if (!found.HasValue)
        {
            throw new ArgumentException($"The shader manifest has no {description} resource.", nameof(program));
        }

        return found.Value;
    }

    private static ShaderBinding? FindBinding(IReadOnlyList<ShaderResourceBinding> resources, ShaderResourceKind kind, ShaderStageMask stage)
    {
        ShaderBinding? found = null;
        foreach (var resource in resources)
        {
            if (resource.Kind != kind || !resource.Stages.HasFlag(stage) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found.HasValue && found.Value != resource.Binding)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible text resource.");
            }

            found = resource.Binding;
        }

        return found;
    }

    private static ShaderBinding? FindTextureBinding(IReadOnlyList<ShaderResourceBinding> resources)
    {
        ShaderBinding? found = null;
        foreach (var resource in resources)
        {
            if ((resource.Kind != ShaderResourceKind.SampledTexture && resource.Kind != ShaderResourceKind.CombinedTextureSampler) ||
                !resource.Stages.HasFlag(ShaderStageMask.Fragment) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found.HasValue && found.Value != resource.Binding)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible atlas resource.");
            }

            found = resource.Binding;
        }

        return found;
    }

    private static bool TryClip(PixelRect requested, PixelExtent viewport, out PixelRect clip)
    {
        var left = Math.Max(0L, requested.X);
        var top = Math.Max(0L, requested.Y);
        var right = Math.Min((long)viewport.Width, (long)requested.X + requested.Width);
        var bottom = Math.Min((long)viewport.Height, (long)requested.Y + requested.Height);
        if (right <= left || bottom <= top)
        {
            clip = default;
            return false;
        }

        clip = new PixelRect((int)left, (int)top, checked((int)(right - left)), checked((int)(bottom - top)));
        return true;
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(TextBounds value)
        => float.IsFinite(value.Left) && float.IsFinite(value.Top) && float.IsFinite(value.Right) && float.IsFinite(value.Bottom);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct PendingRun(
        ShapedText Text,
        float OriginX,
        float OriginY,
        Vector4 Color,
        PixelRect Clip,
        bool MergeWithPrevious,
        TextRunCacheKey CacheKey,
        uint Version);

    private readonly record struct TextRunCacheKey(uint Value, uint Generation)
    {
        public bool IsValid => Value != 0 && Generation != 0;
    }

    private sealed class CachedRun
    {
        public byte[] PackedBytes { get; set; } = [];
        public TextBatch[] Batches { get; set; } = new TextBatch[4];
        public int PackedByteCount;
        public int InstanceCount;
        public int BatchCount;
        public uint Version;
        public float OriginX;
        public float OriginY;
        public Vector4 Color;
        public PixelRect Clip;
        public bool MergeWithPrevious;
        public ulong AtlasEpoch;
    }

    private readonly record struct GlyphKey(
        FontInstanceId Font,
        uint GlyphId,
        float PixelsPerEm,
        GlyphImageMode Mode,
        GlyphImageEncoding Encoding,
        float DistanceRange,
        uint Padding,
        ColorGlyphOptions? Color);

    private readonly record struct GlyphPlacement(int PageIndex, TextBounds PlaneBounds, Vector4 UvRect)
    {
        public static GlyphPlacement Empty => new(-1, default, default);

        public bool IsEmpty => PageIndex < 0;
    }

    private struct TextBatch
    {
        public TextBatch(int pageIndex, PixelRect clip, int start, int count)
        {
            PageIndex = pageIndex;
            Clip = clip;
            Start = start;
            Count = count;
        }

        public int PageIndex;
        public PixelRect Clip;
        public int Start;
        public int Count;
    }

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

    private sealed class TextUploadPass(TextRenderFeature owner) : ITransferPass
    {
        public void Record(ITransferCommandContext commands)
        {
            try
            {
                for (var uploadIndex = 0; uploadIndex < owner._uploadPageCount; uploadIndex++)
                {
                    var page = owner._pages.RefAt(owner._uploadPageIndices.RefAt(uploadIndex)) ?? throw new InvalidOperationException("The text atlas page is unavailable.");
                    commands.UploadTexture(
                        owner._pageGraphHandles.RefAt(owner._uploadPageIndices.RefAt(uploadIndex)),
                        new PixelRect(0, 0, checked((int)owner._atlasWidth), checked((int)owner._atlasHeight)),
                        page.Pixels,
                        checked(owner._atlasWidth * (uint)owner._atlasBytesPerPixel));
                }

                if (owner._instancePayloadDirty)
                {
                    var byteCount = checked((int)((ulong)owner._instanceCount * owner._instanceStride));
                    var bytes = owner._instanceBytes.AsSpan(0, byteCount);
                    commands.UploadBuffer(owner._instanceGraphHandle, bytes);
                    bytes.CopyTo(owner._uploadedInstanceBytes);
                    owner._uploadedInstanceByteCount = byteCount;
                    owner._instancePayloadDirty = false;
                    owner._instanceBufferNeedsUpload = false;
                }
            }
            catch
            {
                for (var uploadIndex = 0; uploadIndex < owner._uploadPageCount; uploadIndex++)
                {
                    var page = owner._pages.RefAt(owner._uploadPageIndices.RefAt(uploadIndex));
                    if (page is not null)
                    {
                        page.Dirty = true;
                    }
                }

                throw;
            }

            for (var uploadIndex = 0; uploadIndex < owner._uploadPageCount; uploadIndex++)
            {
                var page = owner._pages.RefAt(owner._uploadPageIndices.RefAt(uploadIndex));
                if (page is not null)
                {
                    page.Dirty = false;
                }
            }

        }
    }

    private sealed class TextDrawPass(TextRenderFeature owner) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
            => owner.RecordCompositeRuns(commands, 0, owner._pendingRunCount);
    }

    private RenderGraphBufferHandle _instanceGraphHandle;
}
