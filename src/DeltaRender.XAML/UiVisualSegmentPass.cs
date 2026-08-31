using Delta.Render.RenderGraph;

namespace Delta.Render.XAML;

internal sealed class UiVisualSegmentPass(UiDisplayListGraphFeature owner) : IRasterPass
{
    private int _firstOrderIndex;
    private int _visualCount;
    private ulong _instanceCount;

    internal void SetRange(int firstOrderIndex, int visualCount, ulong instanceCount)
    {
        _firstOrderIndex = firstOrderIndex;
        _visualCount = visualCount;
        _instanceCount = instanceCount;
    }

    public void Record(IRasterCommandContext commands)
        => owner.RecordVisualSegment(commands, _firstOrderIndex, _visualCount, _instanceCount);
}
