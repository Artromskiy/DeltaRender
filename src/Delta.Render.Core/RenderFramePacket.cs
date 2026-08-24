namespace Delta.Render.Core;

public static class RenderFramePacketFactory
{
    public static RenderFramePacket ClearOnly(ReadOnlySpan<RenderRecordChange> dirtyRecords = default)
    {
        var uiDrawList = default(UiDrawList);
        var textDrawList = default(TextDrawList);
        return new RenderFramePacket(
            null,
            default,
            uiDrawList,
            null,
            default,
            ReadOnlySpan<ITextAtlasPage>.Empty,
            textDrawList,
            dirtyRecords);
    }
}
