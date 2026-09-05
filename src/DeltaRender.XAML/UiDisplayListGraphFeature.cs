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
    private readonly UiDisplayListResourceRegistry _registry;
    private readonly TextRenderFeature? _textFeature;
    private readonly UiTextVisualUploadPass _textVisualUploadPass;
    private readonly UiClipResolver _clipResolver;
    private readonly PixelExtent _viewport;
    private readonly List<string> _diagnostics = [];
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
    private int[] _visualInstanceCounts = [];
    private uint[] _visualFramePushConstantOffsets = [];
    private RenderGraphTextureHandle?[] _visualImageTextures = [];
    private RenderSamplerHandle[] _visualImageSamplers = [];
    private ShaderBinding?[] _visualImageBindings = [];
    private int[] _visualSeenEpochs = [];
    private int[] _textSeenEpochs = [];
    private int[] _textRunIndices = [];
    private int _visualCount;
    private int _clipCount;
    private int _textCount;
    private int _orderCount;
    private int[] _visualInstanceOffsets = [];
    private bool[] _visualPayloadDirtyByIndex = [];
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
        : this(null, null, viewport, null, null, null, null, false)
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
        IGraphicsShaderProgram? roundedSliceVisualProgram = null)
        : this(session, visualProgram, viewport, registry, textFeature, solidVisualProgram, roundedSliceVisualProgram, true)
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
        _viewport = viewport;
        _registry = registry ?? new UiDisplayListResourceRegistry();
        _textFeature = textFeature;
        _textVisualUploadPass = new UiTextVisualUploadPass(this);
        _clipResolver = new UiClipResolver(viewport, _diagnostics);
        _coordinates = new UiCoordinateMapper(1f, viewport);
        if (session is not null)
        {
            var alignment = session.Capabilities.MinStorageBufferOffsetAlignment;
            _visualInstanceAlignment = alignment >= 1UL ? alignment : 1UL;
        }
    }

    /// <summary>Gets the reusable diagnostic list from the latest consume or submit attempt.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

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
        EnsureCapacity(ref _visualInstanceCounts, displayList.Order.Length);
        EnsureCapacity(ref _visualInstanceOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualFramePushConstantOffsets, displayList.Order.Length);
        EnsureCapacity(ref _visualImageTextures, displayList.Order.Length);
        EnsureCapacity(ref _visualImageSamplers, displayList.Order.Length);
        EnsureCapacity(ref _visualImageBindings, displayList.Order.Length);
        EnsureCapacity(ref _visualPayloadDirtyByIndex, displayList.Visuals.Length);

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
                var textRunCount = 0;
                var previousWasText = false;
                for (var index = 0; index < _orderCount; index++)
                {
                    var draw = _order.RefAt(index);
                    if (draw.Kind != UiDrawKind.Text)
                    {
                        previousWasText = false;
                        continue;
                    }

                    var text = _texts.RefAt(draw.Index);
                    var color = text.Paint.FillColor;
                    var identity = _identities.RefAt(index);
                    var origin = _coordinates.ToPhysical(text.BaselineOrigin);
                    _textRunIndices.RefAt(index) = _textFeature.QueueCompositeRun(
                        text.Text,
                        origin.x,
                        origin.y,
                        new Vector4(color.x, color.y, color.z, color.w),
                        _coordinates.ToPhysical(_commandClips.RefAt(index)),
                        mergeWithPrevious: previousWasText,
                        producerRunId: identity.Value,
                        producerRunGeneration: identity.Generation,
                        producerRunVersion: identity.Version);
                    textRunCount++;
                    previousWasText = true;
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
                if (!textPrepared || _textFeature is null)
                {
                    continue;
                }

                var end = index + 1;
                while (end < _orderCount && _order.RefAt(end).Kind == UiDrawKind.Text)
                {
                    end++;
                }

                var firstRun = _textRunIndices.RefAt(index);
                var lastRun = _textRunIndices.RefAt(end - 1);
                var textFeaturePass = GetTextPass(firstRun, checked(lastRun - firstRun + 1));
                var textPass = graph.AddRasterPass(textFeaturePass.Description, textFeaturePass);
                graph.UseColorAttachment(
                    textPass,
                    0,
                    new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
                _textFeature.ConfigureCompositePass(graph, textPass);
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

            var visualEnd = index + 1;
            var segmentInstanceCount = (ulong)_visualInstanceCounts.RefAt(index);
            while (visualEnd < _orderCount &&
                   _order.RefAt(visualEnd).Kind == UiDrawKind.Visual &&
                   !_commandClips.RefAt(visualEnd).IsEmpty &&
                   CanJoinVisualSegment(index, visualEnd))
            {
                segmentInstanceCount = checked(segmentInstanceCount + (ulong)_visualInstanceCounts.RefAt(visualEnd));
                visualEnd++;
            }

            var visualPass = GetVisualSegmentPass(index, visualEnd - index, segmentInstanceCount, program);
            var pass = graph.AddRasterPass(visualPass.Description, visualPass);
            graph.UseColorAttachment(
                pass,
                0,
                new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
            graph.UseBuffer(pass, _visualInstanceGraphHandle, RenderResourceAccess.Read, RenderPipelineStages.Vertex);

            for (var visualIndex = index; visualIndex < visualEnd; visualIndex++)
            {
                if (_visualImageTextures.RefAt(visualIndex).HasValue)
                {
                    var graphTexture = _visualImageTextures.RefAt(visualIndex) ?? throw new InvalidOperationException("The image graph resource was not imported.");
                    graph.UseTexture(pass, graphTexture, RenderResourceAccess.Read, RenderPipelineStages.Fragment);
                }
            }

            index = visualEnd - 1;
        }
    }

    private UiVisualSegmentPass GetVisualSegmentPass(
        int firstOrderIndex,
        int visualCount,
        ulong instanceCount,
        IGraphicsShaderProgram program)
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

        pass.SetRange(firstOrderIndex, visualCount, instanceCount, program);
        _visualSegmentPassCount++;
        return pass;
    }

    private UiTextPass GetTextPass(int firstRun, int runCount)
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

        pass.SetRange(firstRun, runCount);
        _textPassCount++;
        return pass;
    }

    private bool TryPrepareVisual(IRenderGraphBuilder graph, int orderIndex)
    {
        var draw = _order.RefAt(orderIndex);
        var visual = _visuals.RefAt(draw.Index);
        var program = ResolveVisualProgram(visual, out var shaderVisualKind);
        UiRectangleShaderKind shaderKind;
        ShaderBinding instanceBinding;
        uint instanceStride;
        uint pushConstantSize;
        uint pushConstantOffset;
        if (_hasPreparedVisualDescription &&
            ReferenceEquals(_preparedVisualProgram, program) &&
            _preparedVisualKind == shaderVisualKind)
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
        _visualInstanceCounts.RefAt(orderIndex) = UiVisualShaderContract.MaxInstanceCount(shaderKind);
        _visualFramePushConstantOffsets.RefAt(orderIndex) = pushConstantOffset;
        _visualImageTextures.RefAt(orderIndex) = null;
        _visualImageSamplers.RefAt(orderIndex) = default;
        _visualImageBindings.RefAt(orderIndex) = null;
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

            var stride = _visualInstanceStrides.RefAt(orderIndex);
            var offset = checked((int)byteCursor);
            var maxInstanceBytes = checked((int)stride * _visualInstanceCounts.RefAt(orderIndex));
            var end = checked(offset + maxInstanceBytes);
            EnsureCapacity(ref _visualInstanceBytes, end);
            var visual = _visuals.RefAt(_order.RefAt(orderIndex).Index);
            var clip = _commandClips.RefAt(orderIndex);
            var physicalVisual = _coordinates.ToPhysical(visual);
            var written = UiVisualShaderContract.PackInstances(
                _visualShaderKinds.RefAt(orderIndex),
                in physicalVisual,
                in clip,
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
            previousOrderIndex = orderIndex;
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

            var stride = _visualInstanceStrides.RefAt(orderIndex);
            var instanceCount = _visualInstanceCounts.RefAt(orderIndex);
            var offset = _visualInstanceOffsets.RefAt(orderIndex);
            var instanceBytes = checked((int)(stride * (uint)instanceCount));
            var visual = _visuals.RefAt(draw.Index);
            var clip = _commandClips.RefAt(orderIndex);
            var physicalVisual = _coordinates.ToPhysical(visual);
            var written = UiVisualShaderContract.PackInstances(
                _visualShaderKinds.RefAt(orderIndex),
                in physicalVisual,
                in clip,
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
        => ReferenceEquals(_visualPrograms.RefAt(firstOrderIndex), _visualPrograms.RefAt(nextOrderIndex)) &&
           _visualPushConstantSizes.RefAt(firstOrderIndex) == _visualPushConstantSizes.RefAt(nextOrderIndex) &&
           _visualInstanceStrides.RefAt(firstOrderIndex) == _visualInstanceStrides.RefAt(nextOrderIndex) &&
           _visualInstanceBindings.RefAt(firstOrderIndex) == _visualInstanceBindings.RefAt(nextOrderIndex);

    internal void RecordVisualSegment(IRasterCommandContext commands, int firstOrderIndex, int visualCount, ulong segmentInstanceCount)
    {
        commands.SetViewport(new RenderViewport(0, 0, _viewport.Width, _viewport.Height));
        commands.PushConstants(_visualFramePushConstants.AsSpan(
            0,
            checked((int)_visualPushConstantSizes.RefAt(firstOrderIndex))),
            _visualFramePushConstantOffsets.RefAt(firstOrderIndex));
        var stride = _visualInstanceStrides.RefAt(firstOrderIndex);

        if (_flatVisualInstanceBuffer)
        {
            commands.BindBuffer(_visualInstanceBindings.RefAt(firstOrderIndex), _visualInstanceGraphHandle, 0, checked((ulong)_visualInstanceByteCount));
        }
        else
        {
            commands.BindBuffer(
                _visualInstanceBindings.RefAt(firstOrderIndex),
                _visualInstanceGraphHandle,
                checked((ulong)_visualInstanceOffsets.RefAt(firstOrderIndex)),
                checked((ulong)stride * segmentInstanceCount));
        }

        if (UiVisualShaderContract.UsesShaderClip(_visualShaderKinds.RefAt(firstOrderIndex)))
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
            var firstInstance = _flatVisualInstanceBuffer
                ? checked((uint)((ulong)_visualInstanceOffsets.RefAt(firstOrderIndex) / stride))
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
            uint instanceCount = (uint)_visualInstanceCounts.RefAt(drawStart);
            while (drawEnd < segmentEnd && _commandClips.RefAt(drawEnd) == clip)
            {
                if (instanceCount == 0 && _visualInstanceCounts.RefAt(drawEnd) != 0)
                {
                    firstDrawOrder = drawEnd;
                }

                instanceCount = checked(instanceCount + (uint)_visualInstanceCounts.RefAt(drawEnd));
                drawEnd++;
            }

            if (instanceCount != 0)
            {
                commands.SetScissor(_coordinates.ToPhysical(clip));
                var firstInstance = _flatVisualInstanceBuffer
                    ? checked((uint)((ulong)_visualInstanceOffsets.RefAt(firstDrawOrder) / stride))
                    : checked((uint)(((ulong)_visualInstanceOffsets.RefAt(firstDrawOrder) - (ulong)_visualInstanceOffsets.RefAt(firstOrderIndex)) / stride));
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

    private IGraphicsShaderProgram ResolveVisualProgram(UiVisualDraw visual, out UiVisualKind shaderVisualKind)
    {
        shaderVisualKind = visual.Kind;
        if (visual.Kind == UiVisualKind.Custom && _registry.TryResolveVisualType(visual.VisualType, out var registered))
        {
            return registered ?? throw new InvalidOperationException("The visual registry returned a null program.");
        }

        if (visual.Kind == UiVisualKind.SolidRectangle && _solidVisualProgram is not null)
        {
            return _solidVisualProgram;
        }

        if ((visual.Kind == UiVisualKind.RoundedRectangle || visual.Kind == UiVisualKind.Border) &&
            visual.Paint.StrokeWidth == 0f &&
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
            !UiDisplayListGeometry.IsFinite(visual.Paint.StrokeColor) || !UiDisplayListGeometry.IsFinite(visual.Paint.CornerRadii) ||
            !float.IsFinite(visual.Paint.StrokeWidth) ||
            visual.Paint.Units is not (PaintUnits.Logical or PaintUnits.Device))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] contains non-finite geometry or paint.");
            return false;
        }

        if (visual.Kind == UiVisualKind.SolidRectangle &&
            (visual.Paint.StrokeWidth != 0 || !UiDisplayListGeometry.IsZero(visual.Paint.CornerRadii)))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] requests stroke or rounded geometry without a registered effect shader.");
            return false;
        }

        switch (visual.Kind)
        {
            case UiVisualKind.SolidRectangle:
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

        if (visual.Paint.StrokeWidth < 0)
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] has a negative stroke width.");
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

    private bool ValidateText(UiTextDraw text, int orderIndex, out PixelRect clip)
    {
        clip = default;
        if (!_clipResolver.TryGetClip(text.Clip, out clip, orderIndex))
        {
            return false;
        }

        if (text.Text is null || !UiDisplayListGeometry.IsFinite(text.BaselineOrigin) || !UiDisplayListGeometry.IsFinite(text.Paint.FillColor) ||
            !UiDisplayListGeometry.IsFinite(text.Paint.OutlineColor) || !float.IsFinite(text.Paint.OutlineWidth) ||
            text.Paint.Units is not (PaintUnits.Logical or PaintUnits.Device))
        {
            AddDiagnostic($"Text at Order[{orderIndex}] contains an invalid shaped value or paint.");
            return false;
        }

        if (text.Paint.OutlineWidth != 0 || text.Paint.Effect.IsValid)
        {
            AddDiagnostic($"Text at Order[{orderIndex}] requests outline/effect data without a registered text effect shader.");
            return false;
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
        for (var index = 0; index < displayList.Visuals.Length; index++)
        {
            var previous = _visuals.RefAt(index);
            var current = displayList.Visuals[index];
            if (!HasStableVisualInstanceLayout(in previous, in current))
            {
                return false;
            }

            _visualPayloadDirtyByIndex.RefAt(index) = !previous.Equals(current);
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
               previous.Paint.StrokeWidth == current.Paint.StrokeWidth;
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
                StrokeWidth = paint.Units switch
                {
                    PaintUnits.Logical => paint.StrokeWidth * _dpiScale,
                    PaintUnits.Device => paint.StrokeWidth,
                    _ => throw new ArgumentOutOfRangeException(nameof(visual), paint.Units, "Unknown paint unit system."),
                },
                CornerRadii = ToPhysical(paint.CornerRadii),
                Units = PaintUnits.Device,
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
