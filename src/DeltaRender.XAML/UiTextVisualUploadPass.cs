using Delta.Render.RenderGraph;

namespace Delta.Render.XAML;

internal sealed class UiTextVisualUploadPass(UiDisplayListGraphFeature owner) : ITransferPass
{
    public void Record(ITransferCommandContext commands)
        => owner.RecordTextVisualUpload(commands);
}
