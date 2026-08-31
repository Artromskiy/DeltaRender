using Delta.Render.RenderGraph;

namespace Delta.Render;

public readonly record struct RenderWindowId(Guid Value)
{
    public static RenderWindowId New() => new(Guid.NewGuid());
}

public readonly record struct RenderWindowHandle(ulong Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct WindowMetrics(uint Width, uint Height, float DpiScale)
{
    /// <summary>Physical drawable width in pixels, or <see cref="Width"/> when unspecified.</summary>
    public uint DrawableWidth { get; init; }

    /// <summary>Physical drawable height in pixels, or <see cref="Height"/> when unspecified.</summary>
    public uint DrawableHeight { get; init; }

    /// <summary>Physical render target extent corresponding to this window.</summary>
    public PixelExtent DrawableExtent => new(
        DrawableWidth == 0 ? Width : DrawableWidth,
        DrawableHeight == 0 ? Height : DrawableHeight);

    public bool IsValid => Width > 0 && Height > 0 && float.IsFinite(DpiScale) && DpiScale > 0;
}

public readonly record struct WindowConfiguration(
    string Title,
    uint Width,
    uint Height,
    bool Resizable,
    bool HighDpi = true);

public interface IRenderWindow : IAsyncDisposable
{
    RenderWindowId Id { get; }
    RenderWindowHandle Handle { get; }
    WindowMetrics Metrics { get; }
    bool IsClosed { get; }
    IVulkanWindowSurfaceSource VulkanSurfaceSource { get; }
}

public interface IRenderWindowFactory
{
    WindowCreateResult CreateWindow(WindowConfiguration configuration);
}

public sealed class WindowCreateResult
{
    private WindowCreateResult(IRenderWindow? window, RenderDiagnosticBag diagnostics)
    {
        Window = window;
        Diagnostics = diagnostics;
    }

    public IRenderWindow? Window { get; }
    public RenderDiagnosticBag Diagnostics { get; }
    public bool Succeeded => Window is not null;
    public bool Success => Succeeded;

    public static WindowCreateResult SuccessResult(IRenderWindow window, RenderDiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new WindowCreateResult(window, diagnostics);
    }

    public static WindowCreateResult Failure(RenderDiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new WindowCreateResult(null, diagnostics);
    }
}

public readonly record struct HeadlessProbeResult(RuntimeStatus Status, RenderDiagnosticBag Diagnostics);

public enum RuntimeStatus : byte
{
    Ok,
    MissingDisplay,
    Unavailable,
}

public interface IVulkanWindowSurfaceSource
{
    bool SupportsPortabilityEnumeration { get; }
    string PlatformName { get; }
    bool TryCreateSurface(ulong vkInstance, ulong allocatorAddress, out ulong surface, out RenderDiagnosticBag diagnostics);
    bool TryDestroySurface(ulong vkInstance, ulong surface, out RenderDiagnosticBag diagnostics);
    bool TryGetRequiredInstanceExtensions(out string[] extensionNames, out RenderDiagnosticBag diagnostics);
}
