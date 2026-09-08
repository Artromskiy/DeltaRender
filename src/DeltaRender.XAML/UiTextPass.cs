using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Shader.Contract;

namespace Delta.Render.XAML;

internal sealed class UiTextPass(TextRenderFeature feature) : IRasterPass
{
    private RasterPassDescription _description = new("DeltaRender.XAML.Text", feature.CompositePipeline);
    private IGraphicsShaderProgram _shaderProgram = feature.CompositePipeline.ShaderProgram;
    private int _firstRun;
    private int _runCount;

    internal RasterPassDescription Description => _description;

    internal bool SetRange(int firstRun, int runCount)
    {
        if (!feature.TryGetCompositePipeline(firstRun, runCount, out var pipeline))
        {
            return false;
        }

        if (!ReferenceEquals(_shaderProgram, pipeline.ShaderProgram))
        {
            _shaderProgram = pipeline.ShaderProgram;
            _description = new RasterPassDescription("DeltaRender.XAML.Text", pipeline);
        }

        _firstRun = firstRun;
        _runCount = runCount;
        return true;
    }

    public void Record(IRasterCommandContext commands)
        => feature.RecordCompositeRuns(commands, _firstRun, _runCount);
}
