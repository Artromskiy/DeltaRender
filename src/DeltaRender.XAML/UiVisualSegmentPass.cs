using Delta.Render.RenderGraph;

namespace Delta.Render.XAML;

internal sealed class UiVisualSegmentPass(UiDisplayListGraphFeature owner) : IRasterPass
{
    private int _firstOrderIndex;
    private int _visualCount;

    internal void SetRange(int firstOrderIndex, int visualCount)
    {
        _firstOrderIndex = firstOrderIndex;
        _visualCount = visualCount;
    }

    public void Record(IRasterCommandContext commands)
        => owner.RecordVisualSegment(commands, _firstOrderIndex, _visualCount);
}
