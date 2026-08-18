using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

public sealed class VulkanRendererOptions
{
    public string ApplicationName { get; init; } = "Delta.Render";

    public string EngineName { get; init; } = "Delta.Render";

    public bool EnableValidation { get; init; } = true;

    public uint ApiVersion { get; init; } = Vk.Version13;

    public uint MaxFramesInFlight { get; init; } = 2;

    public uint PreferredSurfaceImageCount { get; init; } = 2;
}

public enum VulkanProbeStatus
{
    Ok,
    HeadlessUnavailable,
    WindowingUnavailable,
    Error
}

public readonly record struct VulkanProbeResult(VulkanProbeStatus Status, bool HasLoader, bool HasWindowPath, Core.RenderDiagnosticBag Diagnostics)
{
    public bool Usable => Status == VulkanProbeStatus.Ok;
}
