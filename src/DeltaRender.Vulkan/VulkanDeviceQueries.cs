using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Delta.Render;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Delta.Render.Vulkan;

internal static unsafe class VulkanDeviceQueries
{
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes extension counts through unsafe out pointers that the analyzer cannot model.")]
    internal static bool TrySelectPhysicalDevice(
        Vk api,
        Instance instance,
        KhrSurface surfaceExtension,
        SurfaceKHR surface,
        RenderDiagnosticBag diagnostics,
        out PhysicalDevice physicalDevice)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        physicalDevice = default;
        uint deviceCount = 0;
        var enumResult = api.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (enumResult != Result.Success || deviceCount == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No Vulkan physical devices are available.");
            return false;
        }

        var devices = new PhysicalDevice[(int)deviceCount];
        enumResult = api.EnumeratePhysicalDevices(instance, &deviceCount, devices);
        if (enumResult != Result.Success)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"EnumeratePhysicalDevices failed: {enumResult}");
            return false;
        }

        for (var i = 0; i < devices.Length; i++)
        {
            var candidate = devices[i];
            if (!IsDeviceSuitable(api, surfaceExtension, candidate, surface))
            {
                continue;
            }

            physicalDevice = candidate;
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Suitable physical device selected.");
            return true;
        }

        diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "No physical device supports graphics+present with swapchain.");
        return false;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    internal static bool TrySelectHeadlessPhysicalDevice(
        Vk api,
        Instance instance,
        RenderDiagnosticBag diagnostics,
        out PhysicalDevice physicalDevice,
        out uint graphicsFamily)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        physicalDevice = default;
        graphicsFamily = uint.MaxValue;
        uint deviceCount = 0;
        var enumResult = api.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (enumResult != Result.Success || deviceCount == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No Vulkan physical devices are available.");
            return false;
        }

        var devices = new PhysicalDevice[(int)deviceCount];
        enumResult = api.EnumeratePhysicalDevices(instance, &deviceCount, devices);
        if (enumResult != Result.Success)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"EnumeratePhysicalDevices failed: {enumResult}");
            return false;
        }

        for (var i = 0; i < devices.Length; i++)
        {
            if (!TryGetGraphicsQueueFamily(api, devices[i], out var family))
            {
                continue;
            }

            physicalDevice = devices[i];
            graphicsFamily = family;
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Suitable headless graphics device selected.");
            return true;
        }

        diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "No physical device exposes a graphics queue for headless rendering.");
        return false;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    internal static QueueFamilyProperties[] GetQueueFamilyProperties(Vk api, PhysicalDevice physicalDevice)
    {
        uint queueFamilyCount = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            return Array.Empty<QueueFamilyProperties>();
        }

        var families = new QueueFamilyProperties[(int)queueFamilyCount];
        api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, families);
        return families;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes extension counts through unsafe out pointers that the analyzer cannot model.")]
    internal static bool DeviceSupportsSwapchainExtensions(Vk api, PhysicalDevice device, params string[] requiredExtensions)
    {
        ArgumentNullException.ThrowIfNull(requiredExtensions);
        uint extensionCount = 0;
        _ = api.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);
        if (extensionCount == 0)
        {
            return false;
        }

        var available = new ExtensionProperties[(int)extensionCount];
        _ = api.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, available);

        foreach (var required in requiredExtensions)
        {
            var found = false;
            for (var i = 0; i < available.Length; i++)
            {
                string? extensionName;
                fixed (byte* name = available[i].ExtensionName)
                {
                    extensionName = Marshal.PtrToStringAnsi((nint)name);
                }

                if (string.Equals(extensionName, required, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes surface capability counts through unsafe out pointers that the analyzer cannot model.")]
    private static bool HasSwapChainDetails(Vk api, KhrSurface surfaceExtension, PhysicalDevice physicalDevice, SurfaceKHR surface)
    {
        _ = surfaceExtension.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out _);
        uint formatCount = 0;
        _ = surfaceExtension.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, null);
        uint modeCount = 0;
        _ = surfaceExtension.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, &modeCount, null);
        return formatCount != 0 && modeCount != 0;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private static bool IsDeviceSuitable(Vk api, KhrSurface surfaceExtension, PhysicalDevice physicalDevice, SurfaceKHR surface)
    {
        if (!DeviceSupportsSwapchainExtensions(api, physicalDevice, KhrSwapchain.ExtensionName) ||
            !HasSwapChainDetails(api, surfaceExtension, physicalDevice, surface))
        {
            return false;
        }

        var queueFamilies = GetQueueFamilyProperties(api, physicalDevice);
        if (queueFamilies.Length == 0)
        {
            return false;
        }

        var hasGraphics = false;
        var hasPresent = false;
        for (var i = 0; i < queueFamilies.Length; i++)
        {
            if (!hasGraphics && queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                hasGraphics = true;
            }

            _ = surfaceExtension.GetPhysicalDeviceSurfaceSupport(physicalDevice, (uint)i, surface, out var canPresent);
            if (!hasPresent && canPresent)
            {
                hasPresent = true;
            }
        }

        return hasGraphics && hasPresent;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private static bool TryGetGraphicsQueueFamily(Vk api, PhysicalDevice physicalDevice, out uint graphicsFamily)
    {
        graphicsFamily = uint.MaxValue;
        var families = GetQueueFamilyProperties(api, physicalDevice);
        if (families.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < families.Length; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamily = (uint)i;
                return true;
            }
        }

        return false;
    }
}
