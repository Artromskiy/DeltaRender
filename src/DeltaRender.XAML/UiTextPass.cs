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
    private TextRenderLayer _layer;

    internal RasterPassDescription Description => _description;

    internal bool SetRange(int firstRun, int runCount, TextRenderLayer layer)
    {
        if (!feature.TryGetCompositePipeline(firstRun, runCount, layer, out var pipeline))
        {
            return false;
        }

        if (!ReferenceEquals(_shaderProgram, pipeline.ShaderProgram) || _layer != layer)
        {
            _shaderProgram = pipeline.ShaderProgram;
            _description = new RasterPassDescription(
                layer switch
                {
                    TextRenderLayer.Shadow => "DeltaRender.XAML.Text.Shadow",
                    TextRenderLayer.Glow => "DeltaRender.XAML.Text.Glow",
                    TextRenderLayer.InnerShadow => "DeltaRender.XAML.Text.InnerShadow",
                    TextRenderLayer.InnerGlow => "DeltaRender.XAML.Text.InnerGlow",
                    _ => "DeltaRender.XAML.Text.Base",
                },
                pipeline);
        }

        _firstRun = firstRun;
        _runCount = runCount;
        _layer = layer;
        return true;
    }

    public void Record(IRasterCommandContext commands)
        => feature.RecordCompositeRuns(commands, _firstRun, _runCount, _layer);
}
