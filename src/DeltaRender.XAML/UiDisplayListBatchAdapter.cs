using Delta.Render;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>
/// Converts one borrowed XAML display list into the existing renderer-owned UI
/// batch. The adapter never stores the display list or takes ownership of XAML,
/// shaped text, resource identities, or source payloads.
/// </summary>
/// <remarks>
/// Text records must be prepared by the caller's DeltaText/render integration and
/// correspond to <see cref="UiDisplayList.Text"/> by index. This adapter does not
/// accept strings, shape text, resolve fonts, or allocate atlas resources. The
/// borrowed display list is valid only for the duration of <see cref="TryReplace"/>.
/// Built-in solid, rounded, and border visuals map to the current rectangle draw
/// contract. Image and custom visuals return <see langword="false"/> because the
/// current neutral batch has no lossless XAML resource/visual resolver.
/// </remarks>
public sealed class UiDisplayListBatchAdapter : IDisposable
{
    private readonly UiRenderBatchAdapter _batch = new();
    private UiQuad[] _rectangles = [];
    private TextSubmissionRecord[] _textSubmissions = [];
    private UiRenderClipEntry[] _clips = [];
    private int _rectangleCount;
    private int _textSubmissionCount;
    private int _clipCount;
    private bool _disposed;

    /// <summary>
    /// Replaces the adapter-owned batch from one borrowed display list. A failed
    /// conversion leaves the previously published batch and token unchanged.
    /// </summary>
    public bool TryReplace(
        in UiDisplayList displayList,
        ReadOnlySpan<TextSubmissionRecord> preparedTextSubmissions,
        ReadOnlySpan<RenderRecordChange> dirtyRecords,
        in UiRenderDrawDelta delta,
        out UiRenderFrameToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token = default;
        if (preparedTextSubmissions.Length != displayList.Text.Length)
        {
            return false;
        }

        var visuals = displayList.Visuals;
        var sourceClips = displayList.Clips;
        EnsureCapacity(ref _rectangles, visuals.Length);
        EnsureCapacity(ref _textSubmissions, preparedTextSubmissions.Length);
        EnsureCapacity(ref _clips, sourceClips.Length);

        if (!TryBuildClipTable(sourceClips))
        {
            return false;
        }

        var rectangleCount = 0;
        for (var index = 0; index < visuals.Length; index++)
        {
            var visual = visuals[index];
            if (!TryMapVisual(visual.Kind, visual.Resource, visual.VisualType, out var kind))
            {
                return false;
            }

            if (!TryResolveClip(sourceClips, visual.Clip, out var clip, out var clipId))
            {
                return false;
            }

            if (!clip.IsValid)
            {
                continue;
            }

            var bounds = visual.Bounds;
            var color = visual.Color;
            var quad = new UiQuad(
                bounds.x,
                bounds.y,
                bounds.z,
                bounds.w,
                color.x,
                color.y,
                color.z,
                color.w)
            {
                Kind = kind,
                Clip = clip,
                ClipId = clipId,
                Order = checked((uint)index)
            };
            if (!quad.IsValid)
            {
                return false;
            }

            _rectangles[rectangleCount++] = quad;
        }

        var textSubmissionCount = 0;
        for (var index = 0; index < preparedTextSubmissions.Length; index++)
        {
            var request = displayList.Text[index];
            var source = preparedTextSubmissions[index];
            if (!source.IsValid || !TryResolveClip(sourceClips, request.Clip, out var clip, out _))
            {
                return false;
            }

            var effectiveClip = Intersect(source.Clip, clip);
            if (!effectiveClip.IsValid)
            {
                continue;
            }

            _textSubmissions[textSubmissionCount++] = source with { Clip = effectiveClip };
        }

        ClearTail(_textSubmissions, textSubmissionCount, _textSubmissionCount);
        _rectangleCount = rectangleCount;
        _textSubmissionCount = textSubmissionCount;

        token = _batch.Replace(
            _rectangles.AsSpan(0, rectangleCount),
            _textSubmissions.AsSpan(0, textSubmissionCount),
            dirtyRecords,
            _clips.AsSpan(0, _clipCount),
            in delta);
        return true;
    }

    /// <summary>Returns the current borrowed batch until the next successful replace or dispose.</summary>
    public UiRenderBatch Borrow(in UiRenderFrameToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _batch.Borrow(in token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _batch.Dispose();
        Array.Clear(_rectangles);
        Array.Clear(_textSubmissions);
        Array.Clear(_clips);
        _rectangles = [];
        _textSubmissions = [];
        _clips = [];
        _rectangleCount = 0;
        _textSubmissionCount = 0;
        _clipCount = 0;
        _disposed = true;
    }

    private bool TryBuildClipTable(ReadOnlySpan<UiClip> sourceClips)
    {
        for (var index = 0; index < sourceClips.Length; index++)
        {
            var source = sourceClips[index];
            var bounds = ToClipRect(source.Bounds);
            if (!bounds.IsValid || (source.Parent.IsValid && source.Parent.Value >= sourceClips.Length))
            {
                return false;
            }

            _clips[index] = new UiRenderClipEntry(
                new UiRenderClipId(checked((uint)index + 1)),
                bounds,
                ToRenderClipId(source.Parent));
        }

        _clipCount = sourceClips.Length;
        for (var index = 0; index < sourceClips.Length; index++)
        {
            if (!TryResolveClip(sourceClips, new UiClipId(index), out _, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMapVisual(
        UiVisualKind kind,
        UiResourceId resource,
        UiVisualTypeId visualType,
        out UiRenderDrawKind drawKind)
    {
        drawKind = UiRenderDrawKind.Rectangle;
        if (resource.IsValid || visualType.IsValid)
        {
            return false;
        }

        return kind is UiVisualKind.SolidRectangle or UiVisualKind.RoundedRectangle or UiVisualKind.Border;
    }

    private static bool TryResolveClip(
        ReadOnlySpan<UiClip> sourceClips,
        UiClipId sourceId,
        out UiClipRect effective,
        out UiRenderClipId renderId)
    {
        effective = UiClipRect.Unbounded;
        renderId = ToRenderClipId(sourceId);
        var current = sourceId;
        var steps = 0;
        while (current.IsValid)
        {
            if (current.Value >= sourceClips.Length || steps++ >= sourceClips.Length)
            {
                effective = default;
                renderId = default;
                return false;
            }

            var source = sourceClips[current.Value];
            var bounds = ToClipRect(source.Bounds);
            if (!bounds.IsValid)
            {
                effective = default;
                renderId = default;
                return false;
            }

            effective = Intersect(effective, bounds);
            current = source.Parent;
        }

        return true;
    }

    private static UiRenderClipId ToRenderClipId(UiClipId sourceId) =>
        sourceId.IsValid ? new UiRenderClipId(checked((uint)sourceId.Value + 1)) : default;

    private static UiClipRect ToClipRect(Delta.Maths.float4 bounds) =>
        new(bounds.x, bounds.y, bounds.z, bounds.w);

    private static UiClipRect Intersect(UiClipRect left, UiClipRect right)
    {
        if (left.IsUnbounded)
        {
            return right;
        }

        if (right.IsUnbounded)
        {
            return left;
        }

        var x = MathF.Max(left.X, right.X);
        var y = MathF.Max(left.Y, right.Y);
        var rightEdge = MathF.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = MathF.Min(left.Y + left.Height, right.Y + right.Height);
        return new UiClipRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (storage.Length >= required)
        {
            return;
        }

        var capacity = Math.Max(4, storage.Length);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        storage = new T[capacity];
    }

    private static void ClearTail<T>(T[] storage, int newCount, int oldCount)
    {
        if (newCount < oldCount)
        {
            Array.Clear(storage, newCount, oldCount - newCount);
        }
    }
}
