using Delta.Render.Core;
using Delta.Render.Platform.SDL3;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public class VulkanHeadlessTests
{
    [Fact]
    public void HeadlessVulkanProbeReturnsDiagnosticOrReadyState()
    {
        var result = VulkanEnvironmentProbe.CheckHeadless();
        Assert.NotNull(result.Diagnostics);
        if (result.Usable)
        {
            Assert.True(result.Status == VulkanProbeStatus.Ok);
        }
        else
        {
            Assert.True(result.Status != VulkanProbeStatus.Ok);
            Assert.True(result.Diagnostics.HasErrors);
        }
    }

    [Fact]
    public void HeadlessSdlProbeReportsClearStatusWithoutThrowing()
    {
        var result = Sdl3WindowFactory.CheckHeadlessDisplay();
        Assert.True(result.Status is RuntimeStatus.Ok or RuntimeStatus.MissingDisplay or RuntimeStatus.Unknown or RuntimeStatus.Unsupported);
        Assert.NotNull(result.Diagnostics);
    }
}
