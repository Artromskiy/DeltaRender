using Delta.Shader.Abstractions;

namespace Delta.Render.Core;

public enum ComputeAbiLayout : byte
{
    Std430 = 0
}

public enum ComputeDescriptorKind : byte
{
    StorageBuffer = 0
}

public enum ComputeBufferAccess : byte
{
    ReadOnly = 0,
    WriteOnly = 1,
    ReadWrite = 2
}

public readonly record struct ComputeDescriptorBinding(
    uint Set,
    uint Binding,
    ComputeDescriptorKind Kind,
    ComputeBufferAccess Access,
    uint ArrayCount = 1);

public readonly record struct ComputeShaderMetadata(
    ComputeAbiLayout AbiLayout,
    uint LocalSizeX,
    uint LocalSizeY,
    uint LocalSizeZ,
    ReadOnlyMemory<ComputeDescriptorBinding> Bindings);

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

public enum ComputeDispatchStatus : byte
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
    ComputeShaderMetadata Metadata { get; }
}

public interface IComputeDevice : IAsyncDisposable
{
    ComputeDeviceLimits Limits { get; }

    IComputeStorageBuffer CreateStorageBuffer(ulong byteLength, ComputeBufferAccess declaredAccess = ComputeBufferAccess.ReadWrite);

    bool Upload(IComputeStorageBuffer destination, ReadOnlySpan<byte> source, ulong destinationOffset = 0);

    bool UploadRanges(IComputeStorageBuffer destination, ReadOnlySpan<ComputeUploadRange> ranges);

    bool Readback(IComputeStorageBuffer source, Span<byte> destination, ulong sourceOffset = 0);

    IComputePipeline CreateComputePipeline(ReadOnlySpan<uint> spirvWords, in ComputeShaderMetadata metadata);

    IComputePipeline CreateComputePipeline(ReadOnlySpan<byte> spirvBytes, in ComputeShaderMetadata metadata);

    IComputePipeline CreateComputePipeline(ShaderArtifact artifact);

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
