using Delta.Shader.Contract;

namespace Delta.Render.RenderGraph;

/// <summary>
/// Renderer-owned persistent instance batcher. It applies producer changes to
/// ordered segments or unordered buckets, uploads only changed byte ranges and
/// records one instanced draw per active segment. It is neutral to ECS, XAML and
/// shader authoring; instance bytes and ABI metadata come from the caller.
/// </summary>
public sealed class RenderBatcher : IRenderFeature, IDisposable
{
    private readonly IRenderFrameSession _session;
    private readonly RenderBatchResourceRegistry _resources;
    private readonly RenderBatchLayout _layout;
    private readonly List<string> _diagnostics = [];
    private bool _disposed;

    public RenderBatcher(
        IRenderFrameSession session,
        PixelExtent viewport,
        RenderBatchOrderMode orderMode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (viewport.IsEmpty)
        {
            throw new ArgumentException("The batch viewport must be non-empty.", nameof(viewport));
        }

        _session = session;
        _resources = new RenderBatchResourceRegistry(session);
        _layout = new RenderBatchLayout(_resources, viewport, orderMode);
    }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public int ActiveItemCount => _layout.ActiveItemCount;

    public int ActiveSegmentCount => _layout.ActiveSegmentCount;

    public RenderBatchPipelineHandle RegisterPipeline(
        RasterPipelineDescription pipeline,
        ShaderBinding instanceBinding,
        uint instanceStride,
        uint vertexCount = 6)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfZero(instanceStride);
        ArgumentOutOfRangeException.ThrowIfZero(vertexCount);
        return _resources.RegisterPipeline(pipeline, instanceBinding, instanceStride, vertexCount);
    }

    public RenderBatchMaterialHandle RegisterMaterial(ReadOnlySpan<byte> pushConstants)
    {
        ThrowIfDisposed();
        return _resources.RegisterMaterial(pushConstants);
    }

    public bool TryUpdateMaterial(
        RenderBatchMaterialHandle handle,
        RenderBatchVersion version,
        ReadOnlySpan<byte> pushConstants,
        out string diagnostic)
    {
        ThrowIfDisposed();
        return _resources.TryUpdateMaterial(handle, version, pushConstants, out diagnostic);
    }

    public bool TryApply(in RenderBatchItemChange change, out string diagnostic)
    {
        ThrowIfDisposed();
        return _layout.TryApply(in change, out diagnostic);
    }

    public bool TryRemove(
        RenderBatchItemId id,
        RenderBatchVersion version,
        out string diagnostic)
    {
        ThrowIfDisposed();
        return _layout.TryRemove(id, version, out diagnostic);
    }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (ActiveItemCount == 0)
        {
            return;
        }

        var target = _session.Target;
        if (!target.IsValid)
        {
            _diagnostics.Add("Render batcher requires a valid session target.");
            return;
        }

        var graphTarget = graph.ImportTarget(target);
        for (var index = 0; index < _resources.PipelineCount; index++)
        {
            var pipeline = _resources.GetPipeline(index);
            if (!pipeline.HasActiveSegments)
            {
                continue;
            }

            pipeline.EnsureBuffer();
            pipeline.GraphBuffer = graph.ImportBuffer(pipeline.Buffer);
            if (pipeline.DirtyCount != 0)
            {
                var upload = graph.AddTransferPass(pipeline.UploadPassName, pipeline.UploadPass);
                graph.UseBuffer(upload, pipeline.GraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }
        }

        if (_layout.OrderMode == RenderBatchOrderMode.Ordered)
        {
            for (var index = 0; index < _layout.OrderedSegmentCount; index++)
            {
                RenderBatchGraphPasses.AddRasterPass(graph, graphTarget, _layout.GetOrderedSegment(index));
            }
        }
        else
        {
            for (var index = 0; index < _layout.UnorderedSegmentCount; index++)
            {
                RenderBatchGraphPasses.AddRasterPass(graph, graphTarget, _layout.GetUnorderedSegment(index));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _layout.Clear();
        _resources.Dispose();
        _diagnostics.Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
