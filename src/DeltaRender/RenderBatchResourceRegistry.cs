using System.Diagnostics.CodeAnalysis;
using Delta.Shader.Contract;

namespace Delta.Render.RenderGraph;

internal sealed class RenderBatchResourceRegistry : IDisposable
{
    private readonly IRenderFrameSession _session;
    private readonly ulong _alignment;
    private readonly List<RenderBatchPipelineState> _pipelines = [];
    private readonly List<RenderBatchMaterialState> _materials = [];
    private ulong _nextPipelineValue = 1;
    private ulong _nextMaterialValue = 1;
    private bool _disposed;

    internal RenderBatchResourceRegistry(IRenderFrameSession session)
    {
        _session = session;
        _alignment = Math.Max(1UL, session.Capabilities.MinStorageBufferOffsetAlignment);
    }

    internal int PipelineCount => _pipelines.Count;

    internal RenderBatchPipelineState GetPipeline(int index) => _pipelines[index];

    internal RenderBatchPipelineHandle RegisterPipeline(
        RasterPipelineDescription description,
        ShaderBinding instanceBinding,
        uint instanceStride,
        uint vertexCount)
    {
        var handle = new RenderBatchPipelineHandle(_nextPipelineValue++, 1);
        _pipelines.Add(new RenderBatchPipelineState(
            _session,
            handle,
            description,
            instanceBinding,
            instanceStride,
            vertexCount,
            _alignment));
        return handle;
    }

    internal RenderBatchMaterialHandle RegisterMaterial(ReadOnlySpan<byte> pushConstants)
    {
        var handle = new RenderBatchMaterialHandle(_nextMaterialValue++, 1);
        _materials.Add(new RenderBatchMaterialState(handle, pushConstants));
        return handle;
    }

    internal bool TryUpdateMaterial(
        RenderBatchMaterialHandle handle,
        RenderBatchVersion version,
        ReadOnlySpan<byte> pushConstants,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!version.IsValid)
        {
            diagnostic = "Material version must be non-zero.";
            return false;
        }

        if (!TryGetMaterial(handle, out var material))
        {
            diagnostic = $"Unknown or stale material handle {handle}.";
            return false;
        }

        if (version.Value <= material.Version.Value)
        {
            diagnostic = $"Material update {version.Value} is not newer than {material.Version.Value}.";
            return false;
        }

        material.Version = version;
        material.PushConstants = pushConstants.ToArray();
        return true;
    }

    internal bool TryGetPipeline(
        RenderBatchPipelineHandle handle,
        [NotNullWhen(true)] out RenderBatchPipelineState? pipeline)
    {
        var index = handle.Value == 0 || handle.Value > (ulong)_pipelines.Count
            ? -1
            : checked((int)handle.Value - 1);
        if (index >= 0 && _pipelines[index].Handle.Generation == handle.Generation)
        {
            pipeline = _pipelines[index];
            return true;
        }

        pipeline = null;
        return false;
    }

    internal bool TryGetMaterial(
        RenderBatchMaterialHandle handle,
        [NotNullWhen(true)] out RenderBatchMaterialState? material)
    {
        var index = handle.Value == 0 || handle.Value > (ulong)_materials.Count
            ? -1
            : checked((int)handle.Value - 1);
        if (index >= 0 && _materials[index].Handle.Generation == handle.Generation)
        {
            material = _materials[index];
            return true;
        }

        material = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (var index = 0; index < _pipelines.Count; index++)
        {
            _pipelines[index].Dispose();
        }

        _pipelines.Clear();
        _materials.Clear();
        _disposed = true;
    }
}

internal sealed class RenderBatchMaterialState
{
    internal RenderBatchMaterialState(
        RenderBatchMaterialHandle handle,
        ReadOnlySpan<byte> pushConstants)
    {
        Handle = handle;
        Version = new RenderBatchVersion(1);
        PushConstants = pushConstants.ToArray();
    }

    internal RenderBatchMaterialHandle Handle;
    internal RenderBatchVersion Version;
    internal byte[] PushConstants;
}
