namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchPipelineUploadPass(RenderBatchPipelineState pipeline) : ITransferPass
{
    public void Record(ITransferCommandContext commands)
        => pipeline.UploadDirtyRanges(commands);
}
