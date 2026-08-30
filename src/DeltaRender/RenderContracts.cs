using Delta.Render.RenderGraph;

namespace Delta.Render;

/// <summary>
/// Owns persistent renderer resources and creates reusable render graphs.
/// The target is invalid for compute-only sessions, valid for offscreen
/// graphics sessions and presentable for windowed sessions.
/// </summary>
public interface IRenderFrameSession : IAsyncDisposable
{
    RenderDeviceCapabilities Capabilities { get; }

    RenderTargetHandle Target { get; }

    IRenderGraph CreateRenderGraph();

    /// <summary>
    /// Recreates device-local session state after a Vulkan device-loss event.
    /// All existing persistent and graph-local handles become stale; callers
    /// must create and upload their resources again after a successful return.
    /// </summary>
    bool TryReinitializeAfterDeviceLoss();

    RenderBufferHandle CreateBuffer(in RenderBufferDescription description);

    RenderTextureHandle CreateTexture(in RenderTextureDescription description);

    RenderSamplerHandle CreateSampler(in RenderSamplerDescription description);

    void Release(RenderBufferHandle buffer);

    void Release(RenderTextureHandle texture);

    void Release(RenderSamplerHandle sampler);

    void ResizeTarget(in PixelExtent extent);
}
