using Delta.Render;
using System.Diagnostics.CodeAnalysis;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

public static unsafe class VulkanEnvironmentProbe
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The headless probe is a Vulkan loader boundary and must convert unknown native-loader failures into diagnostics.")]
    public static VulkanProbeResult CheckHeadless()
    {
        var diagnostics = new RenderDiagnosticBag();

        try
        {
            var vk = Vk.GetApi();
            byte* name = (byte*)SilkMarshal.StringToPtr("DeltaRender");
            try
            {
                ApplicationInfo appInfo = new()
                {
                    SType = StructureType.ApplicationInfo,
                    PNext = null,
                    PApplicationName = name,
                    PEngineName = name,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 0),
                    EngineVersion = Vk.MakeVersion(0, 0, 0),
                    ApiVersion = Vk.Version13
                };

                InstanceCreateInfo instanceInfo = new()
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                    EnabledExtensionCount = 0,
                    EnabledLayerCount = 0,
                    PpEnabledExtensionNames = null,
                    PNext = null,
                    Flags = 0,
                };

                var result = vk.CreateInstance(instanceInfo, null, out var instance);
                if (result != Result.Success)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-HEADLESS", $"CreateInstance failed: {result}");
                    return new VulkanProbeResult(VulkanProbeStatus.Error, true, false, diagnostics);
                }

                vk.DestroyInstance(instance, null);
                diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-HEADLESS", "Vulkan loader is present. Headless probe succeeded.");
                return new VulkanProbeResult(VulkanProbeStatus.Ok, true, false, diagnostics);
            }
            finally
            {
                if (name != null)
                {
                    SilkMarshal.Free((nint)name);
                }
            }
        }
        catch (DllNotFoundException ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-HEADLESS", "Vulkan loader was not found. Install a system Vulkan runtime or MoltenVK on macOS.");
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-HEADLESS", ex.Message);
            return new VulkanProbeResult(VulkanProbeStatus.HeadlessUnavailable, false, false, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-HEADLESS", ex.Message);
            return new VulkanProbeResult(VulkanProbeStatus.Error, true, false, diagnostics);
        }
    }
}
