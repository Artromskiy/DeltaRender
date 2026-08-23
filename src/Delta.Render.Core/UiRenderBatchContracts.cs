namespace Delta.Render.Core;

/// <summary>
/// A borrowed, frame-scoped view over the renderer-facing UI handoff.
/// </summary>
public readonly ref struct UiRenderBatch
{
    public UiRenderBatch(
        ReadOnlySpan<UiQuad> rectangles,
        ReadOnlySpan<TextSubmissionRecord> textSubmissions,
        ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        Rectangles = rectangles;
        TextSubmissions = textSubmissions;
        DirtyRecords = dirtyRecords;
    }

    public ReadOnlySpan<UiQuad> Rectangles { get; }

    public ReadOnlySpan<TextSubmissionRecord> TextSubmissions { get; }

    public ReadOnlySpan<RenderRecordChange> DirtyRecords { get; }

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
    private bool _disposed;

    public int RectangleCount => _rectangleCount;

    public int TextSubmissionCount => _textSubmissionCount;

    public int DirtyRecordCount => _dirtyRecordCount;

    public void Replace(
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
    }

    public UiRenderBatch Borrow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new UiRenderBatch(
            _rectangles.AsSpan(0, _rectangleCount),
            _textSubmissions.AsSpan(0, _textSubmissionCount),
            _dirtyRecords.AsSpan(0, _dirtyRecordCount));
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
