using DeltaShader.Contract;

namespace DeltaRender;

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

public readonly record struct RenderFrameState(bool IsValid, bool RequiresResize, uint ImageIndex, WindowMetrics Metrics)
{
    public static RenderFrameState NotReady(WindowMetrics metrics) => new(false, false, 0, metrics);
    public static RenderFrameState ResizeRequested(WindowMetrics metrics) => new(false, true, 0, metrics);
    public static RenderFrameState Ready(uint imageIndex, WindowMetrics metrics) => new(true, false, imageIndex, metrics);
}

/// <summary>
/// Borrowed contents for one already-begun frame. A null pipeline is valid only
/// with the corresponding empty draw list; the default packet is clear-only.
/// </summary>
public readonly ref struct RenderFramePacket
{
    public RenderFramePacket(
        IGraphicsPipeline? uiPipeline,
        GraphicsFrameParameters uiParameters,
        UiDrawList uiDrawList,
        IGraphicsPipeline? textPipeline,
        TextFrameParameters textParameters,
        ReadOnlySpan<ITextAtlasPage> atlasPages,
        TextDrawList textDrawList,
        ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        UiPipeline = uiPipeline;
        UiParameters = uiParameters;
        UiDrawList = uiDrawList;
        TextPipeline = textPipeline;
        TextParameters = textParameters;
        AtlasPages = atlasPages;
        TextDrawList = textDrawList;
        DirtyRecords = dirtyRecords;
    }

    public IGraphicsPipeline? UiPipeline { get; }

    public GraphicsFrameParameters UiParameters { get; }

    public UiDrawList UiDrawList { get; }

    public IGraphicsPipeline? TextPipeline { get; }

    public TextFrameParameters TextParameters { get; }

    public ReadOnlySpan<ITextAtlasPage> AtlasPages { get; }

    public TextDrawList TextDrawList { get; }

    public ReadOnlySpan<RenderRecordChange> DirtyRecords { get; }

    public bool IsValid => (UiPipeline is null ? UiDrawList.IsEmpty : UiParameters.IsValid) &&
                           (TextPipeline is null
                               ? TextDrawList.IsEmpty && AtlasPages.IsEmpty
                               : UiPipeline is not null && TextParameters.IsValid);
}

public interface IRenderWindowFrameSession : IAsyncDisposable
{
    RenderWindowId WindowId { get; }

    RenderFrameState BeginFrame();

    bool EndFrame(in RenderFrameState frameState, in RenderFramePacket packet);

    IGraphicsPipeline CreateTextPipeline(in IGraphicsShaderProgram shaderProgram);

    IGraphicsPipeline CreateGraphicsPipeline(in IGraphicsShaderProgram shaderProgram);

    ITextAtlasDevice CreateTextAtlasDevice();

    bool DrawFullscreenTriangle(IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters);

    bool Resize(WindowMetrics metrics);
}

public static class RenderFrameSessionExtensions
{
    /// <summary>Ends an already-begun frame through the canonical packet contract.</summary>
    public static bool EndFrame(
        this IRenderWindowFrameSession session,
        in RenderFrameState frameState,
        in RenderFramePacket packet)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!frameState.IsValid || !packet.IsValid)
        {
            return false;
        }

        return session.EndFrame(in frameState, in packet);
    }

    public static bool SubmitFrame(this IRenderWindowFrameSession session, in RenderFramePacket packet)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!packet.IsValid)
        {
            return false;
        }

        var frameState = session.BeginFrame();
        return frameState.IsValid && session.EndFrame(in frameState, in packet);
    }
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
