using Delta.Shader.Contract;
using Delta.Render.RenderGraph;

namespace Delta.Render;

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

public enum RenderRecordChangeKind
{
    Upserted = 0,
    Removed = 1
}

public readonly record struct RenderRecordChange(ulong EntityId, uint ComponentKindId, RenderRecordChangeKind Kind, ReadOnlyMemory<byte> Payload)
{
    public uint PayloadSize => (uint)Payload.Length;

    public static RenderRecordChange Upsert(ulong entityId, uint componentKindId, ReadOnlyMemory<byte> payload)
        => new(entityId, componentKindId, RenderRecordChangeKind.Upserted, payload);

    public static RenderRecordChange Remove(ulong entityId, uint componentKindId)
        => new(entityId, componentKindId, RenderRecordChangeKind.Removed, ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// Owns one window's graph execution lifetime. The graph created by this session
/// owns acquire, command recording, submission and presentation; callers do not
/// submit a packet or manipulate an acquired frame state directly.
/// </summary>
public interface IRenderFrameSession : IAsyncDisposable
{
    RenderWindowId WindowId { get; }

    RenderSurfaceHandle SurfaceHandle { get; }

    IRenderGraph CreateRenderGraph();

    IGraphicsPipeline CreateTextPipeline(in IGraphicsShaderProgram shaderProgram);

    IGraphicsPipeline CreateGraphicsPipeline(in IGraphicsShaderProgram shaderProgram);

    ITextAtlasDevice CreateTextAtlasDevice();

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
