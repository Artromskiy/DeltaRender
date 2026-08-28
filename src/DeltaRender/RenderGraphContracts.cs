using Delta.Shader.Contract;

namespace Delta.Render.RenderGraph;

/// <summary>
/// Builds and executes Vulkan work for one frame or one headless operation.
/// A graph may be reused, but its graph-local handles expire on the next build.
/// </summary>
public interface IRenderGraph
{
    void Build(ulong frameNumber, ReadOnlySpan<IRenderFeature> features);

    RenderGraphExecutionResult Execute();

    int CopyReadback(RenderGraphReadbackHandle readback, Span<byte> destination);
}

/// <summary>
/// Declares graph resources, passes and dependencies. The builder is borrowed
/// by a feature only for the duration of that feature's AddPasses call.
/// </summary>
public interface IRenderGraphBuilder
{
    RenderGraphTextureHandle ImportTarget(RenderTargetHandle target);

    RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture);

    RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer);

    RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description);

    RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description);

    RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass);

    RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass);

    RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass);

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

    RenderGraphReadbackHandle ReadbackBuffer(
        RenderGraphBufferHandle buffer,
        in BufferRange range);

    RenderGraphReadbackHandle ReadbackTexture(
        RenderGraphTextureHandle texture,
        in PixelRect region);
}

public interface IRenderFeature
{
    void AddPasses(IRenderGraphBuilder graph, ulong frameNumber);
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

    void CopyTexture(
        RenderGraphTextureHandle source,
        in PixelRect sourceRegion,
        RenderGraphTextureHandle destination,
        in PixelRect destinationRegion);

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
