using Delta.Render;
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
            Assert.True(result.Diagnostics.Count > 0);
        }
    }

    [Fact]
    public void HeadlessSdlProbeReportsClearStatusWithoutThrowing()
    {
        var result = Sdl3WindowFactory.CheckHeadlessDisplay();
        Assert.NotEqual(default, result.Status);
        Assert.NotNull(result.Diagnostics);
    }
}
