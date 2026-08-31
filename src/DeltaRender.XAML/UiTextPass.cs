using Delta.Render.RenderGraph;
using Delta.Render.Text;

namespace Delta.Render.XAML;

internal sealed class UiTextPass(TextRenderFeature feature) : IRasterPass
{
    private readonly RasterPassDescription _description = new("DeltaRender.XAML.Text", feature.CompositePipeline);
    private int _firstRun;
    private int _runCount;

    internal RasterPassDescription Description => _description;

    internal void SetRange(int firstRun, int runCount)
    {
        _firstRun = firstRun;
        _runCount = runCount;
    }

    public void Record(IRasterCommandContext commands)
        => feature.RecordCompositeRuns(commands, _firstRun, _runCount);
}
