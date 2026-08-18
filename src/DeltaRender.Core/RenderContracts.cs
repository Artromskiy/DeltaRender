namespace DVG.Render.Core;

public readonly record struct RenderWindowId(Guid Value)
{
    public static RenderWindowId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public readonly record struct WindowMetrics(uint Width, uint Height, float DpiScale);

public record struct WindowConfiguration(string Title, uint Width = 1280, uint Height = 720, bool Resizable = true, bool HighDpiAware = true);

public interface IRenderWindow : IAsyncDisposable
{
    RenderWindowId Id { get; }

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
