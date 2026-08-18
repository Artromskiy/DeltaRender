using DVG.Render.Core;
using DVG.Render.Platform.SDL3;
using DVG.Render.Vulkan;
using System.IO;
using Xunit;

namespace DVG.Render.Tests;

public class VulkanHeadlessTests
{
    [Fact]
    public void Headless_vulkan_probe_returns_diagnostic_or_ready_state()
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
    public void Headless_sdl_probe_reports_clear_status_without_throwing()
    {
        var result = Sdl3WindowFactory.CheckHeadlessDisplay();
        Assert.True(result.Status is RuntimeStatus.Ok or RuntimeStatus.MissingDisplay or RuntimeStatus.Unknown or RuntimeStatus.Unsupported);
        Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public void GLSH_manifest_requires_explicit_std430_storage_buffer_abi()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "clear-triangle.glsl.manifest.json");
        var json = File.ReadAllText(path);

        Assert.True(ShaderAbiManifestReader.TryRead(json, out var manifest, out var diagnostics), diagnostics.ToText());
        Assert.NotNull(manifest);
        Assert.Equal(ShaderAbiLayout.Std430, manifest!.Layout);
        Assert.All(manifest.Resources, resource => Assert.Equal(ShaderAbiResourceKind.StorageBuffer, resource.Kind));
        Assert.DoesNotContain(manifest.Resources.SelectMany(static resource => resource.Members), member => member.Stride == 0);
    }
}
