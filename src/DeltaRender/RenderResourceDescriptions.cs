namespace Delta.Render.RenderGraph;

public enum RenderTextureFormat : byte
{
    Unknown,
    R8Unorm,
    Rgba8Unorm,
    Rgba8Srgb,
    Bgra8Unorm,
    Bgra8Srgb,
    Rgba16Float,
    D32Float,
    D24UnormS8UInt,
}

[Flags]
public enum RenderTextureUsage
{
    None = 0,
    Sampled = 1 << 0,
    Storage = 1 << 1,
    ColorAttachment = 1 << 2,
    DepthStencilAttachment = 1 << 3,
    TransferSource = 1 << 4,
    TransferDestination = 1 << 5,
}

[Flags]
public enum RenderBufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    Indirect = 1 << 4,
    TransferSource = 1 << 5,
    TransferDestination = 1 << 6,
}

public enum RenderFilter : byte
{
    Nearest,
    Linear,
}

public enum RenderAddressMode : byte
{
    Repeat,
    MirroredRepeat,
    ClampToEdge,
}

public readonly record struct RenderTextureDescription(
    uint Width,
    uint Height,
    RenderTextureFormat Format,
    RenderTextureUsage Usage,
    uint Layers = 1,
    uint MipLevels = 1,
    uint Samples = 1)
{
    public bool IsValid => Width > 0 && Height > 0 &&
                           Format != RenderTextureFormat.Unknown &&
                           Usage != RenderTextureUsage.None &&
                           Layers > 0 && MipLevels > 0 && Samples > 0;
}

public readonly record struct RenderBufferDescription(
    ulong SizeInBytes,
    RenderBufferUsage Usage)
{
    public bool IsValid => SizeInBytes > 0 && Usage != RenderBufferUsage.None;
}

public readonly record struct RenderSamplerDescription(
    RenderFilter MinFilter = RenderFilter.Linear,
    RenderFilter MagFilter = RenderFilter.Linear,
    RenderFilter MipmapFilter = RenderFilter.Linear,
    RenderAddressMode AddressU = RenderAddressMode.ClampToEdge,
    RenderAddressMode AddressV = RenderAddressMode.ClampToEdge,
    RenderAddressMode AddressW = RenderAddressMode.ClampToEdge);

public readonly record struct ColorAttachmentDescription(
    RenderGraphTextureHandle Texture,
    AttachmentLoadOperation Load,
    AttachmentStoreOperation Store,
    ClearColor ClearValue = default);

public readonly record struct DepthStencilAttachmentDescription(
    RenderGraphTextureHandle Texture,
    AttachmentLoadOperation DepthLoad,
    AttachmentStoreOperation DepthStore,
    ClearDepthStencil ClearValue = default,
    AttachmentLoadOperation StencilLoad = AttachmentLoadOperation.Discard,
    AttachmentStoreOperation StencilStore = AttachmentStoreOperation.Discard);
