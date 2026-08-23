namespace Delta.Render.Core;

public static class TextSubmissionRendering
{
    // Renderer-neutral adapter seam: producers own the snapshot and reusable
    // output storage; the frame session receives the same UI+text submission
    // path as a directly-authored TextDrawList.
    public static bool SubmitTextSubmission(
        this IRenderWindowFrameSession session,
        IGraphicsPipeline uiPipeline,
        in GraphicsFrameParameters uiParameters,
        in UiDrawList uiDrawList,
        IGraphicsPipeline textPipeline,
        in TextFrameParameters textParameters,
        ReadOnlySpan<ITextAtlasPage> atlasPages,
        in TextSubmissionFrame submission,
        in TextProjectionContext projectionContext,
        ITextWorldProjection? worldProjection,
        Span<TextGlyphInstance> orderedGlyphs,
        Span<TextBatchRange> batches,
        ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TextSubmissionBatching.TryBuild(
                in submission,
                in projectionContext,
                worldProjection,
                orderedGlyphs,
                batches,
                out var orderedCount,
                out _,
                out _))
        {
            return false;
        }

        return session.SubmitFrame(
            uiPipeline,
            in uiParameters,
            in uiDrawList,
            textPipeline,
            in textParameters,
            atlasPages,
            new TextDrawList(orderedGlyphs.Slice(0, orderedCount)),
            dirtyRecords);
    }
}
