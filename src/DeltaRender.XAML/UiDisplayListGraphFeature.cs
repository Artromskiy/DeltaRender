using System.Diagnostics;
using System.Numerics;
using Delta;
using Delta.Diagnostics;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Shader.Contract;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;


/// <summary>
/// Synchronously consumes one borrowed DeltaXAML display list and exposes it as
/// renderer-owned graph work. The input spans are copied during <see cref="Consume"/>
/// and are never retained. The borrowed view is valid only until the next consume or
/// disposal; the feature itself owns the reusable backing storage.
/// </summary>
public sealed class UiDisplayListGraphFeature : IRenderFeature, IDisposable
{
    private const int VisualUploadMergeGapBytes = 64;
    private readonly IRenderFrameSession? _session;
    private readonly IGraphicsShaderProgram? _defaultVisualProgram;
    private readonly IGraphicsShaderProgram? _solidVisualProgram;
    private readonly IGraphicsShaderProgram? _roundedSliceVisualProgram;
    private readonly IGraphicsShaderProgram? _linearGradientVisualProgram;
    private readonly IGraphicsShaderProgram? _imageVisualProgram;
    private readonly UiDisplayListResourceRegistry _registry;
    private readonly TextRenderFeature? _textFeature;
    private readonly UiTextVisualUploadPass _textVisualUploadPass;
    private readonly UiClipResolver _clipResolver;
    private readonly PixelExtent _viewport;
    private readonly List<string> _diagnostics = [];
    private readonly List<string> _warnings = [];
    private UiCoordinateMapper _coordinates;
    private float _dpiScale = 1f;

    private UiVisualDraw[] _visuals = [];
    private UiClipRegion[] _clips = [];
    private UiTextDraw[] _texts = [];
    private UiDrawRef[] _order = [];
    private UiElementIdentity[] _identities = [];
    private UiVisualSegmentPass?[] _visualSegmentPasses = [];
    private int _visualSegmentPassCount;
    private UiTextPass?[] _textPasses = [];
    private int _textPassCount;
    private PixelRect[] _commandClips = [];
    private byte[] _visualInstanceBytes = [];
    private byte[] _uploadedVisualInstanceBytes = [];
    private byte[] _visualFramePushConstants = [];
    private IGraphicsShaderProgram?[] _visualPrograms = [];
    private uint[] _visualPushConstantSizes = [];
    private uint[] _visualInstanceStrides = [];
    private ShaderBinding[] _visualInstanceBindings = [];
    private UiRectangleShaderKind[] _visualShaderKinds = [];
    private UiVisualShaderPath[] _visualShaderPaths = [];
    private UiEffectResource[] _visualEffectResources = [];
    private IGraphicsShaderProgram?[] _visualShadowPrograms = [];
    private uint[] _visualShadowPushConstantSizes = [];
    private uint[] _visualShadowInstanceStrides = [];
    private ShaderBinding[] _visualShadowInstanceBindings = [];
    private UiRectangleShaderKind[] _visualShadowShaderKinds = [];
    private UiVisualShaderPath[] _visualShadowShaderPaths = [];
    private UiEffectResource[] _visualShadowEffectResources = [];
    private int[] _visualShadowInstanceCounts = [];
    private int[] _visualShadowInstanceOffsets = [];
    private uint[] _visualShadowFramePushConstantOffsets = [];
    private bool[] _visualHasShadow = [];
    private IGraphicsShaderProgram?[] _visualGlowPrograms = [];
    private uint[] _visualGlowPushConstantSizes = [];
    private uint[] _visualGlowInstanceStrides = [];
    private ShaderBinding[] _visualGlowInstanceBindings = [];
    private UiRectangleShaderKind[] _visualGlowShaderKinds = [];
    private UiVisualShaderPath[] _visualGlowShaderPaths = [];
    private UiEffectResource[] _visualGlowEffectResources = [];
    private int[] _visualGlowInstanceCounts = [];
    private int[] _visualGlowInstanceOffsets = [];
    private uint[] _visualGlowFramePushConstantOffsets = [];
    private bool[] _visualHasGlow = [];
    private int[] _visualInstanceCounts = [];
    private uint[] _visualFramePushConstantOffsets = [];
    private RenderGraphTextureHandle?[] _visualImageTextures = [];
    private RenderSamplerHandle[] _visualImageSamplers = [];
    private ShaderBinding?[] _visualImageBindings = [];
    private UiLinearGradientResource?[] _visualGradientResources = [];
    private RenderGraphTextureHandle?[] _visualMaskTextures = [];
    private RenderTextureHandle[] _visualMaskResources = [];
    private RenderSamplerHandle[] _visualMaskSamplers = [];
    private float4[] _visualMaskUvRects = [];
    private int[] _visualSeenEpochs = [];
    private int[] _textSeenEpochs = [];
    private int[] _textRunIndices = [];
    private int _visualCount;
    private int _clipCount;
    private int _textCount;
    private int _orderCount;
    private int[] _visualInstanceOffsets = [];
    private bool[] _visualPayloadDirtyByIndex = [];
    private ulong[] _visualEffectRevisionsByIndex = [];
    private BufferRange[] _visualUploadRanges = [];
    private int _visualUploadRangeCount;
    private int _visualInstanceByteCount;
    private int _uploadedVisualInstanceByteCount;
    private ulong _visualInstanceBufferCapacity;
    private ulong _visualInstanceAlignment = 1;
    private RenderBufferHandle _visualInstanceBuffer;
    private RenderGraphBufferHandle _visualInstanceGraphHandle;
    private bool _visualInstancePayloadDirty = true;
    private bool _visualInstancesPrepared;
    private bool _flatVisualInstanceBuffer;
    private bool _hasPackedVisualFrame;
    private UiRectangleShaderKind _packedVisualFrameKind;
    private uint _packedVisualFrameSize;
    private uint _packedVisualFrameOffset;
    private IGraphicsShaderProgram? _preparedVisualProgram;
    private UiVisualKind _preparedVisualKind;
    private UiVisualShaderPath _preparedVisualShaderPath;
    private UiRectangleShaderKind _preparedVisualShaderKind;
    private ShaderBinding _preparedVisualInstanceBinding;
    private uint _preparedVisualInstanceStride;
    private uint _preparedVisualPushConstantSize;
    private uint _preparedVisualPushConstantOffset;
    private bool _hasPreparedVisualDescription;
    private bool _visualCommandsPrepared;
    private bool _reuseVisualInstanceLayout;
    private int _validationEpoch;
    private bool _hasFrame;
    private bool _disposed;

    /// <summary>Creates a headless planning adapter without a graph submission owner.</summary>
    public UiDisplayListGraphFeature(PixelExtent viewport)
        : this(null, null, viewport, null, null, null, null, null, null, false)
    {
    }

    /// <summary>
    /// Creates an adapter for a session-owned target. The optional text feature remains
    /// owned by its caller and is used only for already shaped text submission.
    /// </summary>
    public UiDisplayListGraphFeature(
        IRenderFrameSession session,
        IGraphicsShaderProgram visualProgram,
        PixelExtent viewport,
        UiDisplayListResourceRegistry? registry = null,
        TextRenderFeature? textFeature = null,
        IGraphicsShaderProgram? solidVisualProgram = null,
        IGraphicsShaderProgram? roundedSliceVisualProgram = null,
        IGraphicsShaderProgram? linearGradientVisualProgram = null,
        IGraphicsShaderProgram? imageVisualProgram = null)
        : this(session, visualProgram, viewport, registry, textFeature, solidVisualProgram, roundedSliceVisualProgram, linearGradientVisualProgram, imageVisualProgram, true)
    {
    }

    private UiDisplayListGraphFeature(
        IRenderFrameSession? session,
        IGraphicsShaderProgram? visualProgram,
        PixelExtent viewport,
        UiDisplayListResourceRegistry? registry,
        TextRenderFeature? textFeature,
        IGraphicsShaderProgram? solidVisualProgram,
        IGraphicsShaderProgram? roundedSliceVisualProgram,
        IGraphicsShaderProgram? linearGradientVisualProgram,
        IGraphicsShaderProgram? imageVisualProgram,
        bool validate)
    {
        if (validate && viewport.IsEmpty)
        {
            throw new ArgumentException("The UI viewport must be non-empty.", nameof(viewport));
        }

        if (validate)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(visualProgram);
        }

        _session = session;
        _defaultVisualProgram = visualProgram;
        _solidVisualProgram = solidVisualProgram;
        _roundedSliceVisualProgram = roundedSliceVisualProgram;
        _linearGradientVisualProgram = linearGradientVisualProgram;
        _imageVisualProgram = imageVisualProgram;
        _viewport = viewport;
        _registry = registry ?? new UiDisplayListResourceRegistry();
        _textFeature = textFeature;
        _textVisualUploadPass = new UiTextVisualUploadPass(this);
        _clipResolver = new UiClipResolver(viewport, _diagnostics, _warnings);
        _coordinates = new UiCoordinateMapper(1f, viewport);
        if (session is not null)
        {
            var alignment = session.Capabilities.MinStorageBufferOffsetAlignment;
            _visualInstanceAlignment = alignment >= 1UL ? alignment : 1UL;
        }
    }

    /// <summary>Gets the reusable diagnostic list from the latest consume or submit attempt.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Gets non-fatal boundary warnings from the latest consume or submit attempt.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Gets whether the latest display list was accepted for planning.</summary>
    public bool HasFrame => _hasFrame;

    /// <summary>Gets the number of copied visual payloads.</summary>
    public int VisualCount => _visualCount;

    /// <summary>Gets the number of copied text payloads.</summary>
    public int TextCount => _textCount;

    /// <summary>Gets the copied canonical draw order until the next consume or disposal.</summary>
    public ReadOnlySpan<UiDrawRef> BorrowOrder()
    {
        ThrowIfDisposed();
        return _order.AsSpan(0, _orderCount);
    }

    /// <summary>Gets the copied visual payloads until the next consume or disposal.</summary>
    internal ReadOnlySpan<UiVisualDraw> BorrowVisuals()
    {
        ThrowIfDisposed();
        return _visuals.AsSpan(0, _visualCount);
    }

    /// <summary>Gets the copied clip payloads until the next consume or disposal.</summary>
    internal ReadOnlySpan<UiClipRegion> BorrowClips()
    {
        ThrowIfDisposed();
        return _clips.AsSpan(0, _clipCount);
    }

    /// <summary>Gets the copied shaped-text payloads until the next consume or disposal.</summary>
    internal ReadOnlySpan<UiTextDraw> BorrowTexts()
    {
        ThrowIfDisposed();
        return _texts.AsSpan(0, _textCount);
    }

    /// <summary>Gets the effective viewport-intersected clip for one order entry.</summary>
    public PixelRect GetEffectiveClip(int orderIndex)
    {
        ThrowIfDisposed();
        if ((uint)orderIndex >= (uint)_orderCount)
        {
            throw new ArgumentOutOfRangeException(nameof(orderIndex));
        }

        return _commandClips.RefAt(orderIndex);
    }

    /// <summary>
    /// Copies and validates one borrowed display list synchronously. Rectangular parent
    /// chains are resolved to pixel scissors. Rounded/stroke/gradient forms are rejected
    /// with diagnostics until their shader artifacts are registered for this adapter.
    /// </summary>
    public bool Consume(UiDisplayList displayList)
    {
        ThrowIfDisposed();
        var profiler = _session?.Profiler;
        var started = profiler is null ? 0 : Stopwatch.GetTimestamp();
        _diagnostics.Clear();
        _warnings.Clear();
        _textFeature?.Clear();
        if (!float.IsFinite(displayList.DpiScale) || displayList.DpiScale <= 0)
        {
            _hasFrame = false;
            AddDiagnostic("The display-list DPI scale must be finite and positive.");
            return false;
        }

        var dpiScaleChanged = _dpiScale != displayList.DpiScale;
        _dpiScale = displayList.DpiScale;
        _coordinates = new UiCoordinateMapper(_dpiScale, _viewport);
        var reuseVisualInstanceLayout = !dpiScaleChanged && CanReuseVisualInstanceLayout(displayList);
        ClearFrameStorage(reuseVisualInstanceLayout);

        EnsureCapacity(ref _visuals, displayList.Visuals.Length);
        EnsureCapacity(ref _clips, displayList.Clips.Length);
        EnsureCapacity(ref _texts, displayList.Text.Length);
        EnsureCapacity(ref _order, displayList.Order.Length);
        EnsureCapacity(ref _identities, displayList.Identities.Length);
        EnsureCapacity(ref _commandClips, displayList.Order.Length);
        var validationEpoch = NextValidationEpoch();
        EnsureCapacity(ref _visualSeenEpochs, displayList.Visuals.Length);
        EnsureCapacity(ref _textSeenEpochs, displayList.Text.Length);
        EnsureCapacity(ref _textRunIndices, displayList.Order.Length);
        EnsureCapacity(ref _visualPrograms, displayList.Order.Length);
        EnsureCapacity(ref _visualPushConstantSizes, displayList.Order.Length);
        EnsureCapacity(ref _visualInstanceStrides, displayList.Order.Length);
        EnsureCapacity(ref _visualInstanceBindings, displayList.Order.Length);
        EnsureCapacity(ref _visualShaderKinds, displayList.Order.Length);
        EnsureCapacity(ref _visualShaderPaths, displayList.Order.Length);
        EnsureCapacity(ref _visualEffectResources, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowPrograms, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowPushConstantSizes, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowInstanceStrides, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowInstanceBindings, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowShaderKinds, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowShaderPaths, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowEffectResources, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowInstanceCounts, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowInstanceOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualShadowFramePushConstantOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualHasShadow, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowPrograms, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowPushConstantSizes, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowInstanceStrides, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowInstanceBindings, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowShaderKinds, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowShaderPaths, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowEffectResources, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowInstanceCounts, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowInstanceOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualGlowFramePushConstantOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualHasGlow, displayList.Order.Length);
        EnsureCapacity(ref _visualInstanceCounts, displayList.Order.Length);
        EnsureCapacity(ref _visualInstanceOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualFramePushConstantOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualImageTextures, displayList.Order.Length);
        EnsureCapacity(ref _visualImageSamplers, displayList.Order.Length);
        EnsureCapacity(ref _visualImageBindings, displayList.Order.Length);
        EnsureCapacity(ref _visualGradientResources, displayList.Order.Length);
        EnsureCapacity(ref _visualMaskTextures, displayList.Order.Length);
        EnsureCapacity(ref _visualMaskResources, displayList.Order.Length);
        EnsureCapacity(ref _visualMaskSamplers, displayList.Order.Length);
        EnsureCapacity(ref _visualMaskUvRects, displayList.Order.Length);
        EnsureCapacity(ref _visualPayloadDirtyByIndex, displayList.Visuals.Length);
        EnsureCapacity(ref _visualEffectRevisionsByIndex, displayList.Visuals.Length);

        displayList.Visuals.CopyTo(_visuals);
        displayList.Clips.CopyTo(_clips);
        displayList.Text.CopyTo(_texts);
        displayList.Order.CopyTo(_order);
        displayList.Identities.CopyTo(_identities);
        _visualCount = displayList.Visuals.Length;
        _clipCount = displayList.Clips.Length;
        _textCount = displayList.Text.Length;
        _orderCount = displayList.Order.Length;
        _clipResolver.SetFrame(_clips, _clipCount, _coordinates.LogicalViewport);

        for (var index = 0; index < _clipCount; index++)
        {
            if (!_clipResolver.TryResolve(new UiClipId(index), out _))
            {
                ClearFrameStorage();
                profiler?.RecordLayoutAndShaping(ProfileDuration.FromStopwatchTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency));
                return false;
            }
        }

        _visualSegmentPassCount = 0;
        _textPassCount = 0;
        for (var index = 0; index < _orderCount; index++)
        {
            var draw = _order.RefAt(index);
            if (!draw.IsValid)
            {
                AddDiagnostic($"Order[{index}] is not a valid visual or text reference.");
                continue;
            }

            if (draw.Kind == UiDrawKind.Visual)
            {
                if ((uint)draw.Index >= (uint)_visualCount)
                {
                    AddDiagnostic($"Order[{index}] references missing visual {draw.Index}.");
                    continue;
                }

                if (_visualSeenEpochs.RefAt(draw.Index) == validationEpoch)
                {
                    AddDiagnostic($"Order[{index}] references visual {draw.Index} more than once.");
                    continue;
                }

                _visualSeenEpochs.RefAt(draw.Index) = validationEpoch;
                if (!ValidateVisual(_visuals.RefAt(draw.Index), index, out var clip))
                {
                    continue;
                }

                _commandClips.RefAt(index) = clip;
            }
            else
            {
                if ((uint)draw.Index >= (uint)_textCount)
                {
                    AddDiagnostic($"Order[{index}] references missing text {draw.Index}.");
                    continue;
                }

                if (_textSeenEpochs.RefAt(draw.Index) == validationEpoch)
                {
                    AddDiagnostic($"Order[{index}] references text {draw.Index} more than once.");
                    continue;
                }

                _textSeenEpochs.RefAt(draw.Index) = validationEpoch;
                if (!ValidateText(_texts.RefAt(draw.Index), index, out var clip))
                {
                    continue;
                }

                _commandClips.RefAt(index) = clip;
            }
        }

        for (var index = 0; index < _visualCount; index++)
        {
            if (_visualSeenEpochs.RefAt(index) != validationEpoch)
            {
                AddDiagnostic($"Visual {index} is not present in the canonical Order span.");
            }
        }

        for (var index = 0; index < _textCount; index++)
        {
            if (_textSeenEpochs.RefAt(index) != validationEpoch)
            {
                AddDiagnostic($"Text {index} is not present in the canonical Order span.");
            }
        }

        _hasFrame = _diagnostics.Count == 0;
        if (!_hasFrame)
        {
            ClearFrameStorage();
        }

        profiler?.RecordLayoutAndShaping(ProfileDuration.FromStopwatchTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency));
        return _hasFrame;
    }

    /// <inheritdoc />
    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!_hasFrame || _orderCount == 0)
        {
            return;
        }

        if (_session is null || _defaultVisualProgram is null)
        {
            AddDiagnostic("A graph submission session and visual shader program are required for AddPasses.");
            return;
        }

        EnsureCapacity(ref _visualFramePushConstants, UiVisualShaderContract.MaxPushConstantSize);
        var target = graph.ImportTarget(_session.Target);
        var textPrepared = false;
        if (_textCount != 0)
        {
            if (_textFeature is null)
            {
                AddDiagnostic("Text payloads require a caller-owned DeltaRender.Text adapter.");
            }
            else
            {
                Array.Fill(_textRunIndices, -1, 0, _orderCount);
                var textRunCount = 0;
                var previousTextCanMerge = false;
                for (var index = 0; index < _orderCount; index++)
                {
                    var draw = _order.RefAt(index);
                    if (draw.Kind != UiDrawKind.Text || _commandClips.RefAt(index).IsEmpty)
                    {
                        previousTextCanMerge = false;
                        continue;
                    }

                    var text = _texts.RefAt(draw.Index);
                    TextShaderVariant? baseShaderVariant = null;
                    TextShaderVariant? shadowShaderVariant = null;
                    TextShaderVariant? glowShaderVariant = null;
                    var effectValues = TextEffectValues.Empty;
                    if (text.Paint.EffectSet.IsValid)
                    {
                        if (!_registry.TryResolveTextEffectPlan(
                                text.Paint.EffectSet,
                                out baseShaderVariant,
                                out shadowShaderVariant,
                                out glowShaderVariant,
                                out var effectResource) ||
                            baseShaderVariant.HasValue && !_textFeature.TryResolveTextVariant(baseShaderVariant.Value, out _) ||
                            shadowShaderVariant.HasValue && !_textFeature.TryResolveTextVariant(shadowShaderVariant.Value, out _) ||
                            glowShaderVariant.HasValue && !_textFeature.TryResolveTextVariant(glowShaderVariant.Value, out _))
                        {
                            AddDiagnostic($"Text at Order[{index}] effect-set is no longer registered or compatible with the text packer.");
                            previousTextCanMerge = false;
                            continue;
                        }

                        effectValues = ToTextEffectValues(effectResource, _dpiScale);
                    }

                    var color = text.Paint.FillColor;
                    var identity = _identities.RefAt(index);
                    var origin = _coordinates.ToPhysical(text.BaselineOrigin);
                    _textRunIndices.RefAt(index) = _textFeature.QueueCompositeRun(
                        text.Text,
                        origin.x,
                        origin.y,
                        new Vector4(color.x, color.y, color.z, color.w),
                        _coordinates.ToPhysical(_commandClips.RefAt(index)),
                        mergeWithPrevious: previousTextCanMerge && !shadowShaderVariant.HasValue && !glowShaderVariant.HasValue,
                        baseShaderVariant: baseShaderVariant,
                        effectValues: effectValues,
                        shadowShaderVariant: shadowShaderVariant,
                        glowShaderVariant: glowShaderVariant,
                        producerRunId: identity.Value,
                        producerRunGeneration: identity.Generation,
                        producerRunVersion: identity.Version);

                    textRunCount++;
                    previousTextCanMerge = !shadowShaderVariant.HasValue && !glowShaderVariant.HasValue;
                }

                textPrepared = textRunCount != 0 && _textFeature.PrepareComposite(graph, registerUploadPass: false);
            }
        }

        for (var index = 0; index < _orderCount; index++)
        {
            var draw = _order.RefAt(index);
            if (draw.Kind != UiDrawKind.Visual || _commandClips.RefAt(index).IsEmpty)
            {
                continue;
            }

            if (_visualCommandsPrepared && _visuals.RefAt(draw.Index).Kind != UiVisualKind.Image)
            {
                continue;
            }

            TryPrepareVisual(graph, index);
        }

        _visualCommandsPrepared = true;

        if (_visualCount != 0 && !PrepareVisualInstances())
        {
            return;
        }

        var visualUploadPending = false;
        if (_visualInstanceByteCount > 0)
        {
            EnsureVisualInstanceBuffer(checked((ulong)_visualInstanceByteCount));
            _visualInstanceGraphHandle = graph.ImportBuffer(_visualInstanceBuffer);
            if (_visualInstancePayloadDirty && _visualUploadRangeCount == 0)
            {
                SetFullVisualUploadRange();
            }

            visualUploadPending = _visualInstancePayloadDirty;
        }

        var textUploadPending = textPrepared && _textFeature?.CompositeUploadPending == true;
        if (visualUploadPending || textUploadPending)
        {
            var upload = graph.AddTransferPass("DeltaRender.XAML.TextVisualUpload", _textVisualUploadPass);
            if (visualUploadPending)
            {
                graph.UseBuffer(upload, _visualInstanceGraphHandle, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }

            if (textUploadPending)
            {
                _textFeature!.ConfigureCompositeUploadPass(graph, upload);
            }
        }

        for (var index = 0; index < _orderCount; index++)
        {
            var draw = _order.RefAt(index);
            if (draw.Kind == UiDrawKind.Text)
            {
                if (!textPrepared || _textFeature is null ||
                    _commandClips.RefAt(index).IsEmpty || _textRunIndices.RefAt(index) < 0)
                {
                    continue;
                }

                var firstRun = _textRunIndices.RefAt(index);
                var runCount = 1;
                var end = index + 1;
                var lastRun = firstRun;
                var hasShadowLayer = _textFeature.HasShadowLayer(firstRun);
                var hasGlowLayer = _textFeature.HasGlowLayer(firstRun);
                while (!hasShadowLayer && !hasGlowLayer && end < _orderCount &&
                       _order.RefAt(end).Kind == UiDrawKind.Text &&
                       !_commandClips.RefAt(end).IsEmpty &&
                       _textRunIndices.RefAt(end) == lastRun + 1 &&
                       _textFeature.AreRunVariantsCompatible(lastRun, _textRunIndices.RefAt(end)))
                {
                    lastRun++;
                    runCount++;
                    end++;
                }

                if (hasShadowLayer && !TryAddTextPass(graph, target, firstRun, runCount, TextRenderLayer.Shadow) ||
                    hasGlowLayer && !TryAddTextPass(graph, target, firstRun, runCount, TextRenderLayer.Glow) ||
                    !TryAddTextPass(graph, target, firstRun, runCount, TextRenderLayer.Base))
                {
                    AddDiagnostic($"Text at Order[{index}] has no compatible prepared shader variant.");
                    index = end - 1;
                    continue;
                }

                index = end - 1;
                continue;
            }

            var clip = _commandClips.RefAt(index);
            if (clip.IsEmpty)
            {
                continue;
            }

            if (_visualPrograms.RefAt(index) is not { } program)
            {
                continue;
            }

            if (_visualHasShadow.RefAt(index))
            {
                var shadowProgram = _visualShadowPrograms.RefAt(index);
                if (shadowProgram is null || !AddVisualPass(
                        graph,
                        target,
                        index,
                        1,
                        checked((ulong)_visualShadowInstanceCounts.RefAt(index)),
                        shadowProgram,
                        UiVisualRenderLayer.Shadow))
                {
                    AddDiagnostic($"Visual at Order[{index}] has no compatible prepared outer-shadow layer.");
                    continue;
                }
            }

            if (_visualHasGlow.RefAt(index))
            {
                var glowProgram = _visualGlowPrograms.RefAt(index);
                if (glowProgram is null || !AddVisualPass(
                        graph,
                        target,
                        index,
                        1,
                        checked((ulong)_visualGlowInstanceCounts.RefAt(index)),
                        glowProgram,
                        UiVisualRenderLayer.Glow))
                {
                    AddDiagnostic($"Visual at Order[{index}] has no compatible prepared outer-glow layer.");
                    continue;
                }
            }

            var visualEnd = index + 1;
            var segmentInstanceCount = (ulong)_visualInstanceCounts.RefAt(index);
            while (visualEnd < _orderCount &&
                   _order.RefAt(visualEnd).Kind == UiDrawKind.Visual &&
                   !_commandClips.RefAt(visualEnd).IsEmpty &&
                   !_visualHasShadow.RefAt(visualEnd) &&
                   !_visualHasGlow.RefAt(visualEnd) &&
                   CanJoinVisualSegment(index, visualEnd))
            {
                segmentInstanceCount = checked(segmentInstanceCount + (ulong)_visualInstanceCounts.RefAt(visualEnd));
                visualEnd++;
            }

            AddVisualPass(graph, target, index, visualEnd - index, segmentInstanceCount, program, UiVisualRenderLayer.Base);

            index = visualEnd - 1;
        }
    }

    private bool AddVisualPass(
        IRenderGraphBuilder graph,
        RenderGraphTextureHandle target,
        int firstOrderIndex,
        int visualCount,
        ulong instanceCount,
        IGraphicsShaderProgram program,
        UiVisualRenderLayer layer)
    {
        var visualPass = GetVisualSegmentPass(firstOrderIndex, visualCount, instanceCount, program, layer);
        var pass = graph.AddRasterPass(visualPass.Description, visualPass);
        graph.UseColorAttachment(
            pass,
            0,
            new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
        graph.UseBuffer(pass, _visualInstanceGraphHandle, RenderResourceAccess.Read, RenderPipelineStages.Vertex);

        if (layer == UiVisualRenderLayer.Base)
        {
            for (var visualIndex = firstOrderIndex; visualIndex < firstOrderIndex + visualCount; visualIndex++)
            {
                if (_visualImageTextures.RefAt(visualIndex).HasValue)
                {
                    var graphTexture = _visualImageTextures.RefAt(visualIndex) ?? throw new InvalidOperationException("The image graph resource was not imported.");
                    graph.UseTexture(pass, graphTexture, RenderResourceAccess.Read, RenderPipelineStages.Fragment);
                }

                if (_visualMaskTextures.RefAt(visualIndex).HasValue)
                {
                    var graphTexture = _visualMaskTextures.RefAt(visualIndex) ?? throw new InvalidOperationException("The mask graph resource was not imported.");
                    graph.UseTexture(pass, graphTexture, RenderResourceAccess.Read, RenderPipelineStages.Fragment);
                }
            }
        }

        return true;
    }

    private UiVisualSegmentPass GetVisualSegmentPass(
        int firstOrderIndex,
        int visualCount,
        ulong instanceCount,
        IGraphicsShaderProgram program,
        UiVisualRenderLayer layer)
    {
        if (_visualSegmentPassCount == _visualSegmentPasses.Length)
        {
            var newLength = _visualSegmentPassCount == 0 ? 4 : _visualSegmentPassCount * 2;
            Array.Resize(ref _visualSegmentPasses, newLength);
        }

        var pass = _visualSegmentPasses[_visualSegmentPassCount];
        if (pass is null)
        {
            pass = new UiVisualSegmentPass(this);
            _visualSegmentPasses[_visualSegmentPassCount] = pass;
        }

        pass.SetRange(firstOrderIndex, visualCount, instanceCount, program, layer);
        _visualSegmentPassCount++;
        return pass;
    }

    private bool TryAddTextPass(
        IRenderGraphBuilder graph,
        RenderGraphTextureHandle target,
        int firstRun,
        int runCount,
        TextRenderLayer layer)
    {
        var textFeaturePass = GetTextPass(firstRun, runCount, layer);
        if (textFeaturePass is null)
        {
            return false;
        }

        var textPass = graph.AddRasterPass(textFeaturePass.Description, textFeaturePass);
        graph.UseColorAttachment(
            textPass,
            0,
            new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
        _textFeature!.ConfigureCompositePass(graph, textPass);
        return true;
    }

    private UiTextPass? GetTextPass(int firstRun, int runCount, TextRenderLayer layer)
    {
        if (_textPassCount == _textPasses.Length)
        {
            var newLength = _textPassCount == 0 ? 4 : _textPassCount * 2;
            Array.Resize(ref _textPasses, newLength);
        }

        var pass = _textPasses[_textPassCount];
        if (pass is null)
        {
            pass = new UiTextPass(_textFeature ?? throw new InvalidOperationException("The text feature is not configured."));
            _textPasses[_textPassCount] = pass;
        }

        if (!pass.SetRange(firstRun, runCount, layer))
        {
            return null;
        }

        _textPassCount++;
        return pass;
    }

    private bool TryPrepareVisual(IRenderGraphBuilder graph, int orderIndex)
    {
        var draw = _order.RefAt(orderIndex);
        var visual = _visuals.RefAt(draw.Index);
        ResolveVisualPlan(visual, out var baseVariant, out var shadowVariant, out var glowVariant, out var effectResource);
        var program = baseVariant.Program;
        var shaderVisualKind = baseVariant.Kind;
        var shaderPath = baseVariant.Path;
        UiRectangleShaderKind shaderKind;
        ShaderBinding instanceBinding;
        uint instanceStride;
        uint pushConstantSize;
        uint pushConstantOffset;
        if (_hasPreparedVisualDescription &&
            ReferenceEquals(_preparedVisualProgram, program) &&
            _preparedVisualKind == shaderVisualKind &&
            _preparedVisualShaderPath == shaderPath)
        {
            shaderKind = _preparedVisualShaderKind;
            instanceBinding = _preparedVisualInstanceBinding;
            instanceStride = _preparedVisualInstanceStride;
            pushConstantSize = _preparedVisualPushConstantSize;
            pushConstantOffset = _preparedVisualPushConstantOffset;
        }
        else if (!UiVisualShaderContract.TryDescribeInstance(
                     program,
                     shaderVisualKind,
                     shaderPath,
                     out shaderKind,
                     out instanceBinding,
                     out instanceStride,
                     out pushConstantSize,
                     out pushConstantOffset,
                     out var shaderDiagnostic))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] is unsupported: {shaderDiagnostic}");
            return false;
        }
        else
        {
            _preparedVisualProgram = program;
            _preparedVisualKind = shaderVisualKind;
            _preparedVisualShaderPath = shaderPath;
            _preparedVisualShaderKind = shaderKind;
            _preparedVisualInstanceBinding = instanceBinding;
            _preparedVisualInstanceStride = instanceStride;
            _preparedVisualPushConstantSize = pushConstantSize;
            _preparedVisualPushConstantOffset = pushConstantOffset;
            _hasPreparedVisualDescription = true;
        }

        if (!_hasPackedVisualFrame ||
            _packedVisualFrameKind != shaderKind ||
            _packedVisualFrameSize != pushConstantSize ||
            _packedVisualFrameOffset != pushConstantOffset)
        {
            var packedFrameSize = UiVisualShaderContract.PackFrame(
                shaderKind,
                _viewport,
                _visualFramePushConstants.AsSpan(0, checked((int)pushConstantSize)));
            if (packedFrameSize != pushConstantSize)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] generated UI frame packer wrote {packedFrameSize} bytes; expected {pushConstantSize}.");
                return false;
            }

            _hasPackedVisualFrame = true;
            _packedVisualFrameKind = shaderKind;
            _packedVisualFrameSize = pushConstantSize;
            _packedVisualFrameOffset = pushConstantOffset;
        }

        _visualPrograms.RefAt(orderIndex) = program;
        _visualPushConstantSizes.RefAt(orderIndex) = pushConstantSize;
        _visualInstanceStrides.RefAt(orderIndex) = instanceStride;
        _visualInstanceBindings.RefAt(orderIndex) = instanceBinding;
        _visualShaderKinds.RefAt(orderIndex) = shaderKind;
        _visualShaderPaths.RefAt(orderIndex) = shaderPath;
        _visualEffectResources.RefAt(orderIndex) = effectResource;
        _visualInstanceCounts.RefAt(orderIndex) = UiVisualShaderContract.MaxInstanceCount(shaderKind);
        _visualFramePushConstantOffsets.RefAt(orderIndex) = pushConstantOffset;
        _visualImageTextures.RefAt(orderIndex) = null;
        _visualImageSamplers.RefAt(orderIndex) = default;
        _visualImageBindings.RefAt(orderIndex) = null;
        _visualGradientResources.RefAt(orderIndex) = null;
        _visualMaskTextures.RefAt(orderIndex) = null;
        _visualMaskResources.RefAt(orderIndex) = default;
        _visualMaskSamplers.RefAt(orderIndex) = default;
        _visualMaskUvRects.RefAt(orderIndex) = default;
        _visualShadowPrograms.RefAt(orderIndex) = null;
        _visualShadowPushConstantSizes.RefAt(orderIndex) = 0;
        _visualShadowInstanceStrides.RefAt(orderIndex) = 0;
        _visualShadowInstanceBindings.RefAt(orderIndex) = default;
        _visualShadowShaderKinds.RefAt(orderIndex) = default;
        _visualShadowShaderPaths.RefAt(orderIndex) = default;
        _visualShadowEffectResources.RefAt(orderIndex) = default;
        _visualShadowInstanceCounts.RefAt(orderIndex) = 0;
        _visualShadowInstanceOffsets.RefAt(orderIndex) = 0;
        _visualShadowFramePushConstantOffsets.RefAt(orderIndex) = 0;
        _visualHasShadow.RefAt(orderIndex) = false;
        _visualGlowPrograms.RefAt(orderIndex) = null;
        _visualGlowPushConstantSizes.RefAt(orderIndex) = 0;
        _visualGlowInstanceStrides.RefAt(orderIndex) = 0;
        _visualGlowInstanceBindings.RefAt(orderIndex) = default;
        _visualGlowShaderKinds.RefAt(orderIndex) = default;
        _visualGlowShaderPaths.RefAt(orderIndex) = default;
        _visualGlowEffectResources.RefAt(orderIndex) = default;
        _visualGlowInstanceCounts.RefAt(orderIndex) = 0;
        _visualGlowInstanceOffsets.RefAt(orderIndex) = 0;
        _visualGlowFramePushConstantOffsets.RefAt(orderIndex) = 0;
        _visualHasGlow.RefAt(orderIndex) = false;
        if (visual.Kind == UiVisualKind.Image)
        {
            if (!_registry.TryResolveImage(visual.Resource, out var imageTexture, out var imageSampler, out var imageBinding) || !imageBinding.HasValue)
            {
                AddDiagnostic($"Image resource {visual.Resource.Value} is not registered with a fragment binding.");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            _visualImageTextures.RefAt(orderIndex) = graph.ImportTexture(imageTexture);
            _visualImageSamplers.RefAt(orderIndex) = imageSampler;
            _visualImageBindings.RefAt(orderIndex) = imageBinding;
        }

        if (shaderPath == UiVisualShaderPath.SolidLinearGradient)
        {
            if (!_registry.TryResolveLinearGradient(visual.Resource, out var gradient))
            {
                AddDiagnostic($"Linear-gradient resource {visual.Resource.Value} is not registered.");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            _visualGradientResources.RefAt(orderIndex) = gradient;
        }

        if (shaderPath == UiVisualShaderPath.CachedMask)
        {
            if (!_registry.TryResolveMask(
                    effectResource.Parameters.CachedMask,
                    out var maskTexture,
                    out var maskSampler,
                    out var maskUvRect))
            {
                AddDiagnostic($"Cached mask resource {effectResource.Parameters.CachedMask.Value} is not registered.");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            _visualMaskTextures.RefAt(orderIndex) = graph.ImportTexture(maskTexture);
            _visualMaskResources.RefAt(orderIndex) = maskTexture;
            _visualMaskSamplers.RefAt(orderIndex) = maskSampler;
            _visualMaskUvRects.RefAt(orderIndex) = maskUvRect;
        }

        if (shadowVariant is { } shadow)
        {
            if (!UiVisualShaderContract.TryDescribeInstance(
                    shadow.Program,
                    shadow.Kind,
                    shadow.Path,
                    out var shadowKind,
                    out var shadowBinding,
                    out var shadowStride,
                    out var shadowPushConstantSize,
                    out var shadowPushConstantOffset,
                    out var shadowDiagnostic))
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] has an unsupported outer-shadow layer: {shadowDiagnostic}");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            var shadowFrameSize = UiVisualShaderContract.PackFrame(
                shadowKind,
                _viewport,
                _visualFramePushConstants.AsSpan(0, checked((int)shadowPushConstantSize)));
            if (shadowFrameSize != shadowPushConstantSize)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] generated shadow frame packer wrote {shadowFrameSize} bytes; expected {shadowPushConstantSize}.");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            _visualShadowPrograms.RefAt(orderIndex) = shadow.Program;
            _visualShadowPushConstantSizes.RefAt(orderIndex) = shadowPushConstantSize;
            _visualShadowInstanceStrides.RefAt(orderIndex) = shadowStride;
            _visualShadowInstanceBindings.RefAt(orderIndex) = shadowBinding;
            _visualShadowShaderKinds.RefAt(orderIndex) = shadowKind;
            _visualShadowShaderPaths.RefAt(orderIndex) = shadow.Path;
            _visualShadowEffectResources.RefAt(orderIndex) = effectResource;
            _visualShadowInstanceCounts.RefAt(orderIndex) = UiVisualShaderContract.MaxInstanceCount(shadowKind);
            _visualShadowFramePushConstantOffsets.RefAt(orderIndex) = shadowPushConstantOffset;
            _visualHasShadow.RefAt(orderIndex) = true;
        }

        if (glowVariant is { } glow)
        {
            if (!UiVisualShaderContract.TryDescribeInstance(
                    glow.Program,
                    glow.Kind,
                    glow.Path,
                    out var glowKind,
                    out var glowBinding,
                    out var glowStride,
                    out var glowPushConstantSize,
                    out var glowPushConstantOffset,
                    out var glowDiagnostic))
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] has an unsupported outer-glow layer: {glowDiagnostic}");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            var glowFrameSize = UiVisualShaderContract.PackFrame(
                glowKind,
                _viewport,
                _visualFramePushConstants.AsSpan(0, checked((int)glowPushConstantSize)));
            if (glowFrameSize != glowPushConstantSize)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] generated glow frame packer wrote {glowFrameSize} bytes; expected {glowPushConstantSize}.");
                _visualPrograms.RefAt(orderIndex) = null;
                return false;
            }

            _visualGlowPrograms.RefAt(orderIndex) = glow.Program;
            _visualGlowPushConstantSizes.RefAt(orderIndex) = glowPushConstantSize;
            _visualGlowInstanceStrides.RefAt(orderIndex) = glowStride;
            _visualGlowInstanceBindings.RefAt(orderIndex) = glowBinding;
            _visualGlowShaderKinds.RefAt(orderIndex) = glowKind;
            _visualGlowShaderPaths.RefAt(orderIndex) = glow.Path;
            _visualGlowEffectResources.RefAt(orderIndex) = effectResource;
            _visualGlowInstanceCounts.RefAt(orderIndex) = UiVisualShaderContract.MaxInstanceCount(glowKind);
            _visualGlowFramePushConstantOffsets.RefAt(orderIndex) = glowPushConstantOffset;
            _visualHasGlow.RefAt(orderIndex) = true;
        }

        return true;
    }

    private bool PrepareVisualInstances()
    {
        if (_visualInstancesPrepared)
        {
            return true;
        }

        if (_reuseVisualInstanceLayout)
        {
            return PrepareReusedVisualInstances();
        }

        _flatVisualInstanceBuffer = HasUniformVisualInstanceLayout();
        ulong byteCursor = 0;
        var previousOrderIndex = -1;
        for (var orderIndex = 0; orderIndex < _orderCount; orderIndex++)
        {
            if (_order.RefAt(orderIndex).Kind != UiDrawKind.Visual ||
                _commandClips.RefAt(orderIndex).IsEmpty ||
                _visualPrograms.RefAt(orderIndex) is null)
            {
                previousOrderIndex = -1;
                continue;
            }

            var startsNewSegment = previousOrderIndex < 0 || !CanJoinVisualSegment(previousOrderIndex, orderIndex);
            if (startsNewSegment && !_flatVisualInstanceBuffer)
            {
                byteCursor = Align(byteCursor, _visualInstanceAlignment);
            }

            var visual = _visuals.RefAt(_order.RefAt(orderIndex).Index);
            var clip = _commandClips.RefAt(orderIndex);
            var physicalVisual = _coordinates.ToPhysical(visual);
            if (_visualHasShadow.RefAt(orderIndex))
            {
                byteCursor = Align(byteCursor, _visualInstanceAlignment);
                var shadowStride = _visualShadowInstanceStrides.RefAt(orderIndex);
                var shadowOffset = checked((int)byteCursor);
                var shadowBytes = checked((int)shadowStride * _visualShadowInstanceCounts.RefAt(orderIndex));
                var shadowEnd = checked(shadowOffset + shadowBytes);
                EnsureCapacity(ref _visualInstanceBytes, shadowEnd);
                var shadowWritten = UiVisualShaderContract.PackInstances(
                    _visualShadowShaderKinds.RefAt(orderIndex),
                    in physicalVisual,
                    in clip,
                    in _visualShadowEffectResources.RefAt(orderIndex),
                    in _visualMaskUvRects.RefAt(orderIndex),
                    _dpiScale,
                    _visualGradientResources.RefAt(orderIndex),
                    shadowStride,
                    _visualInstanceBytes.AsSpan(shadowOffset, shadowBytes));
                if (shadowWritten != shadowBytes)
                {
                    AddDiagnostic($"Visual at Order[{orderIndex}] generated shadow packer wrote {shadowWritten} bytes; expected {shadowBytes}.");
                    return false;
                }

                _visualShadowInstanceOffsets.RefAt(orderIndex) = shadowOffset;
                byteCursor = checked(byteCursor + (uint)shadowWritten);
            }

            if (_visualHasGlow.RefAt(orderIndex))
            {
                byteCursor = Align(byteCursor, _visualInstanceAlignment);
                var glowStride = _visualGlowInstanceStrides.RefAt(orderIndex);
                var glowOffset = checked((int)byteCursor);
                var glowBytes = checked((int)glowStride * _visualGlowInstanceCounts.RefAt(orderIndex));
                var glowEnd = checked(glowOffset + glowBytes);
                EnsureCapacity(ref _visualInstanceBytes, glowEnd);
                var glowWritten = UiVisualShaderContract.PackInstances(
                    _visualGlowShaderKinds.RefAt(orderIndex),
                    in physicalVisual,
                    in clip,
                    in _visualGlowEffectResources.RefAt(orderIndex),
                    in _visualMaskUvRects.RefAt(orderIndex),
                    _dpiScale,
                    _visualGradientResources.RefAt(orderIndex),
                    glowStride,
                    _visualInstanceBytes.AsSpan(glowOffset, glowBytes));
                if (glowWritten != glowBytes)
                {
                    AddDiagnostic($"Visual at Order[{orderIndex}] generated glow packer wrote {glowWritten} bytes; expected {glowBytes}.");
                    return false;
                }

                _visualGlowInstanceOffsets.RefAt(orderIndex) = glowOffset;
                byteCursor = checked(byteCursor + (uint)glowWritten);
            }

            var stride = _visualInstanceStrides.RefAt(orderIndex);
            var offset = checked((int)byteCursor);
            var maxInstanceBytes = checked((int)stride * _visualInstanceCounts.RefAt(orderIndex));
            var end = checked(offset + maxInstanceBytes);
            EnsureCapacity(ref _visualInstanceBytes, end);
            var written = UiVisualShaderContract.PackInstances(
                _visualShaderKinds.RefAt(orderIndex),
                in physicalVisual,
                in clip,
                in _visualEffectResources.RefAt(orderIndex),
                in _visualMaskUvRects.RefAt(orderIndex),
                _dpiScale,
                _visualGradientResources.RefAt(orderIndex),
                stride,
                _visualInstanceBytes.AsSpan(offset, maxInstanceBytes));
            if (written <= 0 || written % (int)stride != 0)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] generated instance packer wrote {written} bytes; expected a positive multiple of {stride}.");
                return false;
            }

            _visualInstanceCounts.RefAt(orderIndex) = written / (int)stride;
            _visualInstanceOffsets.RefAt(orderIndex) = offset;
            byteCursor = checked(byteCursor + (uint)written);
            previousOrderIndex = _visualHasShadow.RefAt(orderIndex) || _visualHasGlow.RefAt(orderIndex)
                ? -1
                : orderIndex;
        }

        _visualInstanceByteCount = checked((int)byteCursor);
        _visualInstancePayloadDirty = HasVisualInstancePayloadChanged();
        _visualInstancesPrepared = true;
        return true;
    }

    private bool PrepareReusedVisualInstances()
    {
        _visualUploadRangeCount = 0;
        for (var orderIndex = 0; orderIndex < _orderCount; orderIndex++)
        {
            var draw = _order.RefAt(orderIndex);
            if (draw.Kind != UiDrawKind.Visual ||
                _commandClips.RefAt(orderIndex).IsEmpty ||
                _visualPrograms.RefAt(orderIndex) is null ||
                !_visualPayloadDirtyByIndex.RefAt(draw.Index))
            {
                continue;
            }

            var visual = _visuals.RefAt(draw.Index);
            var clip = _commandClips.RefAt(orderIndex);
            var physicalVisual = _coordinates.ToPhysical(visual);
            if (_visualHasShadow.RefAt(orderIndex))
            {
                var shadowStride = _visualShadowInstanceStrides.RefAt(orderIndex);
                var shadowOffset = _visualShadowInstanceOffsets.RefAt(orderIndex);
                var shadowBytes = checked((int)(shadowStride * (uint)_visualShadowInstanceCounts.RefAt(orderIndex)));
                var shadowWritten = UiVisualShaderContract.PackInstances(
                    _visualShadowShaderKinds.RefAt(orderIndex),
                    in physicalVisual,
                    in clip,
                    in _visualShadowEffectResources.RefAt(orderIndex),
                    in _visualMaskUvRects.RefAt(orderIndex),
                    _dpiScale,
                    _visualGradientResources.RefAt(orderIndex),
                    shadowStride,
                    _visualInstanceBytes.AsSpan(shadowOffset, shadowBytes));
                if (shadowWritten != shadowBytes)
                {
                    _reuseVisualInstanceLayout = false;
                    _visualInstancesPrepared = false;
                    return PrepareVisualInstances();
                }

                AddVisualUploadRange(shadowOffset, shadowWritten);
            }

            if (_visualHasGlow.RefAt(orderIndex))
            {
                var glowStride = _visualGlowInstanceStrides.RefAt(orderIndex);
                var glowOffset = _visualGlowInstanceOffsets.RefAt(orderIndex);
                var glowBytes = checked((int)(glowStride * (uint)_visualGlowInstanceCounts.RefAt(orderIndex)));
                var glowWritten = UiVisualShaderContract.PackInstances(
                    _visualGlowShaderKinds.RefAt(orderIndex),
                    in physicalVisual,
                    in clip,
                    in _visualGlowEffectResources.RefAt(orderIndex),
                    in _visualMaskUvRects.RefAt(orderIndex),
                    _dpiScale,
                    _visualGradientResources.RefAt(orderIndex),
                    glowStride,
                    _visualInstanceBytes.AsSpan(glowOffset, glowBytes));
                if (glowWritten != glowBytes)
                {
                    _reuseVisualInstanceLayout = false;
                    _visualInstancesPrepared = false;
                    return PrepareVisualInstances();
                }

                AddVisualUploadRange(glowOffset, glowWritten);
            }

            var stride = _visualInstanceStrides.RefAt(orderIndex);
            var instanceCount = _visualInstanceCounts.RefAt(orderIndex);
            var offset = _visualInstanceOffsets.RefAt(orderIndex);
            var instanceBytes = checked((int)(stride * (uint)instanceCount));
            var written = UiVisualShaderContract.PackInstances(
                _visualShaderKinds.RefAt(orderIndex),
                in physicalVisual,
                in clip,
                in _visualEffectResources.RefAt(orderIndex),
                in _visualMaskUvRects.RefAt(orderIndex),
                _dpiScale,
                _visualGradientResources.RefAt(orderIndex),
                stride,
                _visualInstanceBytes.AsSpan(offset, instanceBytes));
            if (written != instanceBytes)
            {
                _reuseVisualInstanceLayout = false;
                _visualInstancesPrepared = false;
                return PrepareVisualInstances();
            }

            AddVisualUploadRange(offset, written);
        }

        _visualInstancePayloadDirty = _visualUploadRangeCount != 0 ||
                                      _visualInstanceByteCount != _uploadedVisualInstanceByteCount;
        _visualInstancesPrepared = true;
        return true;
    }

    private bool HasVisualInstancePayloadChanged()
    {
        if (_visualInstanceByteCount != _uploadedVisualInstanceByteCount)
        {
            SetFullVisualUploadRange();
            return true;
        }

        for (var orderIndex = 0; orderIndex < _orderCount; orderIndex++)
        {
            var instanceCount = _visualInstanceCounts.RefAt(orderIndex);
            if (instanceCount == 0)
            {
                continue;
            }

            if (_visualHasShadow.RefAt(orderIndex))
            {
                var shadowOffset = _visualShadowInstanceOffsets.RefAt(orderIndex);
                var shadowLength = checked((int)(_visualShadowInstanceStrides.RefAt(orderIndex) * (uint)_visualShadowInstanceCounts.RefAt(orderIndex)));
                if (!_visualInstanceBytes.AsSpan(shadowOffset, shadowLength).SequenceEqual(
                        _uploadedVisualInstanceBytes.AsSpan(shadowOffset, shadowLength)))
                {
                    SetFullVisualUploadRange();
                    return true;
                }
            }

            if (_visualHasGlow.RefAt(orderIndex))
            {
                var glowOffset = _visualGlowInstanceOffsets.RefAt(orderIndex);
                var glowLength = checked((int)(_visualGlowInstanceStrides.RefAt(orderIndex) * (uint)_visualGlowInstanceCounts.RefAt(orderIndex)));
                if (!_visualInstanceBytes.AsSpan(glowOffset, glowLength).SequenceEqual(
                        _uploadedVisualInstanceBytes.AsSpan(glowOffset, glowLength)))
                {
                    SetFullVisualUploadRange();
                    return true;
                }
            }

            var offset = _visualInstanceOffsets.RefAt(orderIndex);
            var length = checked((int)(_visualInstanceStrides.RefAt(orderIndex) * (uint)instanceCount));
            if (!_visualInstanceBytes.AsSpan(offset, length).SequenceEqual(
                    _uploadedVisualInstanceBytes.AsSpan(offset, length)))
            {
                SetFullVisualUploadRange();
                return true;
            }
        }

        _visualUploadRangeCount = 0;
        return false;
    }

    private bool HasUniformVisualInstanceLayout()
    {
        IGraphicsShaderProgram? program = null;
        var stride = 0u;
        var binding = default(ShaderBinding);
        var found = false;
        for (var orderIndex = 0; orderIndex < _orderCount; orderIndex++)
        {
            if (_order.RefAt(orderIndex).Kind != UiDrawKind.Visual || _commandClips.RefAt(orderIndex).IsEmpty || _visualPrograms.RefAt(orderIndex) is not { } candidate)
            {
                continue;
            }

            if (!found)
            {
                if (_visualHasShadow.RefAt(orderIndex) || _visualHasGlow.RefAt(orderIndex))
                {
                    return false;
                }

                program = candidate;
                stride = _visualInstanceStrides.RefAt(orderIndex);
                binding = _visualInstanceBindings.RefAt(orderIndex);
                found = true;
                continue;
            }

            if (!IsSameShaderProgram(program, candidate) || stride != _visualInstanceStrides.RefAt(orderIndex) || binding != _visualInstanceBindings.RefAt(orderIndex))
            {
                return false;
            }
        }

        return found;
    }

    private static bool IsSameShaderProgram(IGraphicsShaderProgram? first, IGraphicsShaderProgram second)
    {
        return first is not null &&
            ReferenceEquals(first.Vertex, second.Vertex) &&
            ReferenceEquals(first.Fragment, second.Fragment);
    }

    private void EnsureVisualInstanceBuffer(ulong requiredBytes)
    {
        if (_session is null)
        {
            throw new InvalidOperationException("A render session is required for UI instance storage.");
        }

        if (_visualInstanceBufferCapacity >= requiredBytes && _visualInstanceBuffer.IsValid)
        {
            return;
        }

        var capacity = _visualInstanceBufferCapacity == 0 ? 256UL : _visualInstanceBufferCapacity;
        while (capacity < requiredBytes)
        {
            capacity = checked(capacity * 2);
        }

        var replacement = _session.CreateBuffer(new RenderBufferDescription(
            capacity,
            RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination));
        var previous = _visualInstanceBuffer;
        _visualInstanceBuffer = replacement;
        _visualInstanceBufferCapacity = capacity;
        _visualInstancePayloadDirty = true;
        if (previous.IsValid)
        {
            _session.Release(previous);
        }
    }

    private void CommitVisualInstanceSnapshot()
    {
        EnsureCapacity(ref _uploadedVisualInstanceBytes, _visualInstanceByteCount);
        for (var index = 0; index < _visualUploadRangeCount; index++)
        {
            var range = _visualUploadRanges[index];
            _visualInstanceBytes.AsSpan(
                checked((int)range.Offset),
                checked((int)range.SizeInBytes)).CopyTo(_uploadedVisualInstanceBytes.AsSpan(
                    checked((int)range.Offset),
                    checked((int)range.SizeInBytes)));
        }

        _uploadedVisualInstanceByteCount = _visualInstanceByteCount;
        _visualInstancePayloadDirty = false;
    }

    internal void RecordVisualUpload(ITransferCommandContext commands)
    {
        for (var index = 0; index < _visualUploadRangeCount; index++)
        {
            var range = _visualUploadRanges[index];
            commands.UploadBuffer(
                _visualInstanceGraphHandle,
                _visualInstanceBytes.AsSpan(
                    checked((int)range.Offset),
                    checked((int)range.SizeInBytes)),
                range.Offset);
        }

        CommitVisualInstanceSnapshot();
    }

    internal void RecordTextVisualUpload(ITransferCommandContext commands)
    {
        if (_textFeature?.CompositeUploadPending == true)
        {
            _textFeature.RecordCompositeUpload(commands);
        }

        if (_visualInstanceByteCount > 0 && _visualInstancePayloadDirty)
        {
            RecordVisualUpload(commands);
        }
    }

    private void SetFullVisualUploadRange()
    {
        _visualUploadRangeCount = 0;
        if (_visualInstanceByteCount != 0)
        {
            AddVisualUploadRange(0, _visualInstanceByteCount);
        }
    }

    private void AddVisualUploadRange(int offset, int length)
    {
        if (length <= 0)
        {
            return;
        }

        var start = checked((ulong)offset);
        var end = checked(start + (ulong)length);
        if (_visualUploadRangeCount != 0)
        {
            ref var previous = ref _visualUploadRanges.RefAt(_visualUploadRangeCount - 1);
            var previousEnd = checked(previous.Offset + previous.SizeInBytes);
            if (start <= checked(previousEnd + (ulong)VisualUploadMergeGapBytes))
            {
                previous = new BufferRange(previous.Offset, checked((previousEnd >= end ? previousEnd : end) - previous.Offset));
                return;
            }
        }

        EnsureCapacity(ref _visualUploadRanges, checked(_visualUploadRangeCount + 1));
        _visualUploadRanges.RefAt(_visualUploadRangeCount++) = new BufferRange(start, checked(end - start));
    }

    private static ulong Align(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private bool CanJoinVisualSegment(int firstOrderIndex, int nextOrderIndex)
    {
        if (_visualHasShadow.RefAt(firstOrderIndex) || _visualHasShadow.RefAt(nextOrderIndex) ||
            _visualHasGlow.RefAt(firstOrderIndex) || _visualHasGlow.RefAt(nextOrderIndex))
        {
            return false;
        }

        if (!ReferenceEquals(_visualPrograms.RefAt(firstOrderIndex), _visualPrograms.RefAt(nextOrderIndex)) ||
            _visualPushConstantSizes.RefAt(firstOrderIndex) != _visualPushConstantSizes.RefAt(nextOrderIndex) ||
            _visualInstanceStrides.RefAt(firstOrderIndex) != _visualInstanceStrides.RefAt(nextOrderIndex) ||
            _visualInstanceBindings.RefAt(firstOrderIndex) != _visualInstanceBindings.RefAt(nextOrderIndex))
        {
            return false;
        }

        if (_visualShaderPaths.RefAt(firstOrderIndex) == UiVisualShaderPath.SolidImage)
        {
            return _visualImageTextures.RefAt(firstOrderIndex) == _visualImageTextures.RefAt(nextOrderIndex) &&
                   _visualImageSamplers.RefAt(firstOrderIndex) == _visualImageSamplers.RefAt(nextOrderIndex);
        }

        if (_visualShaderPaths.RefAt(firstOrderIndex) != UiVisualShaderPath.CachedMask)
        {
            return true;
        }

        return _visualMaskResources.RefAt(firstOrderIndex) == _visualMaskResources.RefAt(nextOrderIndex) &&
               _visualMaskSamplers.RefAt(firstOrderIndex) == _visualMaskSamplers.RefAt(nextOrderIndex);
    }

    internal void RecordVisualSegment(
        IRasterCommandContext commands,
        int firstOrderIndex,
        int visualCount,
        ulong segmentInstanceCount,
        UiVisualRenderLayer layer)
    {
        var isShadow = layer == UiVisualRenderLayer.Shadow;
        var isGlow = layer == UiVisualRenderLayer.Glow;
        var pushConstantSizes = isShadow ? _visualShadowPushConstantSizes : isGlow ? _visualGlowPushConstantSizes : _visualPushConstantSizes;
        var pushConstantOffsets = isShadow ? _visualShadowFramePushConstantOffsets : isGlow ? _visualGlowFramePushConstantOffsets : _visualFramePushConstantOffsets;
        var strides = isShadow ? _visualShadowInstanceStrides : isGlow ? _visualGlowInstanceStrides : _visualInstanceStrides;
        var bindings = isShadow ? _visualShadowInstanceBindings : isGlow ? _visualGlowInstanceBindings : _visualInstanceBindings;
        var offsets = isShadow ? _visualShadowInstanceOffsets : isGlow ? _visualGlowInstanceOffsets : _visualInstanceOffsets;
        var kinds = isShadow ? _visualShadowShaderKinds : isGlow ? _visualGlowShaderKinds : _visualShaderKinds;
        commands.SetViewport(new RenderViewport(0, 0, _viewport.Width, _viewport.Height));
        commands.PushConstants(_visualFramePushConstants.AsSpan(
            0,
            checked((int)pushConstantSizes.RefAt(firstOrderIndex))),
            pushConstantOffsets.RefAt(firstOrderIndex));
        var stride = strides.RefAt(firstOrderIndex);

        if (_flatVisualInstanceBuffer && layer == UiVisualRenderLayer.Base)
        {
            commands.BindBuffer(bindings.RefAt(firstOrderIndex), _visualInstanceGraphHandle, 0, checked((ulong)_visualInstanceByteCount));
        }
        else
        {
            commands.BindBuffer(
                bindings.RefAt(firstOrderIndex),
                _visualInstanceGraphHandle,
                checked((ulong)offsets.RefAt(firstOrderIndex)),
                checked((ulong)stride * segmentInstanceCount));
        }

        if (!isShadow && kinds.RefAt(firstOrderIndex) == UiRectangleShaderKind.CachedMaskRounded)
        {
            var maskTexture = _visualMaskTextures.RefAt(firstOrderIndex)
                ?? throw new InvalidOperationException("The cached-mask graph resource was not imported.");
            commands.BindTexture(
                UiVisualShaderContract.CachedMaskTextureBinding,
                maskTexture,
                _visualMaskSamplers.RefAt(firstOrderIndex));
        }

        if (!isShadow && kinds.RefAt(firstOrderIndex) == UiRectangleShaderKind.SolidImage)
        {
            var imageTexture = _visualImageTextures.RefAt(firstOrderIndex)
                ?? throw new InvalidOperationException("The image graph resource was not imported.");
            commands.BindTexture(
                UiVisualShaderContract.ImageTextureBinding,
                imageTexture,
                _visualImageSamplers.RefAt(firstOrderIndex));
        }

        if (UiVisualShaderContract.UsesShaderClip(kinds.RefAt(firstOrderIndex)))
        {
            if (segmentInstanceCount == 0)
            {
                return;
            }

            commands.SetScissor(new PixelRect(
                0,
                0,
                checked((int)_viewport.Width),
                checked((int)_viewport.Height)));
            var firstInstance = _flatVisualInstanceBuffer && layer == UiVisualRenderLayer.Base
                ? checked((uint)((ulong)offsets.RefAt(firstOrderIndex) / stride))
                : 0;
            commands.Draw(6, checked((uint)segmentInstanceCount), 0, firstInstance);
            return;
        }

        var segmentEnd = checked(firstOrderIndex + visualCount);
        var drawStart = firstOrderIndex;
        while (drawStart < segmentEnd)
        {
            var clip = _commandClips.RefAt(drawStart);
            var drawEnd = drawStart + 1;
            var firstDrawOrder = drawStart;
            uint instanceCount = (uint)(isShadow ? _visualShadowInstanceCounts : isGlow ? _visualGlowInstanceCounts : _visualInstanceCounts).RefAt(drawStart);
            while (drawEnd < segmentEnd && _commandClips.RefAt(drawEnd) == clip)
            {
                var drawCount = isShadow ? _visualShadowInstanceCounts.RefAt(drawEnd) : isGlow ? _visualGlowInstanceCounts.RefAt(drawEnd) : _visualInstanceCounts.RefAt(drawEnd);
                if (instanceCount == 0 && drawCount != 0)
                {
                    firstDrawOrder = drawEnd;
                }

                instanceCount = checked(instanceCount + (uint)drawCount);
                drawEnd++;
            }

            if (instanceCount != 0)
            {
                commands.SetScissor(_coordinates.ToPhysical(clip));
                var firstInstance = _flatVisualInstanceBuffer && layer == UiVisualRenderLayer.Base
                    ? checked((uint)((ulong)offsets.RefAt(firstDrawOrder) / stride))
                    : checked((uint)(((ulong)offsets.RefAt(firstDrawOrder) - (ulong)offsets.RefAt(firstOrderIndex)) / stride));
                commands.Draw(6, instanceCount, 0, firstInstance);
            }

            drawStart = drawEnd;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ClearFrameStorage();
        var instanceBuffer = _visualInstanceBuffer;
        _visualInstanceBuffer = default;
        _visualInstanceBufferCapacity = 0;
        if (instanceBuffer.IsValid && _session is not null)
        {
            _session.Release(instanceBuffer);
        }

        _disposed = true;
    }

    private void ResolveVisualPlan(
        UiVisualDraw visual,
        out UiVisualShaderVariant baseVariant,
        out UiVisualShaderVariant? shadowVariant,
        out UiVisualShaderVariant? glowVariant,
        out UiEffectResource effectResource)
    {
        if (visual.Paint.EffectSet.IsValid &&
            _registry.TryResolveVisualEffectLayers(
                visual.Paint.EffectSet,
                visual.Kind,
                out baseVariant,
                out shadowVariant,
                out glowVariant,
                out effectResource))
        {
            return;
        }

        var program = ResolveVisualProgram(visual, out var visualKind, out var path, out effectResource);
        baseVariant = new UiVisualShaderVariant(program, visualKind, path);
        shadowVariant = null;
        glowVariant = null;
    }

    private IGraphicsShaderProgram ResolveVisualProgram(
        UiVisualDraw visual,
        out UiVisualKind shaderVisualKind,
        out UiVisualShaderPath shaderPath,
        out UiEffectResource effectResource)
    {
        shaderVisualKind = visual.Kind;
        shaderPath = UiVisualShaderPath.Standard;
        effectResource = default;
        if (visual.Kind == UiVisualKind.Image)
        {
            shaderPath = UiVisualShaderPath.SolidImage;
            return _imageVisualProgram ?? _defaultVisualProgram ?? throw new InvalidOperationException("The visual shader program is not configured.");
        }

        if (visual.Kind == UiVisualKind.SolidRectangle && visual.Resource.IsValid &&
            _registry.TryResolveLinearGradient(visual.Resource, out _))
        {
            shaderPath = UiVisualShaderPath.SolidLinearGradient;
            return _linearGradientVisualProgram ?? _defaultVisualProgram ?? throw new InvalidOperationException("The visual shader program is not configured.");
        }
        if (visual.Paint.EffectSet.IsValid &&
            _registry.TryResolveVisualEffectSet(visual.Paint.EffectSet, visual.Kind, out var effectVariant, out effectResource))
        {
            shaderVisualKind = effectVariant.Kind;
            shaderPath = effectVariant.Path;
            return effectVariant.Program;
        }

        if (visual.Kind == UiVisualKind.Custom && _registry.TryResolveVisualType(visual.VisualType, out var registered))
        {
            return registered ?? throw new InvalidOperationException("The visual registry returned a null program.");
        }

        if (visual.Kind == UiVisualKind.SolidRectangle && _solidVisualProgram is not null)
        {
            return _solidVisualProgram;
        }

        if ((visual.Kind == UiVisualKind.RoundedRectangle || visual.Kind == UiVisualKind.Border) &&
            UiDisplayListGeometry.IsZero(visual.Paint.CornerRadii) &&
            _solidVisualProgram is not null)
        {
            shaderVisualKind = UiVisualKind.SolidRectangle;
            return _solidVisualProgram;
        }

        if ((visual.Kind == UiVisualKind.RoundedRectangle || visual.Kind == UiVisualKind.Border) &&
            !UiDisplayListGeometry.IsZero(visual.Paint.CornerRadii) &&
            _roundedSliceVisualProgram is not null)
        {
            return _roundedSliceVisualProgram;
        }

        return _defaultVisualProgram ?? throw new InvalidOperationException("The visual shader program is not configured.");
    }

    private bool ValidateVisual(UiVisualDraw visual, int orderIndex, out PixelRect clip)
    {
        clip = default;
        if (!_clipResolver.TryGetClip(visual.Clip, out clip, orderIndex))
        {
            return false;
        }

        if (!UiDisplayListGeometry.IsFinite(visual.Bounds) || !UiDisplayListGeometry.IsFinite(visual.Paint.FillColor) ||
            !UiDisplayListGeometry.IsFinite(visual.Paint.CornerRadii))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] contains non-finite geometry or paint.");
            return false;
        }

        if (visual.Paint.EffectSet != UiEffectSet.None &&
            (!visual.Paint.EffectSet.IsValid || visual.Paint.EffectSet.Target != UiEffectTarget.Visual))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] contains an invalid visual effect set.");
            return false;
        }

        var visualVariant = default(UiVisualShaderVariant);
        UiVisualShaderVariant? shadowVariant = null;
        UiVisualShaderVariant? glowVariant = null;
        UiEffectResource effectResource = default;
        var effectRevision = 0UL;
        if (visual.Paint.EffectSet.IsValid &&
            (!_registry.TryResolveVisualEffectSet(visual.Paint.EffectSet, visual.Kind, out visualVariant, out effectResource) ||
             !_registry.TryGetVisualEffectRevision(visual.Paint.EffectSet, out effectRevision)))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] references an unregistered effect-set resource.");
            return false;
        }

        if (visual.Paint.EffectSet.IsValid &&
            _registry.TryResolveVisualEffectLayers(
                visual.Paint.EffectSet,
                visual.Kind,
                out _,
                out shadowVariant,
                out glowVariant,
                out _) &&
            shadowVariant.HasValue)
        {
            if (shadowVariant is not { } resolvedShadow ||
                !IsCompatibleVisualVariant(visual.Kind, resolvedShadow.Kind) ||
                resolvedShadow.Path != UiVisualShaderPath.OuterShadowOnlyEffect)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] uses an incompatible outer-shadow shader layer.");
                return false;
            }
        }

        if (visual.Paint.EffectSet.IsValid && glowVariant.HasValue)
        {
            if (glowVariant is not { } resolvedGlow ||
                !IsCompatibleVisualVariant(visual.Kind, resolvedGlow.Kind) ||
                resolvedGlow.Path != UiVisualShaderPath.OuterGlowOnlyEffect)
            {
                AddDiagnostic($"Visual at Order[{orderIndex}] uses an incompatible outer-glow shader layer.");
                return false;
            }
        }

        if (visual.Paint.EffectSet.IsValid &&
            !IsCompatibleVisualVariant(visual.Kind, visualVariant.Kind))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] uses an effect shader variant for {visualVariant.Kind}, not {visual.Kind}.");
            return false;
        }

        if (visual.Paint.EffectSet.IsValid &&
            ((visualVariant.Path == UiVisualShaderPath.CachedMask &&
              effectResource.Set.Quality != UiEffectQuality.CachedMask)))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] requests an effect layer incompatible with its registered UI shader ABI.");
            return false;
        }

        if (visual.Kind == UiVisualKind.SolidRectangle &&
            !UiDisplayListGeometry.IsZero(visual.Paint.CornerRadii))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] requests rounded geometry from a solid visual kind.");
            return false;
        }

        _visualEffectRevisionsByIndex.RefAt(_order.RefAt(orderIndex).Index) = effectRevision;

        switch (visual.Kind)
        {
            case UiVisualKind.SolidRectangle:
                if (visual.Resource.IsValid && !_registry.TryResolveLinearGradient(visual.Resource, out _))
                {
                    AddDiagnostic($"Solid visual at Order[{orderIndex}] has an unknown resource identity.");
                    return false;
                }

                return true;
            case UiVisualKind.Image:
                if (!visual.Resource.IsValid || !_registry.TryResolveImage(visual.Resource, out _, out _, out _))
                {
                    AddDiagnostic($"Image visual at Order[{orderIndex}] has an unknown or stale resource identity.");
                    return false;
                }

                return true;
            case UiVisualKind.Custom:
                if (!visual.VisualType.IsValid || !_registry.TryResolveVisualType(visual.VisualType, out _))
                {
                    AddDiagnostic($"Custom visual at Order[{orderIndex}] has an unknown visual type identity.");
                    return false;
                }

                return true;
            case UiVisualKind.RoundedRectangle:
            case UiVisualKind.Border:
                return ValidateRoundedVisual(visual, orderIndex);
            default:
                AddDiagnostic($"Visual kind {visual.Kind} at Order[{orderIndex}] is unknown.");
                return false;
        }
    }

    private bool ValidateRoundedVisual(UiVisualDraw visual, int orderIndex)
    {
        var radii = visual.Paint.CornerRadii;
        if (radii.x < 0 || radii.y < 0 || radii.z < 0 || radii.w < 0)
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] has negative corner radii.");
            return false;
        }

        var width = (double)visual.Bounds.z;
        var height = (double)visual.Bounds.w;
        if (width <= 0 || height <= 0)
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] has non-positive bounds for rounded geometry.");
            return false;
        }

        if ((double)radii.x + radii.y > width ||
            (double)radii.w + radii.z > width ||
            (double)radii.x + radii.w > height ||
            (double)radii.y + radii.z > height)
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] has adjacent corner radii exceeding its bounds.");
            return false;
        }

        return true;
    }

    private static bool IsCompatibleVisualVariant(UiVisualKind visualKind, UiVisualKind variantKind)
        => visualKind == variantKind ||
            visualKind is UiVisualKind.RoundedRectangle or UiVisualKind.Border &&
            variantKind is UiVisualKind.RoundedRectangle or UiVisualKind.Border;

    private static TextEffectValues ToTextEffectValues(UiEffectResource effectResource, float dpiScale)
    {
        var scale = effectResource.Parameters.Units switch
        {
            PaintUnits.Logical => dpiScale,
            PaintUnits.Device => 1f,
            _ => throw new ArgumentOutOfRangeException(nameof(effectResource), effectResource.Parameters.Units, "Unknown paint unit system."),
        };
        var stroke = effectResource.Parameters.Stroke;
        var outerGlow = effectResource.Parameters.OuterGlow;
        var outerShadow = effectResource.Parameters.OuterShadow;
        return new TextEffectValues(
            new Vector4(stroke.Color.x, stroke.Color.y, stroke.Color.z, stroke.Color.w),
            stroke.Width * scale,
            new Vector4(outerGlow.Color.x, outerGlow.Color.y, outerGlow.Color.z, outerGlow.Color.w),
            outerGlow.BlurRadius * scale,
            outerGlow.Intensity,
            new Vector4(outerShadow.Color.x, outerShadow.Color.y, outerShadow.Color.z, outerShadow.Color.w),
            new Vector2(outerShadow.Offset.x * scale, outerShadow.Offset.y * scale),
            outerShadow.Width * scale,
            outerShadow.BlurRadius * scale,
            outerShadow.Spread * scale,
            outerShadow.Intensity);
    }

    private bool ValidateText(UiTextDraw text, int orderIndex, out PixelRect clip)
    {
        clip = default;
        if (!_clipResolver.TryGetClip(text.Clip, out clip, orderIndex))
        {
            return false;
        }

        if (text.Text is null || !UiDisplayListGeometry.IsFinite(text.BaselineOrigin) ||
            !UiDisplayListGeometry.IsFinite(text.Paint.FillColor))
        {
            AddDiagnostic($"Text at Order[{orderIndex}] contains an invalid shaped value or paint.");
            return false;
        }

        if (text.Paint.EffectSet != UiEffectSet.None &&
            (!text.Paint.EffectSet.IsValid || text.Paint.EffectSet.Target != UiEffectTarget.Text))
        {
            AddDiagnostic($"Text at Order[{orderIndex}] contains an invalid text effect set.");
            return false;
        }

        if (text.Paint.EffectSet.Quality == UiEffectQuality.CachedMask)
        {
            AddDiagnostic($"Text at Order[{orderIndex}] requests unsupported CachedMask text rendering; use the generated stroke/outer-glow text artifact.");
            return false;
        }

        var textVariant = default(TextShaderVariant);
        if (text.Paint.EffectSet.IsValid && !_registry.TryResolveTextEffectSet(text.Paint.EffectSet, out textVariant, out _))
        {
            AddDiagnostic($"Text at Order[{orderIndex}] references an unregistered text effect-set resource.");
            return false;
        }

        if (text.Paint.EffectSet.IsValid)
        {
            if (_textFeature is null || !_textFeature.TryResolveTextVariant(textVariant, out _))
            {
                AddDiagnostic($"Text at Order[{orderIndex}] references a registered text effect set without a compatible generated text packer.");
                return false;
            }
        }

        return true;
    }

    private int NextValidationEpoch()
    {
        if (_validationEpoch == int.MaxValue)
        {
            Array.Clear(_visualSeenEpochs, 0, _visualSeenEpochs.Length);
            Array.Clear(_textSeenEpochs, 0, _textSeenEpochs.Length);
            _validationEpoch = 1;
        }
        else
        {
            _validationEpoch++;
        }

        return _validationEpoch;
    }

    private bool CanReuseVisualInstanceLayout(UiDisplayList displayList)
    {
        if (!_hasFrame || !_visualInstancesPrepared || !_visualCommandsPrepared ||
            _visualCount != displayList.Visuals.Length ||
            _clipCount != displayList.Clips.Length ||
            _orderCount != displayList.Order.Length ||
            !displayList.Clips.SequenceEqual(_clips.AsSpan(0, _clipCount)) ||
            !displayList.Order.SequenceEqual(_order.AsSpan(0, _orderCount)))
        {
            return false;
        }

        EnsureCapacity(ref _visualPayloadDirtyByIndex, displayList.Visuals.Length);
        EnsureCapacity(ref _visualEffectRevisionsByIndex, displayList.Visuals.Length);
        for (var index = 0; index < displayList.Visuals.Length; index++)
        {
            var previous = _visuals.RefAt(index);
            var current = displayList.Visuals[index];
            if (!HasStableVisualInstanceLayout(in previous, in current))
            {
                return false;
            }

            var effectRevision = 0UL;
            if (current.Paint.EffectSet.IsValid &&
                !_registry.TryGetVisualEffectRevision(current.Paint.EffectSet, out effectRevision))
            {
                return false;
            }

            _visualPayloadDirtyByIndex.RefAt(index) = !previous.Equals(current) ||
                                                     _visualEffectRevisionsByIndex.RefAt(index) != effectRevision;
            _visualEffectRevisionsByIndex.RefAt(index) = effectRevision;
        }

        return true;
    }

    private static bool HasStableVisualInstanceLayout(in UiVisualDraw previous, in UiVisualDraw current)
    {
        if (current.Kind is UiVisualKind.Image or UiVisualKind.Custom)
        {
            return previous.Equals(current);
        }

        return previous.Kind == current.Kind &&
               previous.VisualType.Equals(current.VisualType) &&
               previous.Bounds.Equals(current.Bounds) &&
               previous.Clip.Equals(current.Clip) &&
               previous.Resource.Equals(current.Resource) &&
               previous.Paint.CornerRadii.Equals(current.Paint.CornerRadii) &&
               previous.Paint.EffectSet.Equals(current.Paint.EffectSet);
    }

    private void ClearFrameStorage(bool preserveVisualInstanceLayout = false)
    {
        if (!preserveVisualInstanceLayout)
        {
            Array.Clear(_visuals, 0, _visualCount);
            Array.Clear(_clips, 0, _clipCount);
            Array.Clear(_texts, 0, _textCount);
            Array.Clear(_order, 0, _orderCount);
            Array.Clear(_visualPrograms, 0, _orderCount);
            Array.Clear(_visualShadowPrograms, 0, _orderCount);
            Array.Clear(_visualHasShadow, 0, _orderCount);
            Array.Clear(_visualGlowPrograms, 0, _orderCount);
            Array.Clear(_visualHasGlow, 0, _orderCount);
            _visualInstanceByteCount = 0;
            _flatVisualInstanceBuffer = false;
            _hasPackedVisualFrame = false;
            _packedVisualFrameKind = default;
            _packedVisualFrameSize = 0;
            _packedVisualFrameOffset = 0;
        }

        _visualCount = 0;
        _clipCount = 0;
        _textCount = 0;
        _orderCount = 0;
        _visualInstanceGraphHandle = default;
        _visualInstancesPrepared = false;
        _visualCommandsPrepared = preserveVisualInstanceLayout;
        _reuseVisualInstanceLayout = preserveVisualInstanceLayout;
        _visualUploadRangeCount = 0;
        _hasFrame = false;
        _clipResolver.Clear();
    }

    private void AddDiagnostic(string message) => _diagnostics.Add(message);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly struct UiCoordinateMapper
    {
        private readonly float _dpiScale;
        private readonly PixelExtent _physicalViewport;

        internal UiCoordinateMapper(float dpiScale, PixelExtent physicalViewport)
        {
            _dpiScale = dpiScale;
            _physicalViewport = physicalViewport;
        }

        internal PixelExtent LogicalViewport => new(
            ToLogicalExtent(_physicalViewport.Width),
            ToLogicalExtent(_physicalViewport.Height));

        internal UiVisualDraw ToPhysical(UiVisualDraw visual)
        {
            var paint = visual.Paint;
            var physicalPaint = paint with
            {
                CornerRadii = ToPhysical(paint.CornerRadii),
            };
            return visual with
            {
                Bounds = ToPhysical(visual.Bounds),
                Paint = physicalPaint,
            };
        }

        internal float2 ToPhysical(float2 value)
            => new(value.x * _dpiScale, value.y * _dpiScale);

        internal float4 ToPhysical(float4 value)
            => new(
                value.x * _dpiScale,
                value.y * _dpiScale,
                value.z * _dpiScale,
                value.w * _dpiScale);

        internal PixelRect ToPhysical(PixelRect value)
        {
            if (_dpiScale == 1f || value.IsEmpty)
            {
                return value;
            }

            var scale = (double)_dpiScale;
            var left = checked((int)Maths.Floor(value.X * scale));
            var top = checked((int)Maths.Floor(value.Y * scale));
            var right = checked((int)Maths.Ceil((value.X + (double)value.Width) * scale));
            var bottom = checked((int)Maths.Ceil((value.Y + (double)value.Height) * scale));
            return new PixelRect(left, top, checked(right - left), checked(bottom - top));
        }

        private uint ToLogicalExtent(uint physicalExtent)
        {
            var logicalExtent = physicalExtent / (double)_dpiScale;
            if (logicalExtent <= 0)
            {
                return 0;
            }

            return logicalExtent >= uint.MaxValue
                ? uint.MaxValue
                : checked((uint)Maths.Ceil(logicalExtent));
        }
    }

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (required <= storage.Length)
        {
            return;
        }

        var capacity = storage.Length == 0 ? 4 : storage.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        Array.Resize(ref storage, capacity);
    }

}
