namespace DeltaRender;

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

        var textDrawList = new TextDrawList(orderedGlyphs.Slice(0, orderedCount));
        var packet = new RenderFramePacket(
            uiPipeline,
            uiParameters,
            uiDrawList,
            textPipeline,
            textParameters,
            atlasPages,
            textDrawList,
            dirtyRecords);
        return session.SubmitFrame(in packet);
    }
}
