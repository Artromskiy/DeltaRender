using Delta.Render.Core;

namespace Delta.Render.Platform.SDL3;

public sealed class Sdl3WindowFactory : IRenderWindowFactory
{
    // The host owns event pumping; this helper is intentionally outside the
    // render-facing IRenderWindow contract.
    public static void PumpEvents() => Sdl3Runtime.PumpEvents();

    public WindowCreateResult CreateWindow(WindowConfiguration configuration)
    {
        var diagnostics = new RenderDiagnosticBag();

        if (!Sdl3Runtime.TryInitialize(out var initDiag))
        {
            diagnostics.Merge(initDiag);
            return WindowCreateResult.Failure(diagnostics);
        }

        if (!Sdl3Runtime.TryCreateWindow(configuration.Title, configuration.Width, configuration.Height, configuration.Resizable, out var handle, out var createDiag))
        {
            diagnostics.Merge(createDiag);
            return WindowCreateResult.Failure(diagnostics);
        }

        var window = new Sdl3Window(handle, configuration);
        return WindowCreateResult.SuccessResult(window, diagnostics);
    }

    public static HeadlessProbeResult CheckHeadlessDisplay()
    {
        if (!Sdl3Runtime.IsAvailable)
        {
            return new HeadlessProbeResult(RuntimeStatus.MissingDisplay, new RenderDiagnosticBag());
        }

        if (!Sdl3Runtime.TryInitialize(out var diagnostics))
        {
            return new HeadlessProbeResult(RuntimeStatus.MissingDisplay, diagnostics);
        }

        return new HeadlessProbeResult(RuntimeStatus.Ok, diagnostics);
    }
}
