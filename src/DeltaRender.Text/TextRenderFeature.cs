using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Delta;
using Delta.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Render.Text;
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
    private const uint DefaultAtlasSize = 2048;
    private const uint DefaultPadding = 1;

    private readonly IRenderFrameSession _session;
    private readonly TextAtlas _atlas;
    private readonly TextUploadPass _uploadPass;
    private readonly TextDrawPass _drawPass;
    private readonly RasterPipelineDescription _pipeline;
    private readonly RasterPassDescription _rasterPassDescription;
    private readonly GlyphImageMode _mode;
    private readonly float _distanceRange;
    private readonly Dictionary<TextRunCacheKey, CachedRun> _runCache = new();
    private readonly ShaderBinding _instanceBinding;
    private readonly uint _instanceStride;
    private readonly ShaderBinding _atlasBinding;
    private readonly uint _pushConstantSize;
    private readonly byte[] _pushConstantBytes;
    private PendingRun[] _pendingRuns = new PendingRun[8];
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
    private int _uploadPageCount;
    private int _pendingRunCount;
    private int _instanceCount;
    private int _batchCount;
    private int _localRunBatchCount;
    private int _uploadedInstanceByteCount;
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

        var shaderLayout = TextShaderPacking.Resolve(shaderProgram, mode);
        _instanceBinding = shaderLayout.InstanceBinding;
        _instanceStride = shaderLayout.InstanceStride;
        _atlasBinding = shaderLayout.AtlasBinding;
        _pushConstantSize = shaderLayout.PushConstantSize;
        _pushConstantBytes = new byte[checked((int)Maths.Max(_pushConstantSize, TextShaderPacking.MaxPushConstantSize))];

        _session = session;
        _mode = mode;
        _distanceRange = mode is GlyphImageMode.Sdf or GlyphImageMode.Msdf ? distanceRange : 0;
        _atlas = new TextAtlas(
            session,
            textService,
            mode,
            shaderLayout.Encoding,
            shaderLayout.AtlasFormat,
            shaderLayout.AtlasBytesPerPixel,
            atlasWidth,
            atlasHeight,
            padding,
            _distanceRange,
            colorOptions);
        _viewport = viewport;
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

            _atlas.Dispose();
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

    internal bool TryResolveTextVariant(TextShaderVariant variant, out RasterPipelineDescription pipeline)
    {
        pipeline = _pipeline;
        if (!variant.IsValid || variant.Mode != _mode)
        {
            return false;
        }

        TextShaderLayout layout;
        try
        {
            layout = TextShaderPacking.Resolve(variant.Program, variant.Mode, variant.Path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (layout.InstanceBinding != _instanceBinding ||
            layout.InstanceStride != _instanceStride ||
            layout.AtlasBinding != _atlasBinding ||
            variant.Path == TextShaderPath.Standard && layout.PushConstantSize != _pushConstantSize)
        {
            return false;
        }

        pipeline = new RasterPipelineDescription(
            variant.Program,
            topology: PrimitiveTopology.TriangleList,
            cullMode: RasterCullMode.None,
            blendMode: RenderBlendMode.Alpha);
        return true;
    }

    internal bool AreRunVariantsCompatible(int firstRun, int secondRun)
        => _pendingRuns.RefAt(firstRun).ShaderVariant == _pendingRuns.RefAt(secondRun).ShaderVariant &&
           _pendingRuns.RefAt(firstRun).EffectValues == _pendingRuns.RefAt(secondRun).EffectValues;

    internal bool TryGetCompositePipeline(
        int firstRun,
        int runCount,
        out RasterPipelineDescription pipeline)
    {
        pipeline = _pipeline;
        if (firstRun < 0 || runCount <= 0 || firstRun > _pendingRunCount - runCount)
        {
            return false;
        }

        var variant = _pendingRuns.RefAt(firstRun).ShaderVariant;
        for (var runIndex = firstRun + 1; runIndex < firstRun + runCount; runIndex++)
        {
            if (!AreRunVariantsCompatible(firstRun, runIndex))
            {
                return false;
            }
        }

        return !variant.HasValue || TryResolveTextVariant(variant.Value, out pipeline);
    }

    internal int QueueCompositeRun(
        ShapedText text,
        float originX,
        float originY,
        Vector4 color,
        PixelRect clip,
        bool mergeWithPrevious,
        uint producerRunId = 0,
        uint producerRunGeneration = 0,
        uint producerRunVersion = 0,
        TextShaderVariant? shaderVariant = null,
        TextEffectValues effectValues = default)
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

        if (!effectValues.IsValid ||
            (shaderVariant is null && effectValues != TextEffectValues.Empty) ||
            (shaderVariant is { Path: TextShaderPath.Standard } && effectValues != TextEffectValues.Empty))
        {
            throw new ArgumentException("Text effects require a valid generated stroke/outer-glow shader variant.", nameof(effectValues));
        }

        EnsureArrayCapacity(ref _pendingRuns, _pendingRunCount + 1);
        _pendingRuns.RefAt(_pendingRunCount) = new PendingRun(
            text,
            originX,
            originY,
            color,
            clip,
            mergeWithPrevious,
            shaderVariant,
            effectValues,
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
        => PrepareComposite(graph, registerUploadPass: true);

    internal bool PrepareComposite(IRenderGraphBuilder graph, bool registerUploadPass)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        var profiler = _session.Profiler;
        var started = profiler is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            BuildInstances();
        }
        finally
        {
            if (profiler is not null)
            {
                profiler.RecordLayoutAndShaping(ProfileDuration.FromStopwatchTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency));
            }
        }

        if (_instanceCount == 0)
        {
            return false;
        }

        var instances = graph.ImportBuffer(_instanceBuffer);
        _instanceGraphHandle = instances;
        EnsureArrayCapacity(ref _uploadPageIndices, _atlas.PageCount);
        _uploadPageCount = 0;
        for (var pageIndex = 0; pageIndex < _atlas.PageCount; pageIndex++)
        {
            _pageGraphHandles.RefAt(pageIndex) = graph.ImportTexture(_atlas.GetTexture(pageIndex));
            if (_atlas.IsDirty(pageIndex))
            {
                _uploadPageIndices.RefAt(_uploadPageCount++) = pageIndex;
            }
        }

        if (registerUploadPass && (_instancePayloadDirty || _uploadPageCount > 0))
        {
            var upload = graph.AddTransferPass("DeltaRender.Text.Upload", _uploadPass);
            ConfigureCompositeUploadPass(graph, upload);
        }

        return true;
    }

    internal bool CompositeUploadPending => _instancePayloadDirty || _uploadPageCount > 0;

    internal void ConfigureCompositeUploadPass(IRenderGraphBuilder graph, RenderGraphPassHandle pass)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (_instancePayloadDirty)
        {
            graph.UseBuffer(pass, _instanceGraphHandle, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        }

        for (var uploadIndex = 0; uploadIndex < _uploadPageCount; uploadIndex++)
        {
            graph.UseTexture(pass, _pageGraphHandles.RefAt(_uploadPageIndices.RefAt(uploadIndex)), RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        }
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
        var variant = _pendingRuns.RefAt(firstRun).ShaderVariant;
        var path = variant?.Path ?? TextShaderPath.Standard;
        var layout = variant.HasValue
            ? TextShaderPacking.Resolve(variant.Value.Program, variant.Value.Mode, path)
            : new TextShaderLayout(
                GlyphImageEncoding.SdfR8,
                RenderTextureFormat.R8Unorm,
                1,
                _instanceBinding,
                _instanceStride,
                _atlasBinding,
                _pushConstantSize);
        var pushConstantBytes = _pushConstantBytes.AsSpan(0, checked((int)layout.PushConstantSize));
        var packedPushConstantSize = TextShaderPacking.PackTextParameters(
            path,
            _mode,
            _viewport,
            _distanceRange,
            _pendingRuns.RefAt(firstRun).EffectValues,
            pushConstantBytes);
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

            firstBatch = firstBatch < 0 ? batchStart : Maths.Min(firstBatch, batchStart);
            lastBatch = Maths.Max(lastBatch, checked(batchStart + batchCount));
        }

        if (firstBatch < 0)
        {
            return;
        }

        var boundPage = -1;
        var previousClip = default(PixelRect);
        var hasScissor = false;
        for (var batchIndex = firstBatch; batchIndex < lastBatch; batchIndex++)
        {
            var batch = _batches.RefAt(batchIndex);
            if (batch.PageIndex != boundPage)
            {
                commands.BindTexture(
                    _atlasBinding,
                    _pageGraphHandles.RefAt(batch.PageIndex),
                    _sampler);
                boundPage = batch.PageIndex;
            }

            if (!hasScissor || batch.Clip != previousClip)
            {
                commands.SetScissor(batch.Clip);
                previousClip = batch.Clip;
                hasScissor = true;
            }

            commands.Draw(6, checked((uint)batch.Count), 0, checked((uint)batch.Start));
        }
    }

    internal void RecordCompositeUpload(ITransferCommandContext commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        try
        {
            for (var uploadIndex = 0; uploadIndex < _uploadPageCount; uploadIndex++)
            {
                var pageIndex = _uploadPageIndices.RefAt(uploadIndex);
                commands.UploadTexture(
                    _pageGraphHandles.RefAt(pageIndex),
                    new PixelRect(0, 0, checked((int)_atlas.Width), checked((int)_atlas.Height)),
                    _atlas.GetPixels(pageIndex),
                    checked(_atlas.Width * (uint)_atlas.BytesPerPixel));
            }

            if (_instancePayloadDirty)
            {
                var byteCount = checked((int)((ulong)_instanceCount * _instanceStride));
                var bytes = _instanceBytes.AsSpan(0, byteCount);
                commands.UploadBuffer(_instanceGraphHandle, bytes);
                bytes.CopyTo(_uploadedInstanceBytes);
                _uploadedInstanceByteCount = byteCount;
                _instancePayloadDirty = false;
                _instanceBufferNeedsUpload = false;
            }
        }
        catch
        {
            for (var uploadIndex = 0; uploadIndex < _uploadPageCount; uploadIndex++)
            {
                _atlas.MarkDirty(_uploadPageIndices.RefAt(uploadIndex));
            }

            throw;
        }

        for (var uploadIndex = 0; uploadIndex < _uploadPageCount; uploadIndex++)
        {
            _atlas.MarkUploaded(_uploadPageIndices.RefAt(uploadIndex));
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

        _atlas.Dispose();
    }

    private void BuildInstances()
    {
        _instanceCount = 0;
        _batchCount = 0;
        var useStamp = _atlas.NextUseStamp();
        EnsureRunBatchCapacity(_pendingRunCount);
        for (var runIndex = 0; runIndex < _pendingRunCount; runIndex++)
        {
            var pending = _pendingRuns.RefAt(runIndex);
            var penX = 0f;
            var penY = 0f;
            var runInstanceStart = _instanceCount;
            _localRunBatchCount = 0;
            var runReused = TryReuseRun(pending, out var cachedRun);
            if (runReused)
            {
                var reused = cachedRun!;
                EnsureInstanceCapacity(_instanceCount + reused.InstanceCount);
                var destinationOffset = checked((int)((ulong)runInstanceStart * _instanceStride));
                reused.PackedBytes.AsSpan(0, reused.PackedByteCount)
                    .CopyTo(_instanceBytes.AsSpan(destinationOffset, reused.PackedByteCount));
                var cachedFirstBatch = AppendCachedBatches(reused, runInstanceStart, pending.MergeWithPrevious, useStamp);
                _instanceCount += reused.InstanceCount;
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
                    var placement = _atlas.GetOrCreateGlyph(run, glyph, useStamp);
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
                var path = pending.ShaderVariant?.Path ?? TextShaderPath.Standard;
                var written = TextShaderPacking.PackInstances(
                    path,
                    _mode,
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
        _instancePayloadDirty = _instanceBufferNeedsUpload ||
            byteCount != _uploadedInstanceByteCount ||
            !_instanceBytes.AsSpan(0, byteCount).SequenceEqual(_uploadedInstanceBytes.AsSpan(0, byteCount));
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

        EnsureArrayCapacity(ref _batches, _batchCount + 1);
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

        EnsureArrayCapacity(ref _localRunBatches, _localRunBatchCount + 1);
        _localRunBatches.RefAt(_localRunBatchCount++) = new TextBatch(pageIndex, clip, instance, 1);
    }

    private int AppendCachedBatches(CachedRun cachedRun, int instanceStart, bool mergeWithPrevious, ulong useStamp)
    {
        var firstBatch = -1;
        for (var batchIndex = 0; batchIndex < cachedRun.BatchCount; batchIndex++)
        {
            var cachedBatch = cachedRun.Batches.RefAt(batchIndex);
            _atlas.TouchPage(cachedBatch.PageIndex, useStamp);
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
            cachedRun.ShaderVariant != pending.ShaderVariant ||
            cachedRun.EffectValues != pending.EffectValues ||
            cachedRun.AtlasEpoch != _atlas.Epoch)
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
        cachedRun.ShaderVariant = pending.ShaderVariant;
        cachedRun.EffectValues = pending.EffectValues;
        cachedRun.AtlasEpoch = _atlas.Epoch;
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

    private void EnsureRunBatchCapacity(int required)
    {
        if (required <= _runBatchStarts.Length)
        {
            return;
        }

        var capacity = GrowCapacity(_runBatchStarts.Length, required);
        ResizeArray(ref _runBatchStarts, capacity);
        ResizeArray(ref _runBatchCounts, capacity);
    }

    private void ClearState()
    {
        Array.Clear(_pendingRuns, 0, _pendingRunCount);
        _pendingRunCount = 0;
        _instanceCount = 0;
        _batchCount = 0;
        Array.Clear(_runBatchStarts, 0, _runBatchStarts.Length);
        Array.Clear(_runBatchCounts, 0, _runBatchCounts.Length);
        _runCache.Clear();
    }

    private static int GrowCapacity(int current, int required)
    {
        var capacity = Maths.Max(1, current);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    private static void EnsureArrayCapacity<T>(ref T[] storage, int required)
    {
        if (required > storage.Length)
        {
            ResizeArray(ref storage, GrowCapacity(storage.Length, required));
        }
    }

    private static void ResizeArray<T>(ref T[] storage, int capacity)
        => Array.Resize(ref storage, capacity);

    private static bool TryClip(PixelRect requested, PixelExtent viewport, out PixelRect clip)
    {
        var left = requested.X >= 0L ? requested.X : 0L;
        var top = requested.Y >= 0L ? requested.Y : 0L;
        var requestedRight = (long)requested.X + requested.Width;
        var requestedBottom = (long)requested.Y + requested.Height;
        var right = requestedRight <= (long)viewport.Width ? requestedRight : (long)viewport.Width;
        var bottom = requestedBottom <= (long)viewport.Height ? requestedBottom : (long)viewport.Height;
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TextUploadPass(TextRenderFeature owner) : ITransferPass
    {
        public void Record(ITransferCommandContext commands)
            => owner.RecordCompositeUpload(commands);
    }

    private sealed class TextDrawPass(TextRenderFeature owner) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
            => owner.RecordCompositeRuns(commands, 0, owner._pendingRunCount);
    }

    private RenderGraphBufferHandle _instanceGraphHandle;
}
