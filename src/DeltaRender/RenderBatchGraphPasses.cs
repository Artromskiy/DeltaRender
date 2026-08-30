namespace Delta.Render.RenderGraph;

internal static class RenderBatchGraphPasses
{
    internal static void AddRasterPass(
        IRenderGraphBuilder graph,
        RenderGraphTextureHandle target,
        RenderBatchSegment segment)
    {
        var pass = graph.AddRasterPass(segment.Description, segment.RasterPass);
        graph.UseColorAttachment(
            pass,
            0,
            new ColorAttachmentDescription(target, AttachmentLoadOperation.Load, AttachmentStoreOperation.Store));
        graph.UseBuffer(
            pass,
            segment.Pipeline.GraphBuffer,
            RenderResourceAccess.Read,
            RenderPipelineStages.Vertex);
    }
}
