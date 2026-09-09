using Delta.Render;

namespace Delta.Render.Platform.SDL3;

public sealed class Sdl3Window : IRenderWindow
{
    private readonly ulong _handle;
    private WindowMetrics _metrics;
    private bool _closed;

    public Sdl3Window(ulong handle, WindowConfiguration configuration)
        : this(
            handle,
            configuration,
            new WindowMetrics(configuration.Width, configuration.Height, 1.0f))
    {
    }

    internal Sdl3Window(ulong handle, WindowConfiguration configuration, WindowMetrics metrics)
    {
        Id = RenderWindowId.New();
        Handle = new RenderWindowHandle(handle);
        _handle = handle;
        Title = configuration.Title;
        _metrics = metrics;
        VulkanSurfaceSource = new Sdl3VulkanSurfaceSource(handle);
    }

    public RenderWindowId Id { get; }

    public RenderWindowHandle Handle { get; }

    public string Title { get; }

    public WindowMetrics Metrics
    {
        get
        {
            if (Sdl3Runtime.TryGetWindowMetrics(_handle, out var metrics, out _))
            {
                _metrics = metrics;
            }

            return _metrics;
        }
    }

    public bool IsClosed => _closed;

    public IVulkanWindowSurfaceSource VulkanSurfaceSource { get; }

    public ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return ValueTask.CompletedTask;
        }

        _closed = true;
        if (_handle != 0)
        {
            _ = Sdl3Runtime.TryDestroyWindow(_handle);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Sdl3VulkanSurfaceSource(ulong windowHandle) : IVulkanWindowSurfaceSource
    {
        public bool SupportsPortabilityEnumeration => OperatingSystem.IsMacOS();

        public string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "MacOS";

        public bool TryCreateSurface(ulong vkInstance, ulong allocatorAddress, out ulong surface, out RenderDiagnosticBag diagnostics)
            => Sdl3Runtime.TryCreateSurface(windowHandle, vkInstance, allocatorAddress, out surface, out diagnostics);

        public bool TryDestroySurface(ulong vkInstance, ulong surface, out RenderDiagnosticBag diagnostics)
        {
            diagnostics = new RenderDiagnosticBag();
            if (Sdl3Runtime.TryDestroySurface(vkInstance, surface))
            {
                return true;
            }

            diagnostics.Add(RenderDiagnosticSeverity.Warning, "SDL-SURFACE", "SDL surface destroy call was unavailable.");
            return false;
        }

        public bool TryGetRequiredInstanceExtensions(out string[] extensionNames, out RenderDiagnosticBag diagnostics)
            => Sdl3Runtime.TryGetRequiredInstanceExtensions(out extensionNames, out diagnostics);
    }
}
