namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchSegmentRasterPass(RenderBatchSegment segment, PixelExtent viewport) : IRasterPass
{
    public void Record(IRasterCommandContext commands)
    {
        commands.SetViewport(new RenderViewport(0, 0, viewport.Width, viewport.Height));
        commands.SetScissor(segment.Key.Clip);
        var material = segment.Material;
        if (material.PushConstants.Length != 0)
        {
            commands.PushConstants(material.PushConstants);
        }

        commands.BindBuffer(
            segment.Pipeline.InstanceBinding,
            segment.Pipeline.GraphBuffer,
            segment.GpuOffset,
            checked((ulong)segment.Count * segment.Pipeline.InstanceStride));
        commands.Draw(segment.Pipeline.VertexCount, checked((uint)segment.Count));
    }
}
