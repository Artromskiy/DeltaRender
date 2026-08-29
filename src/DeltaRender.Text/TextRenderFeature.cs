using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Text.Contract;

namespace Delta.Render.Text;

/// <summary>
/// Reusable graph feature for positioned text. The caller supplies already shaped
/// text and owns the feature between queueing runs and graph execution.
/// </summary>
/// <remarks>
/// This adapter consumes DeltaText values synchronously during graph build. It
/// owns one format-specific atlas page, sampler, instance buffer and cache; the
/// session owns the native resources. It does not shape text or retain strings.
/// </remarks>
public sealed class TextRenderFeature : IRenderFeature, IDisposable
{
    private const int InitialInstanceCapacity = 256;
    private const uint DefaultAtlasSize = 2048;
    private const uint DefaultPadding = 1;

    private readonly IRenderFrameSession _session;
    private readonly ITextService _textService;
    private readonly TextUploadPass _uploadPass;
    private readonly TextDrawPass _drawPass;
    private readonly RasterPipelineDescription _pipeline;
    private readonly GlyphImageMode _mode;
    private readonly GlyphImageEncoding _encoding;
    private readonly RenderTextureFormat _atlasFormat;
    private readonly int _atlasBytesPerPixel;
    private readonly uint _atlasWidth;
    private readonly uint _atlasHeight;
    private readonly uint _padding;
    private readonly float _distanceRange;
    private readonly ColorGlyphOptions? _colorOptions;
    private readonly ShaderBinding _instanceBinding;
    private readonly ShaderBinding _atlasBinding;
    private PendingRun[] _pendingRuns = new PendingRun[8];
    private readonly Dictionary<GlyphKey, GlyphPlacement> _glyphs = new();
    private readonly byte[] _atlasPixels;

    private TextGlyphGpu[] _instances;
    private TextBatch[] _batches;
    private int[] _runBatchStarts = new int[8];
    private int[] _runBatchCounts = new int[8];
    private RenderTextureHandle _atlas;
    private RenderBufferHandle _instanceBuffer;
    private RenderSamplerHandle _sampler;
    private uint _cursorX;
    private uint _cursorY;
    private uint _rowHeight;
    private int _pendingRunCount;
    private int _instanceCount;
    private int _batchCount;
    private PixelExtent _viewport;
    private bool _atlasDirty;
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
        _atlasBinding = FindTextureBinding(shaderProgram, "fragment atlas texture");

        _session = session;
        _textService = textService;
        _mode = mode;
        _atlasWidth = atlasWidth;
        _atlasHeight = atlasHeight;
        _padding = padding;
        _distanceRange = mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf ? distanceRange : 0;
        _colorOptions = colorOptions;
        _viewport = viewport;
        _atlasPixels = new byte[checked((int)((ulong)atlasWidth * atlasHeight * (uint)_atlasBytesPerPixel))];
        _instances = new TextGlyphGpu[InitialInstanceCapacity];
        _batches = new TextBatch[16];
        _uploadPass = new TextUploadPass(this);
        _drawPass = new TextDrawPass(this);
        _pipeline = new RasterPipelineDescription(
            shaderProgram,
            topology: PrimitiveTopology.TriangleList,
            cullMode: RasterCullMode.None,
            blendMode: RenderBlendMode.Alpha);

        RenderTextureHandle atlas = default;
        RenderBufferHandle instanceBuffer = default;
        RenderSamplerHandle sampler = default;
        try
        {
            atlas = session.CreateTexture(new RenderTextureDescription(
                atlasWidth,
                atlasHeight,
                _atlasFormat,
                RenderTextureUsage.Sampled | RenderTextureUsage.TransferDestination));
            sampler = session.CreateSampler(new RenderSamplerDescription());
            instanceBuffer = session.CreateBuffer(new RenderBufferDescription(
                checked((ulong)InitialInstanceCapacity * (ulong)Marshal.SizeOf<TextGlyphGpu>()),
                RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
            _atlas = atlas;
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

            if (atlas.IsValid)
            {
                session.Release(atlas);
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
        bool mergeWithPrevious)
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
        _pendingRuns[_pendingRunCount] = new PendingRun(text, originX, originY, color, clip, mergeWithPrevious);
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

        var raster = graph.AddRasterPass(new RasterPassDescription("DeltaRender.Text.Draw", _pipeline), _drawPass);
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

        var atlas = graph.ImportTexture(_atlas);
        var instances = graph.ImportBuffer(_instanceBuffer);
        _atlasGraphHandle = atlas;
        _instanceGraphHandle = instances;

        var upload = graph.AddTransferPass("DeltaRender.Text.Upload", _uploadPass);
        graph.UseBuffer(upload, instances, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        if (_atlasDirty)
        {
            graph.UseTexture(upload, atlas, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        }

        return true;
    }

    internal void ConfigureCompositePass(IRenderGraphBuilder graph, RenderGraphPassHandle pass)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.UseBuffer(pass, _instanceGraphHandle, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
        graph.UseTexture(pass, _atlasGraphHandle, RenderResourceAccess.Read, RenderPipelineStages.Fragment);
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
        commands.BindTexture(_atlasBinding, _atlasGraphHandle, _sampler);
        commands.BindBuffer(
            _instanceBinding,
            _instanceGraphHandle,
            0,
            checked((ulong)_instanceCount * (ulong)Marshal.SizeOf<TextGlyphGpu>()));

        var firstBatch = -1;
        var lastBatch = 0;
        for (var runIndex = firstRun; runIndex < firstRun + runCount; runIndex++)
        {
            var batchStart = _runBatchStarts[runIndex];
            var batchCount = _runBatchCounts[runIndex];
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
            var batch = _batches[batchIndex];
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
        var atlas = _atlas;
        _instanceBuffer = default;
        _sampler = default;
        _atlas = default;
        if (instanceBuffer.IsValid)
        {
            _session.Release(instanceBuffer);
        }

        if (sampler.IsValid)
        {
            _session.Release(sampler);
        }

        if (atlas.IsValid)
        {
            _session.Release(atlas);
        }
    }

    private void BuildInstances()
    {
        _instanceCount = 0;
        _batchCount = 0;
        EnsureRunBatchCapacity(_pendingRunCount);
        var penX = 0f;
        var penY = 0f;
        for (var runIndex = 0; runIndex < _pendingRunCount; runIndex++)
        {
            var pending = _pendingRuns[runIndex];
            var firstBatch = -1;
            foreach (var run in pending.Text.Runs.Span)
            {
                var runX = pending.OriginX + penX;
                var runY = pending.OriginY + penY;
                foreach (var glyph in run.Glyphs.Span)
                {
                    var placement = GetOrCreateGlyph(run, glyph);
                    if (!placement.IsEmpty && TryClip(pending.Clip, _viewport, out var clip))
                    {
                        EnsureInstanceCapacity(_instanceCount + 1);
                        var glyphX = runX + glyph.OffsetX;
                        var glyphY = runY + glyph.OffsetY;
                        var plane = placement.PlaneBounds;
                        _instances[_instanceCount] = new TextGlyphGpu
                        {
                            PixelMin = new Vector2(glyphX + plane.Left, glyphY + plane.Top),
                            PixelMax = new Vector2(glyphX + plane.Right, glyphY + plane.Bottom),
                            UvRect = placement.UvRect,
                            Color = pending.Color,
                        };
                        var batchIndex = AppendBatch(clip, _instanceCount, pending.MergeWithPrevious || firstBatch >= 0);
                        firstBatch = firstBatch < 0 ? batchIndex : firstBatch;
                        _instanceCount++;
                    }
                }

                penX += run.AdvanceX;
                penY += run.AdvanceY;
            }

            _runBatchStarts[runIndex] = firstBatch < 0 ? 0 : firstBatch;
            _runBatchCounts[runIndex] = firstBatch < 0 ? 0 : _batchCount - firstBatch;
        }
    }

    private GlyphPlacement GetOrCreateGlyph(in ShapedRun run, in ShapedGlyph glyph)
    {
        var key = new GlyphKey(run.Font, glyph.GlyphId, run.PixelsPerEm, _mode, _encoding, _distanceRange, _padding, _colorOptions);
        if (_glyphs.TryGetValue(key, out var placement))
        {
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

        if (_cursorX > _atlasWidth - width)
        {
            _cursorX = 0;
            _cursorY = checked(_cursorY + _rowHeight + _padding);
            _rowHeight = 0;
        }

        if (_cursorY > _atlasHeight - height)
        {
            throw new InvalidOperationException("The text atlas is full; create a larger feature atlas.");
        }

        CopyImage(image, _cursorX, _cursorY);
        placement = new GlyphPlacement(
            image.PlaneBounds,
            new Vector4(
                (float)_cursorX / _atlasWidth,
                (float)_cursorY / _atlasHeight,
                (float)(_cursorX + width) / _atlasWidth,
                (float)(_cursorY + height) / _atlasHeight));
        _glyphs.Add(key, placement);
        _cursorX = checked(_cursorX + width + _padding);
        _rowHeight = Math.Max(_rowHeight, height);
        _atlasDirty = true;
        return placement;
    }

    private void CopyImage(GlyphImage image, uint destinationX, uint destinationY)
    {
        var sourceBytesPerPixel = image.Encoding == GlyphImageEncoding.MsdfRgb8 ? 3 : _atlasBytesPerPixel;
        var sourceRowBytes = checked(image.Width * sourceBytesPerPixel);
        var destinationRowBytes = checked((int)_atlasWidth * _atlasBytesPerPixel);
        var source = image.Pixels.Span;
        for (var row = 0; row < image.Height; row++)
        {
            var sourceRow = source.Slice(row * sourceRowBytes, sourceRowBytes);
            var destinationOffset = checked(((int)destinationY + row) * destinationRowBytes + (int)destinationX * _atlasBytesPerPixel);
            var destinationRow = _atlasPixels.AsSpan(destinationOffset, checked(image.Width * _atlasBytesPerPixel));
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

    private int AppendBatch(PixelRect clip, int instance, bool allowMerge)
    {
        if (allowMerge && _batchCount > 0)
        {
            ref var last = ref _batches[_batchCount - 1];
            if (last.Clip == clip && last.Start + last.Count == instance)
            {
                last.Count++;
                return _batchCount - 1;
            }
        }

        EnsureBatchCapacity(_batchCount + 1);
        _batches[_batchCount] = new TextBatch(clip, instance, 1);
        return _batchCount++;
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
        var replacement = new TextGlyphGpu[capacity];
        _instances.AsSpan(0, _instanceCount).CopyTo(replacement);
        var replacementHandle = _session.CreateBuffer(new RenderBufferDescription(
            checked((ulong)capacity * (ulong)Marshal.SizeOf<TextGlyphGpu>()),
            RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
        var oldHandle = _instanceBuffer;
        _instances = replacement;
        _instanceBuffer = replacementHandle;
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

        var capacity = _pendingRuns.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }
        Array.Resize(ref _pendingRuns, capacity);
    }

    private void EnsureBatchCapacity(int required)
    {
        if (required <= _batches.Length)
        {
            return;
        }

        var capacity = _batches.Length * 2;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }
        Array.Resize(ref _batches, capacity);
    }

    private void EnsureRunBatchCapacity(int required)
    {
        if (required <= _runBatchStarts.Length)
        {
            return;
        }

        var capacity = _runBatchStarts.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

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
        bool MergeWithPrevious);

    private readonly record struct GlyphKey(
        FontInstanceId Font,
        uint GlyphId,
        float PixelsPerEm,
        GlyphImageMode Mode,
        GlyphImageEncoding Encoding,
        float DistanceRange,
        uint Padding,
        ColorGlyphOptions? Color);

    private readonly record struct GlyphPlacement(TextBounds PlaneBounds, Vector4 UvRect)
    {
        public static GlyphPlacement Empty => new(default, default);

        public bool IsEmpty => UvRect == default;
    }

    private struct TextBatch
    {
        public TextBatch(PixelRect clip, int start, int count)
        {
            Clip = clip;
            Start = start;
            Count = count;
        }

        public PixelRect Clip;
        public int Start;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
    private struct TextGlyphGpu
    {
        public Vector2 PixelMin;
        public Vector2 PixelMax;
        public Vector4 UvRect;
        public Vector4 Color;
    }

    private sealed class TextUploadPass(TextRenderFeature owner) : ITransferPass
    {
        public void Record(ITransferCommandContext commands)
        {
            if (owner._atlasDirty)
            {
                commands.UploadTexture(
                    owner._atlasGraphHandle,
                    new PixelRect(0, 0, checked((int)owner._atlasWidth), checked((int)owner._atlasHeight)),
                    owner._atlasPixels,
                    checked(owner._atlasWidth * (uint)owner._atlasBytesPerPixel));
                owner._atlasDirty = false;
            }

            var bytes = MemoryMarshal.AsBytes(owner._instances.AsSpan(0, owner._instanceCount));
            commands.UploadBuffer(owner._instanceGraphHandle, bytes);
        }
    }

    private sealed class TextDrawPass(TextRenderFeature owner) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
            => owner.RecordCompositeRuns(commands, 0, owner._pendingRunCount);
    }

    private RenderGraphTextureHandle _atlasGraphHandle;
    private RenderGraphBufferHandle _instanceGraphHandle;
}
