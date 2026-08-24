using Delta.Render.Core;

namespace Delta.Render.Vulkan;

internal sealed class VulkanSurfaceLease
{
    private readonly IVulkanWindowSurfaceSource _source;
    private readonly ulong _instance;
    private readonly ulong _surface;
    private bool _transferred;
    private bool _released;

    internal VulkanSurfaceLease(IVulkanWindowSurfaceSource source, ulong instance, ulong surface)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (instance == 0 || surface == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(surface), "A Vulkan surface lease requires non-zero instance and surface handles.");
        }

        _instance = instance;
        _surface = surface;
    }

    internal ulong Handle => _surface;
    internal bool IsTransferred => _transferred;
    internal bool IsReleased => _released;

    internal void TransferToSession()
    {
        ObjectDisposedException.ThrowIf(_released, this);

        if (_transferred)
        {
            throw new InvalidOperationException("The Vulkan surface lease was already transferred.");
        }

        _transferred = true;
    }

    internal bool TryRelease(out RenderDiagnosticBag diagnostics)
    {
        diagnostics = new RenderDiagnosticBag();
        if (_released)
        {
            return true;
        }

        _released = true;
        try
        {
            return _source.TryDestroySurface(_instance, _surface, out diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-SURFACE-DESTROY", exception.Message);
            return false;
        }
    }
}
