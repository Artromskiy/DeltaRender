using Silk.NET.Vulkan;

namespace DeltaRender.Vulkan;

public sealed class VulkanRendererOptions
{
    public string ApplicationName { get; init; } = "DeltaRender";

    public string EngineName { get; init; } = "DeltaRender";

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

public readonly record struct VulkanProbeResult(VulkanProbeStatus Status, bool HasLoader, bool HasWindowPath, RenderDiagnosticBag Diagnostics)
{
    public bool Usable => Status == VulkanProbeStatus.Ok;
}
