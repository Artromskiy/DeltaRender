namespace Delta.Render.RenderGraph;

/// <summary>
/// Builds and executes one Vulkan render graph. Implementations own resource
/// lifetime, dependency ordering and synchronization.
/// </summary>
public interface IRenderGraph
{
    void Build(in RenderGraphFrame frame, ReadOnlySpan<IRenderFeature> features);

    void Execute();
}

/// <summary>
/// Mutable graph-construction surface. Handles are valid only for the graph
/// build in which they were returned.
/// </summary>
public interface IRenderGraphBuilder
{
    RenderGraphTextureHandle ImportSurface(RenderSurfaceHandle surface);

    RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture);

    RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer);

    RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description);

    RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description);

    RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass);

    RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass);

    RenderGraphPassHandle AddTransferPass(in TransferPassDescription description, ITransferPass pass);

    void UseColorAttachment(
        RenderGraphPassHandle pass,
        uint index,
        in ColorAttachmentDescription attachment);

    void UseDepthStencilAttachment(
        RenderGraphPassHandle pass,
        in DepthStencilAttachmentDescription attachment);

    void UseTexture(
        RenderGraphPassHandle pass,
        RenderGraphTextureHandle texture,
        RenderResourceAccess access,
        RenderPipelineStages stages);

    void UseBuffer(
        RenderGraphPassHandle pass,
        RenderGraphBufferHandle buffer,
        RenderResourceAccess access,
        RenderPipelineStages stages);
}

public interface IRenderFeatureContext
{
    long FrameNumber { get; }

    RenderView View { get; }
}

public interface IRenderFeature
{
    void AddPasses(IRenderGraphBuilder graph, IRenderFeatureContext context);
}

/// <summary>
/// A persistent feature receives borrowed data for the next graph build.
/// Submit replaces the previous submission; it does not execute rendering.
/// </summary>
public interface IRenderFeature<TSubmission> : IRenderFeature
    where TSubmission : struct
{
    void Submit(in TSubmission submission);
}

public interface IRasterPass
{
    void Record(IRasterCommandContext commands);
}

public interface IComputePass
{
    void Record(IComputeCommandContext commands);
}

public interface ITransferPass
{
    void Record(ITransferCommandContext commands);
}

public interface IShaderResourceCommandContext
{
    void BindBuffer(
        ShaderBinding binding,
        RenderGraphBufferHandle buffer,
        ulong offset = 0,
        ulong sizeInBytes = 0);

    void BindTexture(
        ShaderBinding binding,
        RenderGraphTextureHandle texture,
        RenderSamplerHandle sampler);

    void PushConstants(ReadOnlySpan<byte> data, uint offset = 0);
}

public interface IRasterCommandContext : IShaderResourceCommandContext
{
    void SetViewport(in RenderViewport viewport);

    void SetScissor(in PixelRect scissor);

    void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0);

    void BindIndexBuffer(
        RenderGraphBufferHandle buffer,
        IndexElementFormat format,
        ulong offset = 0);

    void Draw(
        uint vertexCount,
        uint instanceCount = 1,
        uint firstVertex = 0,
        uint firstInstance = 0);

    void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0);
}

public interface IComputeCommandContext : IShaderResourceCommandContext
{
    void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1);
}

public interface ITransferCommandContext
{
    void CopyBuffer(
        RenderGraphBufferHandle source,
        RenderGraphBufferHandle destination,
        ulong sizeInBytes,
        ulong sourceOffset = 0,
        ulong destinationOffset = 0);

    void UploadBuffer(
        RenderGraphBufferHandle destination,
        ReadOnlySpan<byte> data,
        ulong destinationOffset = 0);

    void UploadTexture(
        RenderGraphTextureHandle destination,
        in PixelRect destinationRegion,
        ReadOnlySpan<byte> data,
        uint sourceRowPitch);
}
