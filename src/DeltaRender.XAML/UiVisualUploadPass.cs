using Delta.Render.RenderGraph;

namespace Delta.Render.XAML;

internal sealed class UiVisualUploadPass(UiDisplayListGraphFeature owner) : ITransferPass
{
    public void Record(ITransferCommandContext commands)
        => owner.RecordVisualUpload(commands);
}
