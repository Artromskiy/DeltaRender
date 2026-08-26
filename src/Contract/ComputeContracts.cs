using Delta.Shader.Contract;

namespace Delta.Render.Core;

public enum ComputeBufferAccess
{
    ReadOnly = 0,
    WriteOnly = 1,
    ReadWrite = 2
}

public readonly record struct ComputeBufferBinding(
    uint Set,
    uint Binding,
    IComputeStorageBuffer Buffer);

public readonly record struct ComputeUploadRange(
    ulong DestinationOffset,
    ReadOnlyMemory<byte> Source);

public readonly record struct ComputeDeviceLimits(
    ulong MaxStorageBufferRange,
    ulong MinStorageBufferOffsetAlignment,
    ulong NonCoherentAtomSize,
    uint MaxComputeWorkGroupSizeX,
    uint MaxComputeWorkGroupCountX,
    uint MaxBoundDescriptorSets = 1);

public enum ComputeDispatchStatus
{
    Executed = 0,
    NoOp = 1,
    Invalid = 2
}

public readonly record struct ComputeDispatchResult(
    ComputeDispatchStatus Status,
    string? Error = null)
{
    public bool Succeeded => Status is ComputeDispatchStatus.Executed or ComputeDispatchStatus.NoOp;

    public static ComputeDispatchResult Executed() => new(ComputeDispatchStatus.Executed);

    public static ComputeDispatchResult NoOp() => new(ComputeDispatchStatus.NoOp);

    public static ComputeDispatchResult Invalid(string error) => new(ComputeDispatchStatus.Invalid, error);
}

public readonly record struct ComputeDirtyUpdateResult(
    int AcceptedRecords,
    int RejectedRecords,
    int UploadRuns,
    string? Error = null)
{
    public bool Succeeded => RejectedRecords == 0 && Error is null;

    public static ComputeDirtyUpdateResult Empty => new(0, 0, 0);
}

public interface IComputeStorageBuffer : IAsyncDisposable
{
    ulong ByteLength { get; }

    bool IsDeviceLocal { get; }

    ComputeBufferAccess DeclaredAccess { get; }
}

public interface IComputePipeline : IAsyncDisposable
{
    ShaderAbi Abi { get; }
}

public interface IComputeDevice : IAsyncDisposable
{
    ComputeDeviceLimits Limits { get; }

    IComputeStorageBuffer CreateStorageBuffer(ulong byteLength, ComputeBufferAccess declaredAccess = ComputeBufferAccess.ReadWrite);

    bool Upload(IComputeStorageBuffer destination, ReadOnlySpan<byte> source, ulong destinationOffset = 0);

    bool UploadRanges(IComputeStorageBuffer destination, ReadOnlySpan<ComputeUploadRange> ranges);

    bool Readback(IComputeStorageBuffer source, Span<byte> destination, ulong sourceOffset = 0);

    /// <summary>Low-level import for SPIR-V that is accompanied by its canonical ABI.</summary>
    IComputePipeline CreateComputePipeline(ReadOnlySpan<uint> spirvWords, ShaderAbi abi);

    /// <summary>Low-level import for SPIR-V that is accompanied by its canonical ABI.</summary>
    IComputePipeline CreateComputePipeline(ReadOnlySpan<byte> spirvBytes, ShaderAbi abi);

    IComputePipeline CreateComputePipeline(IShaderArtifact artifact);

    ComputeDispatchResult Dispatch(
        IComputePipeline pipeline,
        ReadOnlySpan<ComputeBufferBinding> bindings,
        uint groupCountX,
        uint groupCountY = 1,
        uint groupCountZ = 1);

    ComputeDirtyUpdateResult ApplyDirtyRecords(
        IComputeStorageBuffer destination,
        ReadOnlySpan<RenderRecordChange> dirtyRecords,
        uint recordStride,
        uint recordCapacity);
}
