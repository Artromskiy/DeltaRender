using Delta.Render.RenderGraph;
using Delta.Render.Text;

namespace Delta.Render.XAML;

internal sealed class UiTextPass(TextRenderFeature feature, int firstRun, int runCount) : IRasterPass
{
    public void Record(IRasterCommandContext commands)
        => feature.RecordCompositeRuns(commands, firstRun, runCount);
}
