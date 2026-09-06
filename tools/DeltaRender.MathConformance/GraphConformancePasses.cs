using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal sealed class GraphConformanceFeature : IRenderFeature
{
    private readonly IShaderArtifact _artifact;
    private readonly IReadOnlyList<ShaderResourceBinding> _resources;
    private readonly RenderBufferHandle[] _buffers;
    private readonly byte[]?[] _uploads;
    private readonly int _caseCount;
    private readonly byte[] _pushConstants;
    private readonly uint _pushConstantOffset;

    internal GraphConformanceFeature(
        IShaderArtifact artifact,
        IReadOnlyList<ShaderResourceBinding> resources,
        RenderBufferHandle[] buffers,
        byte[]?[] uploads,
        int caseCount,
        byte[] pushConstants,
        uint pushConstantOffset)
    {
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _caseCount = caseCount;
        _pushConstants = pushConstants ?? throw new ArgumentNullException(nameof(pushConstants));
        _pushConstantOffset = pushConstantOffset;
    }

    internal RenderGraphReadbackHandle Readback { get; private set; }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        var graphBuffers = new RenderGraphBufferHandle[_buffers.Length];
        for (var index = 0; index < _buffers.Length; index++)
        {
            graphBuffers[index] = graph.ImportBuffer(_buffers[index]);
        }

        var transfer = graph.AddTransferPass("conformance-upload", new GraphTransferPass(graphBuffers, _uploads));
        for (var index = 0; index < _resources.Count; index++)
        {
            if (_uploads[index] is { Length: > 0 })
            {
                graph.UseBuffer(transfer, graphBuffers[index], RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }
        }

        var compute = graph.AddComputePass(
            new ComputePassDescription("conformance-compute", _artifact),
            new GraphComputePass(_artifact, _resources, graphBuffers, _caseCount, _pushConstants, _pushConstantOffset));
        for (var index = 0; index < _resources.Count; index++)
        {
            graph.UseBuffer(compute, graphBuffers[index], ToRenderAccess(_resources[index].Access), RenderPipelineStages.Compute);
        }

        var outputIndex = 0;
        for (var index = 0; index < _resources.Count; index++)
        {
            if (_resources[index].Access.HasFlag(ShaderResourceAccess.Write))
            {
                outputIndex = index;
                break;
            }
        }

        if (!_resources[outputIndex].Access.HasFlag(ShaderResourceAccess.Write))
        {
            throw new InvalidOperationException("The artifact must declare a writable return output storage buffer.");
        }

        var output = _resources[outputIndex].Layout.ArrayStride == 0 ? _resources[outputIndex].Layout.Size : _resources[outputIndex].Layout.ArrayStride;
        Readback = graph.ReadbackBuffer(graphBuffers[outputIndex], new BufferRange(0, checked(output * (ulong)_caseCount)));
    }

    private static RenderResourceAccess ToRenderAccess(ShaderResourceAccess access)
    {
        var result = RenderResourceAccess.None;
        if (access.HasFlag(ShaderResourceAccess.Read))
        {
            result |= RenderResourceAccess.Read;
        }

        if (access.HasFlag(ShaderResourceAccess.Write))
        {
            result |= RenderResourceAccess.Write;
        }

        return result;
    }
}

internal sealed class GraphTransferPass(RenderGraphBufferHandle[] buffers, byte[]?[] uploads) : ITransferPass
{
    public void Record(ITransferCommandContext commands)
    {
        for (var index = 0; index < buffers.Length; index++)
        {
            if (uploads[index] is { Length: > 0 } data)
            {
                commands.UploadBuffer(buffers[index], data);
            }
        }
    }
}

internal sealed class GraphComputePass(
    IShaderArtifact artifact,
    IReadOnlyList<ShaderResourceBinding> resources,
    RenderGraphBufferHandle[] buffers,
    int caseCount,
    byte[] pushConstants,
    uint pushConstantOffset) : IComputePass
{
    public void Record(IComputeCommandContext commands)
    {
        for (var index = 0; index < resources.Count; index++)
        {
            commands.BindBuffer(resources[index].Binding, buffers[index]);
        }

        commands.PushConstants(pushConstants, pushConstantOffset);
        var workgroupSize = artifact.Abi.WorkgroupSize.X;
        var groups = checked(((uint)caseCount + workgroupSize - 1) / workgroupSize);
        commands.Dispatch(groups);
    }
}
