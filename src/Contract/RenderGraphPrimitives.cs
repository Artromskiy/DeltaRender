namespace Delta.Render.RenderGraph;

public readonly record struct RenderSurfaceHandle(ulong Value, uint Generation)
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

public readonly record struct ShaderBinding(uint Set, uint Binding);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
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

public readonly record struct RenderView(
    RenderSurfaceHandle Surface,
    RenderViewport Viewport,
    PixelRect Scissor)
{
    public bool IsValid => Surface.IsValid && Viewport.IsValid && !Scissor.IsEmpty;
}

public readonly record struct RenderGraphFrame(
    long FrameNumber,
    ReadOnlyMemory<RenderView> Views)
{
    public bool IsValid => FrameNumber >= 0 && !Views.IsEmpty;
}

public readonly record struct ClearColor(float Red, float Green, float Blue, float Alpha);

public readonly record struct ClearDepthStencil(float Depth, uint Stencil = 0);

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

public enum AttachmentLoadOperation
{
    Load,
    Clear,
    Discard,
}

public enum AttachmentStoreOperation
{
    Store,
    Discard,
}

public enum IndexElementFormat
{
    UnsignedShort,
    UnsignedInt,
}
