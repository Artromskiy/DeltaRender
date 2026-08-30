using System.Numerics;
using Delta.Maths;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Shader.Contract;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>
/// Renderer-owned registry for semantic UI visual and resource identities.
/// Registered handles remain owned by the caller's render session.
/// </summary>
public sealed class UiDisplayListResourceRegistry
{
    private readonly Dictionary<UiVisualTypeId, IGraphicsShaderProgram> _visualTypes = [];
    private readonly Dictionary<UiResourceId, ImageRegistration> _images = [];

    /// <summary>Associates a custom visual identity with its graphics program.</summary>
    public void RegisterVisualType(UiVisualTypeId type, IGraphicsShaderProgram program)
    {
        if (!type.IsValid)
        {
            throw new ArgumentException("A custom visual type must have a non-empty identity.", nameof(type));
        }

        ArgumentNullException.ThrowIfNull(program);
        _visualTypes[type] = program;
    }

    /// <summary>Associates an image identity with session-owned texture resources.</summary>
    public void RegisterImage(
        UiResourceId resource,
        RenderTextureHandle texture,
        RenderSamplerHandle sampler,
        ShaderBinding? fragmentBinding = null)
    {
        if (!resource.IsValid)
        {
            throw new ArgumentException("An image resource must have a non-empty identity.", nameof(resource));
        }

        if (!texture.IsValid || !sampler.IsValid)
        {
            throw new ArgumentException("An image resource requires valid session-owned texture and sampler handles.");
        }

        _images[resource] = new ImageRegistration(texture, sampler, fragmentBinding);
    }

    /// <summary>Removes one semantic visual type without releasing its shader program.</summary>
    public bool UnregisterVisualType(UiVisualTypeId type) => _visualTypes.Remove(type);

    /// <summary>Removes one image identity without releasing its session-owned resources.</summary>
    public bool UnregisterImage(UiResourceId resource) => _images.Remove(resource);

    /// <summary>Removes registrations; resource and shader ownership remains external.</summary>
    public void Clear()
    {
        _visualTypes.Clear();
        _images.Clear();
    }

    internal bool TryResolveVisualType(UiVisualTypeId type, out IGraphicsShaderProgram? program)
        => _visualTypes.TryGetValue(type, out program);

    internal bool TryResolveImage(
        UiResourceId resource,
        out RenderTextureHandle texture,
        out RenderSamplerHandle sampler,
        out ShaderBinding? fragmentBinding)
    {
        if (_images.TryGetValue(resource, out var registration))
        {
            texture = registration.Texture;
            sampler = registration.Sampler;
            fragmentBinding = registration.FragmentBinding;
            return true;
        }

        texture = default;
        sampler = default;
        fragmentBinding = null;
        return false;
    }

    private readonly record struct ImageRegistration(
        RenderTextureHandle Texture,
        RenderSamplerHandle Sampler,
        ShaderBinding? FragmentBinding);
}

/// <summary>
/// Synchronously consumes one borrowed DeltaXAML display list and exposes it as
/// renderer-owned graph work. The input spans are copied during <see cref="Consume"/>
/// and are never retained. The borrowed view is valid only until the next consume or
/// disposal; the feature itself owns the reusable backing storage.
/// </summary>
public sealed class UiDisplayListGraphFeature : IRenderFeature, IDisposable
{
    private readonly IRenderFrameSession? _session;
    private readonly IGraphicsShaderProgram? _defaultVisualProgram;
    private readonly UiDisplayListResourceRegistry _registry;
    private readonly TextRenderFeature? _textFeature;
    private readonly PixelExtent _viewport;
    private readonly List<string> _diagnostics = [];

    private UiVisualDraw[] _visuals = [];
    private UiClipRegion[] _clips = [];
    private UiTextDraw[] _texts = [];
    private UiDrawRef[] _order = [];
    private PixelRect[] _resolvedClips = [];
    private PixelRect[] _commandClips = [];
    private byte[] _visualPushConstants = [];
    private IGraphicsShaderProgram?[] _visualPrograms = [];
    private uint[] _visualPushConstantSizes = [];
    private RenderGraphTextureHandle?[] _visualImageTextures = [];
    private RenderSamplerHandle[] _visualImageSamplers = [];
    private ShaderBinding?[] _visualImageBindings = [];
    private int[] _clipMarks = [];
    private bool[] _seenVisuals = [];
    private bool[] _seenTexts = [];
    private int[] _textRunIndices = [];
    private int _visualCount;
    private int _clipCount;
    private int _textCount;
    private int _orderCount;
    private int _clipMarkEpoch;
    private bool _hasFrame;
    private bool _disposed;

    /// <summary>Creates a headless planning adapter without a graph submission owner.</summary>
    public UiDisplayListGraphFeature(PixelExtent viewport)
        : this(null, null, viewport, null, null, false)
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
        TextRenderFeature? textFeature = null)
        : this(session, visualProgram, viewport, registry, textFeature, true)
    {
    }

    private UiDisplayListGraphFeature(
        IRenderFrameSession? session,
        IGraphicsShaderProgram? visualProgram,
        PixelExtent viewport,
        UiDisplayListResourceRegistry? registry,
        TextRenderFeature? textFeature,
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
        _viewport = viewport;
        _registry = registry ?? new UiDisplayListResourceRegistry();
        _textFeature = textFeature;
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

        return _commandClips[orderIndex];
    }

    /// <summary>
    /// Copies and validates one borrowed display list synchronously. Rectangular parent
    /// chains are resolved to pixel scissors. Rounded/stroke/gradient forms are rejected
    /// with diagnostics until their shader artifacts are registered for this adapter.
    /// </summary>
    public bool Consume(UiDisplayList displayList)
    {
        ThrowIfDisposed();
        _diagnostics.Clear();
        ClearFrameStorage();

        EnsureCapacity(ref _visuals, displayList.Visuals.Length);
        EnsureCapacity(ref _clips, displayList.Clips.Length);
        EnsureCapacity(ref _texts, displayList.Text.Length);
        EnsureCapacity(ref _order, displayList.Order.Length);
        EnsureCapacity(ref _resolvedClips, displayList.Clips.Length);
        EnsureCapacity(ref _commandClips, displayList.Order.Length);
        EnsureCapacity(ref _clipMarks, displayList.Clips.Length);
        EnsureCapacity(ref _seenVisuals, displayList.Visuals.Length);
        EnsureCapacity(ref _seenTexts, displayList.Text.Length);
        EnsureCapacity(ref _textRunIndices, displayList.Order.Length);
        EnsureCapacity(ref _visualPrograms, displayList.Order.Length);
        EnsureCapacity(ref _visualPushConstantSizes, displayList.Order.Length);
        EnsureCapacity(ref _visualImageTextures, displayList.Order.Length);
        EnsureCapacity(ref _visualImageSamplers, displayList.Order.Length);
        EnsureCapacity(ref _visualImageBindings, displayList.Order.Length);

        displayList.Visuals.CopyTo(_visuals);
        displayList.Clips.CopyTo(_clips);
        displayList.Text.CopyTo(_texts);
        displayList.Order.CopyTo(_order);
        _visualCount = displayList.Visuals.Length;
        _clipCount = displayList.Clips.Length;
        _textCount = displayList.Text.Length;
        _orderCount = displayList.Order.Length;

        for (var index = 0; index < _clipCount; index++)
        {
            if (!TryResolveClip(new UiClipId(index), out _resolvedClips[index]))
            {
                ClearFrameStorage();
                return false;
            }
        }

        for (var index = 0; index < _orderCount; index++)
        {
            var draw = _order[index];
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

                if (_seenVisuals[draw.Index])
                {
                    AddDiagnostic($"Order[{index}] references visual {draw.Index} more than once.");
                    continue;
                }

                _seenVisuals[draw.Index] = true;
                if (!ValidateVisual(_visuals[draw.Index], index, out var clip))
                {
                    continue;
                }

                _commandClips[index] = clip;
            }
            else
            {
                if ((uint)draw.Index >= (uint)_textCount)
                {
                    AddDiagnostic($"Order[{index}] references missing text {draw.Index}.");
                    continue;
                }

                if (_seenTexts[draw.Index])
                {
                    AddDiagnostic($"Order[{index}] references text {draw.Index} more than once.");
                    continue;
                }

                _seenTexts[draw.Index] = true;
                if (!ValidateText(_texts[draw.Index], index, out var clip))
                {
                    continue;
                }

                _commandClips[index] = clip;
            }
        }

        for (var index = 0; index < _visualCount; index++)
        {
            if (!_seenVisuals[index])
            {
                AddDiagnostic($"Visual {index} is not present in the canonical Order span.");
            }
        }

        for (var index = 0; index < _textCount; index++)
        {
            if (!_seenTexts[index])
            {
                AddDiagnostic($"Text {index} is not present in the canonical Order span.");
            }
        }

        _hasFrame = _diagnostics.Count == 0;
        if (!_hasFrame)
        {
            ClearFrameStorage();
        }

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

        EnsureCapacity(ref _visualPushConstants, checked(_orderCount * UiVisualShaderContract.MaxPushConstantSize));
        var target = graph.ImportTarget(_session.Target);
        var textPrepared = false;
        if (_textCount != 0)
        {
            if (_textFeature is null)
            {
                AddDiagnostic("Text payloads require a caller-owned DeltaRender.Text adapter.");
                return;
            }

            var textRunCount = 0;
            var previousWasText = false;
            for (var index = 0; index < _orderCount; index++)
            {
                var draw = _order[index];
                if (draw.Kind != UiDrawKind.Text)
                {
                    previousWasText = false;
                    continue;
                }

                var text = _texts[draw.Index];
                var color = text.Paint.FillColor;
                _textRunIndices[index] = _textFeature.QueueCompositeRun(
                    text.Text,
                    text.BaselineOrigin.x,
                    text.BaselineOrigin.y,
                    new Vector4(color.x, color.y, color.z, color.w),
                    _commandClips[index],
                    mergeWithPrevious: previousWasText,
                    producerRunId: text.RunId.Value,
                    producerRunGeneration: text.RunId.Generation,
                    producerRunVersion: text.Version);
                textRunCount++;
                previousWasText = true;
            }

            textPrepared = textRunCount != 0 && _textFeature.PrepareComposite(graph);
        }

        for (var index = 0; index < _orderCount; index++)
        {
            if (_order[index].Kind == UiDrawKind.Visual && !_commandClips[index].IsEmpty)
            {
                TryPrepareVisual(graph, index);
            }
        }

        for (var index = 0; index < _orderCount; index++)
        {
            var draw = _order[index];
            if (draw.Kind == UiDrawKind.Text)
            {
                if (!textPrepared || _textFeature is null)
                {
                    continue;
                }

                var end = index + 1;
                while (end < _orderCount && _order[end].Kind == UiDrawKind.Text)
                {
                    end++;
                }

                var firstRun = _textRunIndices[index];
                var lastRun = _textRunIndices[end - 1];
                var textPass = graph.AddRasterPass(
                    new RasterPassDescription(
                        "DeltaRender.XAML.Text",
                        _textFeature.CompositePipeline),
                    new UiTextPass(_textFeature, firstRun, checked(lastRun - firstRun + 1)));
                graph.UseColorAttachment(
                    textPass,
                    0,
                    new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
                _textFeature.ConfigureCompositePass(graph, textPass);
                index = end - 1;
                continue;
            }

            var clip = _commandClips[index];
            if (clip.IsEmpty)
            {
                continue;
            }

            if (_visualPrograms[index] is not { } program)
            {
                continue;
            }

            var visualEnd = index + 1;
            while (visualEnd < _orderCount &&
                   _order[visualEnd].Kind == UiDrawKind.Visual &&
                   !_commandClips[visualEnd].IsEmpty &&
                   CanJoinVisualSegment(index, visualEnd))
            {
                visualEnd++;
            }

            var pass = graph.AddRasterPass(
                new RasterPassDescription(
                    $"DeltaRender.XAML.VisualSegment[{index}:{visualEnd})",
                    new RasterPipelineDescription(program, cullMode: RasterCullMode.None, blendMode: RenderBlendMode.Alpha)),
                new UiVisualSegmentPass(this, index, visualEnd - index));
            graph.UseColorAttachment(
                pass,
                0,
                new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));

            for (var visualIndex = index; visualIndex < visualEnd; visualIndex++)
            {
                if (_visualImageTextures[visualIndex].HasValue)
                {
                    var graphTexture = _visualImageTextures[visualIndex] ?? throw new InvalidOperationException("The image graph resource was not imported.");
                    graph.UseTexture(pass, graphTexture, RenderResourceAccess.Read, RenderPipelineStages.Fragment);
                }
            }

            index = visualEnd - 1;
        }
    }

    private bool TryPrepareVisual(IRenderGraphBuilder graph, int orderIndex)
    {
        var draw = _order[orderIndex];
        var visual = _visuals[draw.Index];
        var program = ResolveVisualProgram(visual);
        if (!UiVisualShaderContract.TryDescribe(
                program,
                visual.Kind,
                out var shaderKind,
                out var pushConstantSize,
                out var shaderDiagnostic))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] is unsupported: {shaderDiagnostic}");
            return false;
        }

        var parameterOffset = checked(orderIndex * UiVisualShaderContract.MaxPushConstantSize);
        var packedSize = UiVisualShaderContract.Pack(
            shaderKind,
            in visual,
            _viewport,
            _visualPushConstants.AsSpan(parameterOffset, checked((int)pushConstantSize)));
        if (packedSize != pushConstantSize)
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] generated UI packer wrote {packedSize} bytes; expected {pushConstantSize}.");
            return false;
        }

        _visualPrograms[orderIndex] = program;
        _visualPushConstantSizes[orderIndex] = pushConstantSize;
        _visualImageTextures[orderIndex] = null;
        _visualImageSamplers[orderIndex] = default;
        _visualImageBindings[orderIndex] = null;
        if (visual.Kind == UiVisualKind.Image)
        {
            if (!_registry.TryResolveImage(visual.Resource, out var imageTexture, out var imageSampler, out var imageBinding) || !imageBinding.HasValue)
            {
                AddDiagnostic($"Image resource {visual.Resource.Value} is not registered with a fragment binding.");
                _visualPrograms[orderIndex] = null;
                return false;
            }

            _visualImageTextures[orderIndex] = graph.ImportTexture(imageTexture);
            _visualImageSamplers[orderIndex] = imageSampler;
            _visualImageBindings[orderIndex] = imageBinding;
        }

        return true;
    }

    private bool CanJoinVisualSegment(int firstOrderIndex, int nextOrderIndex)
        => ReferenceEquals(_visualPrograms[firstOrderIndex], _visualPrograms[nextOrderIndex]) &&
           _visualPushConstantSizes[firstOrderIndex] == _visualPushConstantSizes[nextOrderIndex];

    private void RecordVisualSegment(IRasterCommandContext commands, int firstOrderIndex, int visualCount)
    {
        commands.SetViewport(new RenderViewport(0, 0, _viewport.Width, _viewport.Height));
        var end = checked(firstOrderIndex + visualCount);
        for (var orderIndex = firstOrderIndex; orderIndex < end; orderIndex++)
        {
            if (_order[orderIndex].Kind != UiDrawKind.Visual || _commandClips[orderIndex].IsEmpty)
            {
                continue;
            }

            commands.SetScissor(_commandClips[orderIndex]);
            var parameterOffset = checked(orderIndex * UiVisualShaderContract.MaxPushConstantSize);
            commands.PushConstants(_visualPushConstants.AsSpan(
                parameterOffset,
                checked((int)_visualPushConstantSizes[orderIndex])));
            var imageTexture = _visualImageTextures[orderIndex];
            var imageBinding = _visualImageBindings[orderIndex];
            if (imageTexture.HasValue && imageBinding is { } binding)
            {
                commands.BindTexture(
                    binding,
                    imageTexture.Value,
                    _visualImageSamplers[orderIndex]);
            }

            commands.Draw(6);
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
        _disposed = true;
    }

    private IGraphicsShaderProgram ResolveVisualProgram(UiVisualDraw visual)
    {
        if (visual.Kind == UiVisualKind.Custom && _registry.TryResolveVisualType(visual.VisualType, out var registered))
        {
            return registered ?? throw new InvalidOperationException("The visual registry returned a null program.");
        }

        return _defaultVisualProgram ?? throw new InvalidOperationException("The visual shader program is not configured.");
    }

    private bool ValidateVisual(UiVisualDraw visual, int orderIndex, out PixelRect clip)
    {
        clip = default;
        if (!TryGetClip(visual.Clip, out clip, orderIndex))
        {
            return false;
        }

        if (!IsFinite(visual.Bounds) || !IsFinite(visual.Paint.FillColor) ||
            !IsFinite(visual.Paint.StrokeColor) || !IsFinite(visual.Paint.CornerRadii) ||
            !float.IsFinite(visual.Paint.StrokeWidth))
        {
            AddDiagnostic($"Visual at Order[{orderIndex}] contains non-finite geometry or paint.");
            return false;
        }

        if (visual.Kind == UiVisualKind.SolidRectangle &&
            (visual.Paint.StrokeWidth != 0 || !IsZero(visual.Paint.CornerRadii)))
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
        if (!TryGetClip(text.Clip, out clip, orderIndex))
        {
            return false;
        }

        if (text.Text is null || !IsFinite(text.BaselineOrigin) || !IsFinite(text.Paint.FillColor) ||
            !IsFinite(text.Paint.OutlineColor) || !float.IsFinite(text.Paint.OutlineWidth))
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

    private bool TryGetClip(UiClipId id, out PixelRect clip, int orderIndex)
    {
        if (!id.IsValid)
        {
            clip = ViewportRect();
            return true;
        }

        if ((uint)id.Value >= (uint)_clipCount)
        {
            clip = default;
            AddDiagnostic($"Order[{orderIndex}] references missing clip {id.Value}.");
            return false;
        }

        clip = _resolvedClips[id.Value];
        return true;
    }

    private bool TryResolveClip(UiClipId id, out PixelRect result)
    {
        result = ViewportRect();
        var stamp = NextClipStamp();
        var current = id;
        while (current.IsValid)
        {
            if ((uint)current.Value >= (uint)_clipCount)
            {
                AddDiagnostic($"Clip {id.Value} has a missing parent/reference {current.Value}.");
                return false;
            }

            if (_clipMarks[current.Value] == stamp)
            {
                AddDiagnostic($"Clip {id.Value} contains a parent cycle.");
                return false;
            }

            _clipMarks[current.Value] = stamp;
            var region = _clips[current.Value];
            if (region.Kind != UiClipKind.Rectangle)
            {
                AddDiagnostic($"Clip {current.Value} uses unsupported kind {region.Kind}; only rectangular clips are currently accepted.");
                return false;
            }

            if (!TryConvertBounds(region.Bounds, out var local))
            {
                AddDiagnostic($"Clip {current.Value} has invalid bounds.");
                return false;
            }

            result = Intersect(result, local);
            current = region.Parent;
        }

        return true;
    }

    private int NextClipStamp()
    {
        if (_clipMarkEpoch == int.MaxValue)
        {
            Array.Clear(_clipMarks, 0, _clipCount);
            _clipMarkEpoch = 1;
        }
        else
        {
            _clipMarkEpoch++;
        }

        return _clipMarkEpoch;
    }

    private PixelRect ViewportRect()
    {
        var width = _viewport.Width > int.MaxValue ? int.MaxValue : (int)_viewport.Width;
        var height = _viewport.Height > int.MaxValue ? int.MaxValue : (int)_viewport.Height;
        return new PixelRect(0, 0, width, height);
    }

    private static bool TryConvertBounds(float4 bounds, out PixelRect result)
    {
        result = default;
        var right = (double)bounds.x + bounds.z;
        var bottom = (double)bounds.y + bounds.w;
        if (!IsFinite(bounds) || !double.IsFinite(right) || !double.IsFinite(bottom) || bounds.z <= 0 || bounds.w <= 0)
        {
            return false;
        }

        var leftValue = Math.Floor(bounds.x);
        var topValue = Math.Floor(bounds.y);
        var rightValue = Math.Ceiling(right);
        var bottomValue = Math.Ceiling(bottom);
        var left = ClampToInt(leftValue);
        var top = ClampToInt(topValue);
        var rightInt = ClampToInt(rightValue);
        var bottomInt = ClampToInt(bottomValue);
        var width = (long)rightInt - left;
        var height = (long)bottomInt - top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        result = new PixelRect(left, top, width > int.MaxValue ? int.MaxValue : (int)width, height > int.MaxValue ? int.MaxValue : (int)height);
        return true;
    }

    private static int ClampToInt(double value)
        => value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;

    private static PixelRect Intersect(PixelRect left, PixelRect right)
    {
        var x = Math.Max((long)left.X, right.X);
        var y = Math.Max((long)left.Y, right.Y);
        var rightEdge = Math.Min((long)left.X + left.Width, (long)right.X + right.Width);
        var bottomEdge = Math.Min((long)left.Y + left.Height, (long)right.Y + right.Height);
        if (rightEdge <= x || bottomEdge <= y)
        {
            return new PixelRect((int)x, (int)y, 0, 0);
        }

        return new PixelRect((int)x, (int)y, checked((int)(rightEdge - x)), checked((int)(bottomEdge - y)));
    }

    private void ClearFrameStorage()
    {
        Array.Clear(_visuals, 0, _visualCount);
        Array.Clear(_clips, 0, _clipCount);
        Array.Clear(_texts, 0, _textCount);
        Array.Clear(_order, 0, _orderCount);
        Array.Clear(_resolvedClips, 0, _clipCount);
        Array.Clear(_commandClips, 0, _orderCount);
        Array.Clear(_seenVisuals, 0, _visualCount);
        Array.Clear(_seenTexts, 0, _textCount);
        Array.Clear(_textRunIndices, 0, _orderCount);
        Array.Clear(_visualPrograms, 0, _visualCount);
        Array.Clear(_visualPushConstantSizes, 0, _visualCount);
        Array.Clear(_visualImageTextures, 0, _visualCount);
        Array.Clear(_visualImageSamplers, 0, _visualCount);
        Array.Clear(_visualImageBindings, 0, _visualCount);
        _visualCount = 0;
        _clipCount = 0;
        _textCount = 0;
        _orderCount = 0;
        _hasFrame = false;
    }

    private void AddDiagnostic(string message) => _diagnostics.Add(message);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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

    private static bool IsFinite(float2 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y);

    private static bool IsFinite(float4 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);

    private static bool IsZero(float4 value)
        => value.x == 0 && value.y == 0 && value.z == 0 && value.w == 0;

    private sealed class UiVisualSegmentPass(
        UiDisplayListGraphFeature owner,
        int firstOrderIndex,
        int visualCount) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
            => owner.RecordVisualSegment(commands, firstOrderIndex, visualCount);
    }

    private sealed class UiTextPass(TextRenderFeature feature, int firstRun, int runCount) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
            => feature.RecordCompositeRuns(commands, firstRun, runCount);
    }
}
