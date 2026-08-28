using Delta.Diagnostics;

namespace Delta.Render.RenderGraph;

public readonly record struct RenderTargetHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderTextureHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderBufferHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderSamplerHandle(ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0;
}

public readonly record struct RenderGraphTextureHandle(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct RenderGraphBufferHandle(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct RenderGraphPassHandle(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct RenderGraphReadbackHandle(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct PixelExtent(uint Width, uint Height)
{
    public bool IsEmpty => Width == 0 || Height == 0;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct BufferRange(ulong Offset, ulong SizeInBytes)
{
    public bool IsEmpty => SizeInBytes == 0;
}

public readonly record struct RenderViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth = 0,
    float MaxDepth = 1)
{
    public bool IsValid => Width > 0 && Height > 0 &&
                           float.IsFinite(X) && float.IsFinite(Y) &&
                           float.IsFinite(Width) && float.IsFinite(Height) &&
                           float.IsFinite(MinDepth) && float.IsFinite(MaxDepth) &&
                           MinDepth >= 0 && MaxDepth <= 1 && MinDepth <= MaxDepth;
}

public readonly record struct ClearColor(float Red, float Green, float Blue, float Alpha);

public readonly record struct ClearDepthStencil(float Depth, uint Stencil = 0);

public readonly record struct RenderDeviceCapabilities(
    ulong MaxStorageBufferRange,
    ulong MinStorageBufferOffsetAlignment,
    uint MaxPushConstantBytes,
    uint MaxBoundDescriptorSets,
    uint MaxComputeWorkGroupInvocations,
    uint MaxComputeWorkGroupSizeX,
    uint MaxComputeWorkGroupSizeY,
    uint MaxComputeWorkGroupSizeZ,
    uint MaxComputeWorkGroupCountX,
    uint MaxComputeWorkGroupCountY,
    uint MaxComputeWorkGroupCountZ);

public enum RenderGraphExecutionStatus : byte
{
    Unknown,
    Submitted,
    NoWork,
    Failed,
    DeviceLost,
}

public readonly record struct RenderGraphExecutionResult(
    RenderGraphExecutionStatus Status,
    ReadOnlyMemory<Diagnostic> Diagnostics);

[Flags]
public enum RenderPipelineStages
{
    None = 0,
    Transfer = 1 << 0,
    Vertex = 1 << 1,
    Fragment = 1 << 2,
    Compute = 1 << 3,
    ColorOutput = 1 << 4,
    DepthStencil = 1 << 5,
    AllGraphics = Vertex | Fragment | ColorOutput | DepthStencil,
    All = Transfer | AllGraphics | Compute,
}

[Flags]
public enum RenderResourceAccess
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    ReadWrite = Read | Write,
}

public enum AttachmentLoadOperation : byte
{
    Load,
    Clear,
    Discard,
}

public enum AttachmentStoreOperation : byte
{
    Store,
    Discard,
}

public enum IndexElementFormat : byte
{
    UnsignedShort,
    UnsignedInt,
}
