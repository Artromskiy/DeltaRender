namespace Delta.Render.Core;

public readonly record struct RenderWindowId(Guid Value)
{
    public static RenderWindowId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public readonly record struct RenderWindowHandle(ulong Value)
{
    public static RenderWindowHandle Invalid => new(0);

    public bool IsValid => Value != 0;
}

public readonly record struct WindowMetrics(uint Width, uint Height, float DpiScale);

public record struct WindowConfiguration(string Title, uint Width = 1280, uint Height = 720, bool Resizable = true, bool HighDpiAware = true);

public enum RenderRecordChangeKind : byte
{
    Upserted = 0,
    Removed = 1
}

public readonly record struct RenderRecordChange(ulong EntityId, uint ComponentKindId, RenderRecordChangeKind Kind, ulong PayloadAddress, uint PayloadSize)
{
    public static RenderRecordChange Upsert(ulong entityId, uint componentKindId, ulong payloadAddress, uint payloadSize)
        => new(entityId, componentKindId, RenderRecordChangeKind.Upserted, payloadAddress, payloadSize);

    public static RenderRecordChange Remove(ulong entityId, uint componentKindId)
        => new(entityId, componentKindId, RenderRecordChangeKind.Removed, 0, 0);
}

public readonly record struct RenderFrameState(bool IsValid, bool RequiresResize, uint ImageIndex, WindowMetrics Metrics)
{
    public static RenderFrameState NotReady(WindowMetrics metrics) => new(false, false, 0, metrics);
    public static RenderFrameState ResizeRequested(WindowMetrics metrics) => new(false, true, 0, metrics);
    public static RenderFrameState Ready(uint imageIndex, WindowMetrics metrics) => new(true, false, imageIndex, metrics);
}

public interface IRenderWindowFrameSession : IAsyncDisposable
{
    RenderWindowId WindowId { get; }

    RenderFrameState BeginFrame();

    bool EndFrame(in RenderFrameState frameState, ReadOnlySpan<RenderRecordChange> dirtyRecords);

    bool EndFrame(
        in RenderFrameState frameState,
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters,
        ReadOnlySpan<UiQuad> uiQuads,
        ReadOnlySpan<RenderRecordChange> dirtyRecords);

    bool EndFrame(
        in RenderFrameState frameState,
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters,
        in UiDrawList drawList,
        ReadOnlySpan<RenderRecordChange> dirtyRecords);

    bool SubmitFrame(
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters,
        in UiDrawList drawList,
        ReadOnlySpan<RenderRecordChange> dirtyRecords);

    IGraphicsPipeline CreateGraphicsPipeline(in GraphicsShaderProgram shaderProgram);

    bool DrawFullscreenTriangle(IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters);

    bool Resize(WindowMetrics metrics);
}

public interface IRenderWindow : IAsyncDisposable
{
    RenderWindowId Id { get; }

    RenderWindowHandle Handle { get; }

    string Title { get; }

    WindowMetrics Metrics { get; }

    bool IsClosed { get; }

    IVulkanWindowSurfaceSource VulkanSurfaceSource { get; }
}

public interface IRenderWindowFactory
{
    WindowCreateResult CreateWindow(WindowConfiguration configuration);
}

public readonly record struct WindowCreateResult(IRenderWindow? Window, bool Success, RenderDiagnosticBag Diagnostics)
{
    public static WindowCreateResult SuccessResult(IRenderWindow window, RenderDiagnosticBag diagnostics) => new(window, true, diagnostics);

    public static WindowCreateResult Failure(RenderDiagnosticBag diagnostics) => new(null, false, diagnostics);
}

public interface IVulkanWindowSurfaceSource
{
    bool SupportsPortabilityEnumeration { get; }

    string PlatformName { get; }

    bool TryGetRequiredInstanceExtensions(out string[] extensionNames, out RenderDiagnosticBag diagnostics);

    bool TryCreateSurface(ulong vkInstance, ulong allocatorAddress, out ulong surface, out RenderDiagnosticBag diagnostics);

    bool TryDestroySurface(ulong vkInstance, ulong surface, out RenderDiagnosticBag diagnostics);
}

public enum RuntimeStatus
{
    Ok,
    MissingLoader,
    MissingDisplay,
    Unsupported,
    Unknown
}

public readonly record struct HeadlessProbeResult(RuntimeStatus Status, RenderDiagnosticBag Diagnostics)
{
    public bool Available => Status == RuntimeStatus.Ok;
}
