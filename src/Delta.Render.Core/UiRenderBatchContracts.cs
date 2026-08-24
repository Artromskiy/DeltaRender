namespace Delta.Render.Core;

/// <summary>
/// Identifies one borrowed UI frame owned by a <see cref="UiRenderBatchAdapter"/>.
/// A token becomes stale when the adapter is replaced or disposed.
/// </summary>
public readonly record struct UiRenderFrameToken(uint Generation)
{
    public bool IsValid => Generation != 0;
}

/// <summary>
/// A borrowed, frame-scoped view over the renderer-facing UI handoff. The view
/// is valid only for the call/frame represented by <see cref="Frame"/> and must
/// not be stored, returned, or used after the adapter is replaced or disposed.
/// </summary>
public readonly ref struct UiRenderBatch
{
    public UiRenderBatch(
        ReadOnlySpan<UiQuad> rectangles,
        ReadOnlySpan<TextSubmissionRecord> textSubmissions,
        ReadOnlySpan<RenderRecordChange> dirtyRecords,
        UiRenderFrameToken frame)
    {
        Rectangles = rectangles;
        TextSubmissions = textSubmissions;
        DirtyRecords = dirtyRecords;
        Frame = frame;
    }

    public ReadOnlySpan<UiQuad> Rectangles { get; }

    public ReadOnlySpan<TextSubmissionRecord> TextSubmissions { get; }

    public ReadOnlySpan<RenderRecordChange> DirtyRecords { get; }

    public UiRenderFrameToken Frame { get; }

    public bool IsEmpty => Rectangles.IsEmpty && TextSubmissions.IsEmpty && DirtyRecords.IsEmpty;
}

/// <summary>
/// Owns reusable renderer-facing UI arrays while producer adapters retain ownership
/// of their source models and payload memory.
/// </summary>
public sealed class UiRenderBatchAdapter : IDisposable
{
    private UiQuad[] _rectangles = [];
    private TextSubmissionRecord[] _textSubmissions = [];
    private RenderRecordChange[] _dirtyRecords = [];
    private int _rectangleCount;
    private int _textSubmissionCount;
    private int _dirtyRecordCount;
    private uint _generation;
    private bool _disposed;

    public int RectangleCount => _rectangleCount;

    public int TextSubmissionCount => _textSubmissionCount;

    public int DirtyRecordCount => _dirtyRecordCount;

    /// <summary>
    /// Replaces the borrowed frame contents and returns its lifetime token.
    /// Any previously borrowed batch is invalid after this method returns.
    /// </summary>
    public UiRenderFrameToken Replace(
        ReadOnlySpan<UiQuad> rectangles,
        ReadOnlySpan<TextSubmissionRecord> textSubmissions,
        ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rectangles = EnsureCapacity(_rectangles, rectangles.Length);
        _textSubmissions = EnsureCapacity(_textSubmissions, textSubmissions.Length);
        _dirtyRecords = EnsureCapacity(_dirtyRecords, dirtyRecords.Length);

        ClearTail(_rectangles, rectangles.Length, _rectangleCount);
        ClearTail(_textSubmissions, textSubmissions.Length, _textSubmissionCount);
        ClearTail(_dirtyRecords, dirtyRecords.Length, _dirtyRecordCount);
        rectangles.CopyTo(_rectangles);
        textSubmissions.CopyTo(_textSubmissions);
        dirtyRecords.CopyTo(_dirtyRecords);
        _rectangleCount = rectangles.Length;
        _textSubmissionCount = textSubmissions.Length;
        _dirtyRecordCount = dirtyRecords.Length;
        _generation = _generation == uint.MaxValue ? 1 : _generation + 1;
        return new UiRenderFrameToken(_generation);
    }

    /// <summary>
    /// Borrows the current frame until the adapter is replaced or disposed.
    /// </summary>
    public UiRenderBatch Borrow(in UiRenderFrameToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!token.IsValid || token.Generation != _generation)
        {
            throw new InvalidOperationException("The UI render frame token is stale or invalid.");
        }

        return new UiRenderBatch(
            _rectangles.AsSpan(0, _rectangleCount),
            _textSubmissions.AsSpan(0, _textSubmissionCount),
            _dirtyRecords.AsSpan(0, _dirtyRecordCount),
            token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_rectangles);
        Array.Clear(_textSubmissions);
        Array.Clear(_dirtyRecords);
        _rectangles = [];
        _textSubmissions = [];
        _dirtyRecords = [];
        _rectangleCount = 0;
        _textSubmissionCount = 0;
        _dirtyRecordCount = 0;
        _disposed = true;
    }

    private static T[] EnsureCapacity<T>(T[] storage, int required)
    {
        if (storage.Length >= required)
        {
            return storage;
        }

        var capacity = Math.Max(4, storage.Length);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return new T[capacity];
    }

    private static void ClearTail<T>(T[] storage, int newCount, int oldCount)
    {
        if (newCount < oldCount)
        {
            Array.Clear(storage, newCount, oldCount - newCount);
        }
    }
}

/// <summary>
/// Bridges a neutral UI batch to the existing renderer-owned combined submission path.
/// </summary>
public static class UiRenderBatchSubmission
{
    public static bool Submit(
        this IRenderWindowFrameSession session,
        IGraphicsPipeline uiPipeline,
        in GraphicsFrameParameters uiParameters,
        IGraphicsPipeline? textPipeline,
        in TextFrameParameters textParameters,
        ReadOnlySpan<ITextAtlasPage> atlasPages,
        in UiRenderBatch batch,
        in TextProjectionContext projectionContext,
        ITextWorldProjection? worldProjection,
        Span<TextGlyphInstance> orderedGlyphs,
        Span<TextBatchRange> textBatches)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(uiPipeline);
        var uiDrawList = new UiDrawList(batch.Rectangles);
        if (batch.TextSubmissions.IsEmpty)
        {
            return session.SubmitFrame(uiPipeline, in uiParameters, in uiDrawList, batch.DirtyRecords);
        }

        if (textPipeline is null)
        {
            return false;
        }

        var submission = new TextSubmissionFrame(batch.TextSubmissions);
        return session.SubmitTextSubmission(
            uiPipeline,
            in uiParameters,
            in uiDrawList,
            textPipeline,
            in textParameters,
            atlasPages,
            in submission,
            in projectionContext,
            worldProjection,
            orderedGlyphs,
            textBatches,
            batch.DirtyRecords);
    }
}
