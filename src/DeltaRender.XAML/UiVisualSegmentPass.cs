using Delta.Render.RenderGraph;
using Delta.Shader.Contract;

namespace Delta.Render.XAML;

internal enum UiVisualRenderLayer : byte
{
    Base,
    Shadow,
    Glow,
}

internal sealed class UiVisualSegmentPass(UiDisplayListGraphFeature owner) : IRasterPass
{
    private RasterPipelineDescription? _pipeline;
    private RasterPassDescription? _description;
    private IGraphicsShaderProgram? _program;
    private RenderBlendState _blendState;
    private int _descriptionFirstOrderIndex = -1;
    private int _descriptionVisualCount = -1;
    private int _firstOrderIndex;
    private int _visualCount;
    private ulong _instanceCount;
    private UiVisualRenderLayer _layer;

    internal RasterPassDescription Description
        => _description ?? throw new InvalidOperationException("The visual segment description is not initialized.");

    internal void SetRange(
        int firstOrderIndex,
        int visualCount,
        ulong instanceCount,
        IGraphicsShaderProgram program,
        UiVisualRenderLayer layer,
        RenderBlendState blendState)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!ReferenceEquals(_program, program) || _blendState != blendState)
        {
            _program = program;
            _blendState = blendState;
            _pipeline = new RasterPipelineDescription(
                program,
                cullMode: RasterCullMode.None,
                blendState: blendState);
            _description = null;
        }

        if (_description is null ||
            _descriptionFirstOrderIndex != firstOrderIndex ||
            _descriptionVisualCount != visualCount)
        {
            _description = new RasterPassDescription(
                $"DeltaRender.XAML.VisualSegment.{layer}[{firstOrderIndex}:{checked(firstOrderIndex + visualCount)})",
                _pipeline ?? throw new InvalidOperationException("The visual segment pipeline is not initialized."));
            _descriptionFirstOrderIndex = firstOrderIndex;
            _descriptionVisualCount = visualCount;
        }

        _firstOrderIndex = firstOrderIndex;
        _visualCount = visualCount;
        _instanceCount = instanceCount;
        _layer = layer;
    }

    public void Record(IRasterCommandContext commands)
        => owner.RecordVisualSegment(commands, _firstOrderIndex, _visualCount, _instanceCount, _layer);
}
