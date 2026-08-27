using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

public sealed unsafe class VulkanRenderer : IAsyncDisposable
{
    private static readonly string[] MoltenVkLibraryNames = ["libMoltenVK.dylib", "MoltenVK"];
    private readonly DefaultNativeContext? _nativeContext;
    private bool _initializedHeadless;

    public VulkanRenderer(VulkanRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;

        if (OperatingSystem.IsMacOS())
        {
            _nativeContext = new DefaultNativeContext(MoltenVkLibraryNames);
            Api = new Vk(_nativeContext);
        }
        else
        {
            Api = Vk.GetApi();
        }
    }

    public VulkanRendererOptions Options { get; }

    public Vk Api { get; }

    public RenderDiagnosticBag Diagnostics { get; } = new();

    public bool IsInitialized { get; private set; }

    public Instance Instance { get; private set; }
    public Device Device { get; private set; }

    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private ExtDebugUtils? _extDebug;

    private KhrSurface SurfaceExtension => _khrSurface ?? throw new InvalidOperationException("Vulkan surface extension is not initialized.");
    private KhrSwapchain SwapchainExtension => _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized.");
    private ExtDebugUtils DebugExtension => _extDebug ?? throw new InvalidOperationException("Vulkan debug extension is not initialized.");

    private DebugUtilsMessengerEXT _debugMessenger;
    private PfnDebugUtilsMessengerCallbackEXT _debugCallback;

    private PhysicalDevice _physicalDevice;
    private uint _graphicsFamily = uint.MaxValue;
    private uint _presentFamily = uint.MaxValue;
    private Queue _graphicsQueue;
    private Queue _presentQueue;

    public bool IsValidationEnabled { get; private set; }

    public ValueTask DisposeAsync() => DisposeResourcesAsync();

    public IComputeDevice CreateComputeDevice() => new VulkanComputeDevice(Options);

    public IRenderFrameSession CreateWindowSession(IRenderWindow window)
    {
        var sessionDiagnostics = new RenderDiagnosticBag();
        ArgumentNullException.ThrowIfNull(window);

        if (_initializedHeadless)
        {
            throw new InvalidOperationException("A Vulkan renderer initialized for headless rendering cannot create a window session. Create a separate renderer for the window path.");
        }

        if (!IsInitialized)
        {
            InitializeForWindow(window.VulkanSurfaceSource, sessionDiagnostics);
            if (!IsInitialized)
            {
                throw new InvalidOperationException($"Failed to initialize Vulkan instance. Diagnostics: {sessionDiagnostics}");
            }
        }

        if (!window.VulkanSurfaceSource.TryCreateSurface((ulong)Instance.Handle, 0, out var surfaceHandle, out var createSurfaceDiagnostics))
        {
            sessionDiagnostics.Merge(createSurfaceDiagnostics);
            throw new InvalidOperationException($"Failed to create Vulkan surface for {window.VulkanSurfaceSource.PlatformName}: {sessionDiagnostics}");
        }

        var surfaceLease = new VulkanSurfaceLease(window.VulkanSurfaceSource, (ulong)Instance.Handle, surfaceHandle);
        try
        {
            var surface = new SurfaceKHR { Handle = surfaceLease.Handle };
            if (_physicalDevice.Handle == default && !SelectPhysicalDevice(surface, sessionDiagnostics))
            {
                throw new InvalidOperationException("Failed to find a Vulkan device/queue pair for this surface.");
            }

            if (Device.Handle == default && !CreateLogicalDevice(surface, sessionDiagnostics))
            {
                throw new InvalidOperationException("Failed to create Vulkan logical device.");
            }

            if (_khrSwapchain is null && !Api.TryGetDeviceExtension(Instance, Device, out _khrSwapchain, string.Empty))
            {
                throw new InvalidOperationException("Failed to load VK_KHR_swapchain device extension.");
            }

            return VulkanWindowSession.Create(this, surfaceLease, window.Id, window.Metrics);
        }
        catch (Exception exception)
        {
            if (!surfaceLease.TryRelease(out var destroyDiagnostics))
            {
                sessionDiagnostics.Merge(destroyDiagnostics);
                throw new AggregateException(
                    $"Vulkan window session creation failed and surface cleanup also failed: {sessionDiagnostics}",
                    exception,
                    new InvalidOperationException(sessionDiagnostics.ToString()));
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    /// <summary>
    /// Creates a Vulkan frame session backed by an offscreen color image. The returned graph
    /// follows the same Build/Execute path as a window session, but Execute submits and waits
    /// for completion instead of acquiring or presenting a swapchain image.
    /// </summary>
    public VulkanWindowSession CreateHeadlessSession(uint width, uint height)
    {
        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The headless render target width must be greater than zero.");
        }

        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "The headless render target height must be greater than zero.");
        }

        var diagnostics = new RenderDiagnosticBag();
        if (!IsInitialized)
        {
            InitializeForHeadless(diagnostics);
            if (!IsInitialized)
            {
                throw new InvalidOperationException($"Failed to initialize headless Vulkan rendering. Diagnostics: {diagnostics}");
            }
        }

        return VulkanWindowSession.CreateHeadless(this, new WindowMetrics(width, height, 1f));
    }

    internal Queue GetGraphicsQueue() => _graphicsQueue;
    internal Queue GetPresentQueue() => _presentQueue;
    internal uint GetGraphicsFamily() => _graphicsFamily;
    internal uint GetPresentFamily() => _presentFamily;
    internal PhysicalDevice GetPhysicalDevice() => _physicalDevice;
    internal KhrSurface GetKhrSurface() => SurfaceExtension;
    internal KhrSwapchain GetKhrSwapchain() => SwapchainExtension;
    internal Device GetDevice() => Device;

    private void InitializeForWindow(IVulkanWindowSurfaceSource surfaceSource, RenderDiagnosticBag diagnostics)
    {
        if (IsInitialized)
        {
            return;
        }

        if (!InitializeInstance(surfaceSource, diagnostics))
        {
            return;
        }

        IsInitialized = true;
    }

    private void InitializeForHeadless(RenderDiagnosticBag diagnostics)
    {
        if (IsInitialized)
        {
            return;
        }

        if (!InitializeInstance(null, diagnostics) ||
            !SelectPhysicalDeviceForHeadless(diagnostics) ||
            !CreateLogicalDeviceForHeadless(diagnostics))
        {
            return;
        }

        _initializedHeadless = true;
        IsInitialized = true;
    }

    private unsafe bool InitializeInstance(IVulkanWindowSurfaceSource? surfaceSource, RenderDiagnosticBag diagnostics)
    {
        string[] platformExtensions;
        var extensionDiagnostics = new RenderDiagnosticBag();
        if (surfaceSource is null)
        {
            platformExtensions = Array.Empty<string>();
        }
        else if (!surfaceSource.TryGetRequiredInstanceExtensions(out platformExtensions, out extensionDiagnostics))
        {
            diagnostics.Merge(extensionDiagnostics);
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-INSTANCE", "Failed to get SDL Vulkan extensions.");
            return false;
        }

        if (surfaceSource is not null)
        {
            diagnostics.Merge(extensionDiagnostics);
        }

        var requiredExtensions = new HashSet<string>(platformExtensions);
        if (surfaceSource is not null)
        {
            requiredExtensions.Add(KhrSurface.ExtensionName);
        }

        var portabilityEnumerationRequested = surfaceSource?.SupportsPortabilityEnumeration ?? true;
        var portabilityEnumerationEnabled = portabilityEnumerationRequested &&
                                            IsInstanceExtensionPresent("VK_KHR_portability_enumeration");
        if (portabilityEnumerationEnabled)
        {
            requiredExtensions.Add("VK_KHR_portability_enumeration");
        }
        else if (portabilityEnumerationRequested)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-PORTABILITY", "VK_KHR_portability_enumeration is not exposed by the loader; continuing without the optional instance extension.");
        }

        if (Options.EnableValidation && IsInstanceExtensionPresent(ExtDebugUtils.ExtensionName))
        {
            requiredExtensions.Add(ExtDebugUtils.ExtensionName);
            IsValidationEnabled = true;
        }

        var extensionList = requiredExtensions.ToArray();
        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(extensionList);

        byte[] appNameBytes = Encoding.UTF8.GetBytes(Options.ApplicationName + '\0');
        byte[] engineNameBytes = Encoding.UTF8.GetBytes(Options.EngineName + '\0');

        try
        {
            fixed (byte* appName = appNameBytes)
            fixed (byte* engineName = engineNameBytes)
            {
                ApplicationInfo appInfo = new()
                {
                    SType = StructureType.ApplicationInfo,
                    PNext = null,
                    PApplicationName = appName,
                    PEngineName = engineName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Options.ApiVersion
                };

                var instanceCreateInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                    EnabledExtensionCount = (uint)extensionList.Length,
                    PpEnabledExtensionNames = extensionPointers,
                    EnabledLayerCount = 0,
                    PpEnabledLayerNames = null,
                    PNext = null,
                    Flags = portabilityEnumerationEnabled
                        ? InstanceCreateFlags.EnumeratePortabilityBitKhr
                        : InstanceCreateFlags.None
                };

                var result = Api.CreateInstance(instanceCreateInfo, null, out var instance);
                if (result != Result.Success)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-INSTANCE", $"CreateInstance failed: {result}");
                    return false;
                }

                Instance = instance;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)extensionPointers);
        }

        if (surfaceSource is not null && !Api.TryGetInstanceExtension(Instance, out _khrSurface))
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-INSTANCE", "VK_KHR_surface was not available in created instance.");
            return false;
        }

        if (IsValidationEnabled)
        {
            if (Api.TryGetInstanceExtension(Instance, out _extDebug))
            {
                _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugUtilsCallback);
                _ = DebugExtension.CreateDebugUtilsMessenger(
                        Instance,
                        new DebugUtilsMessengerCreateInfoEXT
                        {
                            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                                          DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt |
                                            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt,
                            PNext = null,
                            PfnUserCallback = _debugCallback
                        }, null, out _debugMessenger);
            }
            else
            {
                IsValidationEnabled = false;
                diagnostics.Add(RenderDiagnosticSeverity.Warning, "VK-VALID", "VK_EXT_debug_utils requested but not available.");
            }
        }

        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-INSTANCE", "Vulkan instance initialized.");
        return true;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes extension counts through unsafe out pointers that the analyzer cannot model.")]
    private bool IsInstanceExtensionPresent(string extensionName)
    {
        unsafe
        {
            uint extensionCount = 0;
            _ = Api.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null);
            if (extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[(int)extensionCount];
            _ = Api.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, properties);

            foreach (var property in properties)
            {
                var candidate = Marshal.PtrToStringAnsi((nint)property.ExtensionName);
                if (candidate == extensionName)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes device counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool SelectPhysicalDevice(SurfaceKHR surface, RenderDiagnosticBag diagnostics)
    {
        uint deviceCount = 0;
        var enumResult = Api.EnumeratePhysicalDevices(Instance, &deviceCount, null);
        if (enumResult != Result.Success || deviceCount == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No Vulkan physical devices are available.");
            return false;
        }

        var devices = new PhysicalDevice[(int)deviceCount];
        enumResult = Api.EnumeratePhysicalDevices(Instance, &deviceCount, devices);
        if (enumResult != Result.Success)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"EnumeratePhysicalDevices failed: {enumResult}");
            return false;
        }

        for (var i = 0; i < devices.Length; i++)
        {
            var candidate = devices[i];
            if (!IsDeviceSuitable(candidate, surface))
            {
                continue;
            }

            _physicalDevice = candidate;
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Suitable physical device selected.");
            return true;
        }

        diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "No physical device supports graphics+present with swapchain.");
        return false;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool SelectPhysicalDeviceForHeadless(RenderDiagnosticBag diagnostics)
    {
        uint deviceCount = 0;
        var enumResult = Api.EnumeratePhysicalDevices(Instance, &deviceCount, null);
        if (enumResult != Result.Success || deviceCount == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No Vulkan physical devices are available.");
            return false;
        }

        var devices = new PhysicalDevice[(int)deviceCount];
        enumResult = Api.EnumeratePhysicalDevices(Instance, &deviceCount, devices);
        if (enumResult != Result.Success)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"EnumeratePhysicalDevices failed: {enumResult}");
            return false;
        }

        for (var i = 0; i < devices.Length; i++)
        {
            var candidate = devices[i];
            if (!TryGetGraphicsQueueFamily(candidate, out var graphicsFamily))
            {
                continue;
            }

            _physicalDevice = candidate;
            _graphicsFamily = graphicsFamily;
            _presentFamily = graphicsFamily;
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Suitable headless graphics device selected.");
            return true;
        }

        diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "No physical device exposes a graphics queue for headless rendering.");
        return false;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool TryGetGraphicsQueueFamily(PhysicalDevice physicalDevice, out uint graphicsFamily)
    {
        graphicsFamily = uint.MaxValue;
        uint queueFamilyCount = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            return false;
        }

        var families = new QueueFamilyProperties[(int)queueFamilyCount];
        Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, families);
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (families[(int)i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamily = i;
                return true;
            }
        }

        return false;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private bool IsDeviceSuitable(PhysicalDevice physicalDevice, SurfaceKHR surface)
    {
        if (!DeviceSupportsSwapchainExtensions(physicalDevice, KhrSwapchain.ExtensionName))
        {
            return false;
        }

        if (!HasSwapChainDetails(surface, physicalDevice))
        {
            return false;
        }

        uint queueFamilyCount = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            return false;
        }

        var queueFamilies = new QueueFamilyProperties[(int)queueFamilyCount];
        Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, queueFamilies);

        bool hasGraphics = false;
        bool hasPresent = false;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (!hasGraphics && queueFamilies[(int)i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                hasGraphics = true;
            }

            _ = SurfaceExtension.GetPhysicalDeviceSurfaceSupport(physicalDevice, i, surface, out var canPresent);
            if (!hasPresent && canPresent)
            {
                hasPresent = true;
            }
        }

        return hasGraphics && hasPresent;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes extension counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool DeviceSupportsSwapchainExtensions(PhysicalDevice device, params string[] requiredExtensions)
    {
        uint extensionCount = 0;
        _ = Api.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);
        if (extensionCount == 0)
        {
            return false;
        }

        var available = new ExtensionProperties[(int)extensionCount];
        _ = Api.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, available);

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
    private unsafe bool HasSwapChainDetails(SurfaceKHR surface, PhysicalDevice physicalDevice)
    {
        _ = SurfaceExtension.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out _);
        uint formatCount = 0;
        _ = SurfaceExtension.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, null);
        uint modeCount = 0;
        _ = SurfaceExtension.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, &modeCount, null);
        return formatCount != 0 && modeCount != 0;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool CreateLogicalDevice(SurfaceKHR surface, RenderDiagnosticBag diagnostics)
    {
        if (_physicalDevice.Handle == default)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "Physical device not selected.");
            return false;
        }

        uint queueFamilyCount = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No queue families were reported.");
            return false;
        }

        var families = new QueueFamilyProperties[(int)queueFamilyCount];
        Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, families);

        uint graphicsFamily = uint.MaxValue;
        uint presentFamily = uint.MaxValue;

        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (families[(int)i].QueueFlags.HasFlag(QueueFlags.GraphicsBit) && graphicsFamily == uint.MaxValue)
            {
                graphicsFamily = i;
            }

            _ = SurfaceExtension.GetPhysicalDeviceSurfaceSupport(_physicalDevice, i, surface, out var canPresent);
            if (canPresent && presentFamily == uint.MaxValue)
            {
                presentFamily = i;
            }
        }

        if (graphicsFamily == uint.MaxValue || presentFamily == uint.MaxValue)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "Could not identify a compatible graphics/present queue family.");
            return false;
        }

        _graphicsFamily = graphicsFamily;
        _presentFamily = presentFamily;

        var queueFamilies = graphicsFamily == presentFamily
            ? new[] { graphicsFamily }
            : new[] { graphicsFamily, presentFamily };

        var queuePriorities = new float[queueFamilies.Length];
        for (var i = 0; i < queuePriorities.Length; i++)
        {
            queuePriorities[i] = 1.0f;
        }

        var deviceExtensions = new List<string> { KhrSwapchain.ExtensionName };
        if (DeviceSupportsSwapchainExtensions(_physicalDevice, "VK_KHR_portability_subset"))
        {
            deviceExtensions.Add("VK_KHR_portability_subset");
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-PORTABILITY", "VK_KHR_portability_subset enabled on the selected device.");
        }

        var queueCreateInfos = new DeviceQueueCreateInfo[queueFamilies.Length];
        fixed (float* pQueuePriority = queuePriorities)
        {
            for (var i = 0; i < queueFamilies.Length; i++)
            {
                queueCreateInfos[i] = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = queueFamilies[i],
                    QueueCount = 1,
                    PQueuePriorities = pQueuePriority + i
                };
            }

            fixed (DeviceQueueCreateInfo* pQueueCreateInfos = queueCreateInfos)
            {
                DeviceCreateInfo createInfo = new()
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                    PQueueCreateInfos = pQueueCreateInfos,
                    PEnabledFeatures = null,
                    EnabledExtensionCount = (uint)deviceExtensions.Count,
                    PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions.ToArray()),
                    EnabledLayerCount = 0,
                    Flags = 0,
                };

                var result = Api.CreateDevice(_physicalDevice, createInfo, null, out var device);
                SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

                if (result != Result.Success)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"CreateDevice failed: {result}");
                    return false;
                }

                Device = device;
            }
        }

        _graphicsQueue = Api.GetDeviceQueue(Device, _graphicsFamily, 0);
        _presentQueue = Api.GetDeviceQueue(Device, _presentFamily, 0);

        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Logical device created.");
        return true;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool CreateLogicalDeviceForHeadless(RenderDiagnosticBag diagnostics)
    {
        if (_physicalDevice.Handle == default || _graphicsFamily == uint.MaxValue)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "Headless physical device or graphics queue was not selected.");
            return false;
        }

        var queuePriority = 1f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        var deviceExtensions = new List<string>();
        if (DeviceSupportsSwapchainExtensions(_physicalDevice, "VK_KHR_portability_subset"))
        {
            deviceExtensions.Add("VK_KHR_portability_subset");
            diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-PORTABILITY", "VK_KHR_portability_subset enabled on the selected headless device.");
        }

        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions.ToArray());
        try
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                PEnabledFeatures = null,
                EnabledExtensionCount = (uint)deviceExtensions.Count,
                PpEnabledExtensionNames = extensionPointers,
                EnabledLayerCount = 0,
                PpEnabledLayerNames = null,
                Flags = 0
            };

            var result = Api.CreateDevice(_physicalDevice, createInfo, null, out var device);
            if (result != Result.Success)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"CreateDevice failed: {result}");
                return false;
            }

            Device = device;
        }
        finally
        {
            if (extensionPointers != null)
            {
                SilkMarshal.Free((nint)extensionPointers);
            }
        }

        _graphicsQueue = Api.GetDeviceQueue(Device, _graphicsFamily, 0);
        _presentQueue = _graphicsQueue;
        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Headless logical device created.");
        return true;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes surface format and present-mode counts through unsafe out pointers that the analyzer cannot model.")]
    internal unsafe bool QuerySwapchainSupport(SurfaceKHR surface, out SurfaceCapabilitiesKHR capabilities, out SurfaceFormatKHR[] formats, out PresentModeKHR[] presentModes)
    {
        capabilities = default;
        formats = Array.Empty<SurfaceFormatKHR>();
        presentModes = Array.Empty<PresentModeKHR>();

        if (surface.Handle == 0 || _physicalDevice.Handle == default)
        {
            return false;
        }

        var capabilitiesResult = SurfaceExtension.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, surface, out capabilities);
        if (capabilitiesResult != Result.Success)
        {
            return false;
        }

        uint formatCount = 0;
        _ = SurfaceExtension.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, &formatCount, null);
        if (formatCount == 0)
        {
            return false;
        }

        formats = new SurfaceFormatKHR[(int)formatCount];
        fixed (SurfaceFormatKHR* pFormats = formats)
        {
            _ = SurfaceExtension.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, &formatCount, pFormats);
        }

        uint presentModeCount = 0;
        _ = SurfaceExtension.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, surface, &presentModeCount, null);
        if (presentModeCount == 0)
        {
            presentModes = Array.Empty<PresentModeKHR>();
            return false;
        }

        presentModes = new PresentModeKHR[(int)presentModeCount];
        fixed (PresentModeKHR* pPresentModes = presentModes)
        {
            _ = SurfaceExtension.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, surface, &presentModeCount, pPresentModes);
        }

        return true;
    }

    private static unsafe Format ChooseSurfaceFormat(ReadOnlySpan<SurfaceFormatKHR> formats)
    {
        if (formats.Length == 0)
        {
            return Format.B8G8R8A8Unorm;
        }

        foreach (var format in formats)
        {
            if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format.Format;
            }
        }

        return formats[0].Format;
    }

    private static unsafe PresentModeKHR ChoosePresentMode(ReadOnlySpan<PresentModeKHR> presentModes)
    {
        foreach (var mode in presentModes)
        {
            if (mode == PresentModeKHR.MailboxKhr)
            {
                return mode;
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    private static unsafe uint DebugUtilsCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        return 0;
    }

    private ValueTask DisposeResourcesAsync()
    {
        if (Device.Handle != default)
        {
            Api.DeviceWaitIdle(Device);
            Api.DestroyDevice(Device, null);
            Device = default;
        }

        if (IsValidationEnabled && _debugMessenger.Handle != default)
        {
            DebugExtension.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
            _debugMessenger = default;
        }

        if (Instance.Handle != default)
        {
            Api.DestroyInstance(Instance, null);
            Instance = default;
        }

        if (_extDebug is not null)
        {
            _extDebug.Dispose();
            _extDebug = null;
        }

        if (_khrSwapchain is not null)
        {
            _khrSwapchain.Dispose();
            _khrSwapchain = null;
        }

        if (_khrSurface is not null)
        {
            _khrSurface.Dispose();
            _khrSurface = null;
        }

        _nativeContext?.Dispose();

        _initializedHeadless = false;
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}

public sealed unsafe class VulkanWindowSession : IRenderFrameSession, IVulkanTextAtlasCommandContext
{
    private bool _disposed;
    private bool _inFrame;
    private readonly bool _headless;

    private readonly VulkanRenderer _renderer;
    private readonly RenderWindowId _windowId;
    private readonly RenderSurfaceHandle _surfaceHandle;
    private readonly SurfaceKHR _surface;
    private readonly VulkanSurfaceLease? _surfaceLease;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The window session borrows extension loaders owned and disposed by VulkanRenderer.")]
    private readonly KhrSwapchain? _khrSwapchain;
    private readonly Queue _graphicsQueue;
    private readonly Queue _presentQueue;
    private readonly Device _device;
    private readonly uint _graphicsFamily;
    private readonly uint _presentFamily;

    private readonly RenderPass _renderPass;
    private SwapchainKHR _swapchain;
    private ImageView[] _imageViews;
    private Framebuffer[] _frameBuffers;
    private Image _headlessImage;
    private DeviceMemory _headlessMemory;
    private ImageView _headlessImageView;
    private Framebuffer _headlessFramebuffer;

    private readonly VulkanSemaphore _imageAvailable;
    private readonly VulkanSemaphore _renderComplete;
    private readonly Fence _renderFence;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly List<IGraphicsPipeline> _graphicsPipelines = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The shared atlas service is disposed by the window-session cleanup path after its Vulkan resources are idle.")]
    private readonly VulkanTextAtlasService _textAtlas;
    private readonly VulkanGraphResourceRegistry _resourceRegistry = new();
    private readonly List<VulkanRenderGraph> _renderGraphs = new();

    private Extent2D _extent;
    private readonly Format _imageFormat;
    private WindowMetrics _metrics;
    private uint _activeImageIndex;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private static long _nextSurfaceHandle;

    internal static VulkanWindowSession Create(VulkanRenderer renderer, VulkanSurfaceLease surfaceLease, RenderWindowId windowId, WindowMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surfaceLease);
        var surface = new SurfaceKHR { Handle = surfaceLease.Handle };
        return new VulkanWindowSession(renderer, surfaceLease, surface, windowId, metrics);
    }

    internal static VulkanWindowSession CreateHeadless(VulkanRenderer renderer, WindowMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return new VulkanWindowSession(renderer, metrics);
    }

    private VulkanWindowSession(VulkanRenderer renderer, VulkanSurfaceLease surfaceLease, SurfaceKHR surface, RenderWindowId windowId, WindowMetrics metrics)
    {
        _headless = false;
        _renderer = renderer;
        _windowId = windowId;
        _surfaceHandle = new RenderSurfaceHandle(
            unchecked((ulong)Interlocked.Increment(ref _nextSurfaceHandle)),
            1);
        _surface = surface;
        _surfaceLease = surfaceLease;

        _device = renderer.GetDevice();
        _graphicsQueue = renderer.GetGraphicsQueue();
        _presentQueue = renderer.GetPresentQueue();
        _graphicsFamily = renderer.GetGraphicsFamily();
        _presentFamily = renderer.GetPresentFamily();
        _khrSwapchain = renderer.GetKhrSwapchain();

        var acquirer = new VulkanSessionResourceAcquirer();
        RenderPass renderPass = default;
        SwapchainKHR swapchain = default;
        ImageView[] imageViews = [];
        Framebuffer[] frameBuffers = [];
        VulkanSemaphore imageAvailable = default;
        VulkanSemaphore renderComplete = default;
        Fence renderFence = default;
        CommandPool commandPool = default;
        CommandBuffer commandBuffer = default;
        PhysicalDeviceMemoryProperties memoryProperties = default;
        VulkanTextAtlasService? textAtlas = null;
        SurfaceCapabilitiesKHR capabilities = default;
        SurfaceFormatKHR[] formats = [];
        PresentModeKHR[] modes = [];
        try
        {
            _extent = new Extent2D(Math.Max(1, metrics.Width), Math.Max(1, metrics.Height));
            _metrics = metrics;
            if (!_renderer.QuerySwapchainSupport(_surface, out capabilities, out formats, out modes))
            {
                throw new InvalidOperationException("No swapchain support for selected surface.");
            }

            _imageFormat = ChooseSurfaceFormat(formats);
            acquirer.Acquire(
                stage => AcquireNativeResource(stage),
                stage => CleanupNativeResource(stage));

            _renderPass = renderPass;
            _swapchain = swapchain;
            _imageViews = imageViews;
            _frameBuffers = frameBuffers;
            _imageAvailable = imageAvailable;
            _renderComplete = renderComplete;
            _renderFence = renderFence;
            _commandPool = commandPool;
            _commandBuffer = commandBuffer;
            _memoryProperties = memoryProperties;
            _textAtlas = textAtlas ?? throw new InvalidOperationException("Text atlas initialization did not complete.");
            surfaceLease.TransferToSession();
            acquirer.Commit();
        }
        catch (Exception exception)
        {
            acquirer.RollbackPreserving(exception);
            throw;
        }

        void AcquireNativeResource(VulkanSessionResourceStage stage)
        {
            switch (stage)
            {
                case VulkanSessionResourceStage.RenderPass:
                    renderPass = CreateRenderPass(renderer.Api, _device, _imageFormat);
                    break;
                case VulkanSessionResourceStage.Swapchain:
                    swapchain = CreateSwapchain(renderer.Api, _device, _graphicsFamily, _presentFamily, _extent, capabilities, formats, modes);
                    break;
                case VulkanSessionResourceStage.SwapchainImages:
                    var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
                    (imageViews, frameBuffers) = CreateSwapchainImageViews(renderer.Api, swapchainExtension, _device, _extent, renderPass, swapchain, _imageFormat);
                    break;
                case VulkanSessionResourceStage.ImageAvailableSemaphore:
                    EnsureSuccess(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out imageAvailable), "CreateSemaphore(imageAvailable)");
                    break;
                case VulkanSessionResourceStage.RenderCompleteSemaphore:
                    EnsureSuccess(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out renderComplete), "CreateSemaphore(renderComplete)");
                    break;
                case VulkanSessionResourceStage.Fence:
                    EnsureSuccess(renderer.Api.CreateFence(_device, new FenceCreateInfo
                    {
                        SType = StructureType.FenceCreateInfo,
                        Flags = FenceCreateFlags.SignaledBit,
                        PNext = null
                    }, null, out renderFence), "CreateFence");
                    break;
                case VulkanSessionResourceStage.CommandPool:
                    EnsureSuccess(renderer.Api.CreateCommandPool(_device, new CommandPoolCreateInfo
                    {
                        SType = StructureType.CommandPoolCreateInfo,
                        QueueFamilyIndex = _graphicsFamily,
                        Flags = CommandPoolCreateFlags.ResetCommandBufferBit
                    }, null, out commandPool), "CreateCommandPool");
                    break;
                case VulkanSessionResourceStage.CommandBuffer:
                    EnsureSuccess(renderer.Api.AllocateCommandBuffers(_device, new CommandBufferAllocateInfo
                    {
                        SType = StructureType.CommandBufferAllocateInfo,
                        CommandBufferCount = 1,
                        CommandPool = commandPool,
                        Level = CommandBufferLevel.Primary
                    }, out commandBuffer), "AllocateCommandBuffers");
                    break;
                case VulkanSessionResourceStage.TextAtlas:
                    memoryProperties = renderer.Api.GetPhysicalDeviceMemoryProperties(renderer.GetPhysicalDevice());
                    textAtlas = new VulkanTextAtlasService(this);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown Vulkan session acquisition stage.");
            }
        }

        void CleanupNativeResource(VulkanSessionResourceStage stage)
        {
            switch (stage)
            {
                case VulkanSessionResourceStage.RenderPass:
                    renderer.Api.DestroyRenderPass(_device, renderPass, null);
                    break;
                case VulkanSessionResourceStage.Swapchain:
                    _khrSwapchain.DestroySwapchain(_device, swapchain, null);
                    break;
                case VulkanSessionResourceStage.SwapchainImages:
                    DestroySwapchainImageViews(renderer.Api, _device, imageViews, frameBuffers);
                    break;
                case VulkanSessionResourceStage.ImageAvailableSemaphore:
                    renderer.Api.DestroySemaphore(_device, imageAvailable, null);
                    break;
                case VulkanSessionResourceStage.RenderCompleteSemaphore:
                    renderer.Api.DestroySemaphore(_device, renderComplete, null);
                    break;
                case VulkanSessionResourceStage.Fence:
                    renderer.Api.DestroyFence(_device, renderFence, null);
                    break;
                case VulkanSessionResourceStage.CommandPool:
                    renderer.Api.DestroyCommandPool(_device, commandPool, null);
                    break;
                case VulkanSessionResourceStage.CommandBuffer:
                    FreeCommandBuffer(renderer.Api, _device, commandPool, commandBuffer);
                    break;
                case VulkanSessionResourceStage.TextAtlas:
                    textAtlas?.Dispose();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown Vulkan session cleanup stage.");
            }
        }
    }

    private VulkanWindowSession(VulkanRenderer renderer, WindowMetrics metrics)
    {
        _headless = true;
        _renderer = renderer;
        _windowId = RenderWindowId.New();
        _surfaceHandle = new RenderSurfaceHandle(
            unchecked((ulong)Interlocked.Increment(ref _nextSurfaceHandle)),
            1);
        _surface = default;
        _surfaceLease = null;
        _khrSwapchain = null;
        _device = renderer.GetDevice();
        _graphicsQueue = renderer.GetGraphicsQueue();
        _presentQueue = _graphicsQueue;
        _graphicsFamily = renderer.GetGraphicsFamily();
        _presentFamily = _graphicsFamily;
        _extent = new Extent2D(Math.Max(1u, metrics.Width), Math.Max(1u, metrics.Height));
        _metrics = metrics;
        _imageFormat = Format.R8G8B8A8Unorm;
        _memoryProperties = renderer.Api.GetPhysicalDeviceMemoryProperties(renderer.GetPhysicalDevice());

        RenderPass renderPass = default;
        Fence renderFence = default;
        CommandPool commandPool = default;
        CommandBuffer commandBuffer = default;
        HeadlessTarget target = default;
        VulkanTextAtlasService? textAtlas = null;
        try
        {
            renderPass = CreateRenderPass(renderer.Api, _device, _imageFormat, ImageLayout.ColorAttachmentOptimal);
            _renderPass = renderPass;
            target = CreateHeadlessTarget(_extent);

            EnsureSuccess(renderer.Api.CreateFence(_device, new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            }, null, out renderFence), "CreateFence(headless)");
            EnsureSuccess(renderer.Api.CreateCommandPool(_device, new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _graphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit
            }, null, out commandPool), "CreateCommandPool(headless)");
            EnsureSuccess(renderer.Api.AllocateCommandBuffers(_device, new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandBufferCount = 1,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary
            }, out commandBuffer), "AllocateCommandBuffers(headless)");

            textAtlas = new VulkanTextAtlasService(this);
            _swapchain = default;
            _imageViews = Array.Empty<ImageView>();
            _frameBuffers = Array.Empty<Framebuffer>();
            _imageAvailable = default;
            _renderComplete = default;
            _renderFence = renderFence;
            _commandPool = commandPool;
            _commandBuffer = commandBuffer;
            _headlessImage = target.Image;
            _headlessMemory = target.Memory;
            _headlessImageView = target.View;
            _headlessFramebuffer = target.Framebuffer;
            _textAtlas = textAtlas ?? throw new InvalidOperationException("Headless text atlas initialization did not complete.");
        }
        catch
        {
            textAtlas?.Dispose();
            if (commandBuffer.Handle != default)
            {
                FreeCommandBuffer(renderer.Api, _device, commandPool, commandBuffer);
            }

            if (commandPool.Handle != default)
            {
                renderer.Api.DestroyCommandPool(_device, commandPool, null);
            }

            if (renderFence.Handle != default)
            {
                renderer.Api.DestroyFence(_device, renderFence, null);
            }

            DestroyHeadlessTarget(renderer.Api, _device, target);
            if (renderPass.Handle != default)
            {
                renderer.Api.DestroyRenderPass(_device, renderPass, null);
            }

            throw;
        }
    }

    internal Vk Api => _renderer.Api;
    internal Device Device => _device;
    internal PhysicalDeviceMemoryProperties MemoryProperties => _memoryProperties;
    internal CommandBuffer CommandBuffer => _commandBuffer;
    internal VulkanGraphResourceRegistry ResourceRegistry => _resourceRegistry;

    internal RenderPass GraphRenderPass => _renderPass;
    internal Framebuffer GraphFramebuffer => _headless ? _headlessFramebuffer : _frameBuffers[(int)_activeImageIndex];
    internal Extent2D GraphExtent => _extent;

    private HeadlessTarget CreateHeadlessTarget(Extent2D extent)
    {
        var api = _renderer.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _imageFormat,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        EnsureSuccess(api.CreateImage(_device, imageInfo, null, out var image), "CreateImage(headless)");
        DeviceMemory memory = default;
        ImageView view = default;
        Framebuffer framebuffer = default;
        try
        {
            var requirements = api.GetImageMemoryRequirements(_device, image);
            var memoryType = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
            EnsureSuccess(api.AllocateMemory(_device, new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType
            }, null, out memory), "AllocateMemory(headless)");
            EnsureSuccess(api.BindImageMemory(_device, image, memory, 0), "BindImageMemory(headless)");
            EnsureSuccess(api.CreateImageView(_device, new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = _imageFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            }, null, out view), "CreateImageView(headless)");

            var imageView = view;
            EnsureSuccess(api.CreateFramebuffer(_device, new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &imageView,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1
            }, null, out framebuffer), "CreateFramebuffer(headless)");
            return new HeadlessTarget(image, memory, view, framebuffer);
        }
        catch
        {
            if (framebuffer.Handle != default) api.DestroyFramebuffer(_device, framebuffer, null);
            if (view.Handle != default) api.DestroyImageView(_device, view, null);
            if (memory.Handle != default) api.FreeMemory(_device, memory, null);
            api.DestroyImage(_device, image, null);
            throw;
        }
    }

    private static void DestroyHeadlessTarget(Vk api, Device device, HeadlessTarget target)
    {
        if (target.Framebuffer.Handle != default) api.DestroyFramebuffer(device, target.Framebuffer, null);
        if (target.View.Handle != default) api.DestroyImageView(device, target.View, null);
        if (target.Image.Handle != default) api.DestroyImage(device, target.Image, null);
        if (target.Memory.Handle != default) api.FreeMemory(device, target.Memory, null);
    }

    private readonly record struct HeadlessTarget(Image Image, DeviceMemory Memory, ImageView View, Framebuffer Framebuffer);

    public IRenderGraph CreateRenderGraph()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var graph = new VulkanRenderGraph(this);
        _renderGraphs.Add(graph);
        return graph;
    }

    internal bool BeginGraphFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inFrame || (!_headless && _frameBuffers.Length == 0))
        {
            return false;
        }

        uint imageIndex = 0;
        if (!_headless)
        {
            var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
            var acquireResult = swapchainExtension.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref imageIndex);
            if (acquireResult != Result.Success)
            {
                return false;
            }

            if (imageIndex >= (uint)_frameBuffers.Length)
            {
                throw new InvalidOperationException($"Vulkan returned swapchain image index {imageIndex}, but only {_frameBuffers.Length} framebuffers exist.");
            }
        }

        if (Api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success ||
            Api.ResetFences(_device, 1, _renderFence) != Result.Success ||
            Api.ResetCommandBuffer(_commandBuffer, 0) != Result.Success)
        {
            return false;
        }

        _activeImageIndex = imageIndex;
        _inFrame = true;

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        if (Api.BeginCommandBuffer(_commandBuffer, beginInfo) == Result.Success)
        {
            return true;
        }

        _inFrame = false;
        return false;
    }

    internal unsafe bool EndGraphFrame()
    {
        var api = _renderer.Api;
        try
        {
            if (api.EndCommandBuffer(_commandBuffer) != Result.Success)
            {
                return false;
            }

            var commandBuffer = _commandBuffer;
            if (_headless)
            {
                var headlessSubmit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer
                };
                return api.QueueSubmit(_graphicsQueue, 1, &headlessSubmit, _renderFence) == Result.Success &&
                       api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) == Result.Success;
            }

            var waitStages = stackalloc PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
            var imageAvailable = _imageAvailable;
            var renderComplete = _renderComplete;
            var imageIndex = _activeImageIndex;
            var swapchain = _swapchain;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderComplete
            };
            if (api.QueueSubmit(_graphicsQueue, 1, &submitInfo, _renderFence) != Result.Success ||
                api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success)
            {
                return false;
            }

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderComplete,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex
            };
            var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
            var presentResult = swapchainExtension.QueuePresent(_presentQueue, presentInfo);
            return (presentResult == Result.Success || presentResult == Result.SuboptimalKhr) &&
                   api.QueueWaitIdle(_presentQueue) == Result.Success;
        }
        finally
        {
            _inFrame = false;
        }
    }

    internal void AbortGraphFrame() => _inFrame = false;

    internal uint MaxBoundDescriptorSets => _renderer.Api.GetPhysicalDeviceProperties(_renderer.GetPhysicalDevice()).Limits.MaxBoundDescriptorSets;

    private static unsafe void FreeCommandBuffer(Vk api, Device device, CommandPool commandPool, CommandBuffer commandBuffer)
    {
        var commandBufferValue = commandBuffer;
        api.FreeCommandBuffers(device, commandPool, 1, &commandBufferValue);
    }

    internal BufferAllocation CreateBuffer(ulong requestedSize, BufferUsageFlags usage, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        var actualSize = Math.Max(requestedSize, 4);
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = actualSize,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        EnsureSuccess(Api.CreateBuffer(_device, createInfo, null, out var buffer), "CreateBuffer");
        var requirements = Api.GetBufferMemoryRequirements(_device, buffer);
        try
        {
            var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, required, preferred);
            var properties = _memoryProperties.MemoryTypes[(int)memoryTypeIndex].PropertyFlags;
            var allocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex
            };
            EnsureSuccess(Api.AllocateMemory(_device, allocateInfo, null, out var memory), "AllocateMemory");
            try
            {
                EnsureSuccess(Api.BindBufferMemory(_device, buffer, memory, 0), "BindBufferMemory");
                return new BufferAllocation(buffer, memory, requirements.Size, properties);
            }
            catch
            {
                Api.FreeMemory(_device, memory, null);
                throw;
            }
        }
        catch
        {
            Api.DestroyBuffer(_device, buffer, null);
            throw;
        }
    }

    internal void DestroyAllocation(BufferAllocation allocation)
    {
        if (allocation.Buffer.Handle != default)
        {
            Api.DestroyBuffer(_device, allocation.Buffer, null);
        }

        if (allocation.Memory.Handle != default)
        {
            Api.FreeMemory(_device, allocation.Memory, null);
        }
    }

    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
            {
                continue;
            }

            var flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if (!flags.HasFlag(required))
            {
                continue;
            }

            if (flags.HasFlag(preferred))
            {
                return i;
            }

            fallback = i;
        }

        if (fallback != uint.MaxValue)
        {
            return fallback;
        }

        throw new InvalidOperationException($"No Vulkan memory type satisfies {required}.");
    }

    public RenderWindowId WindowId => _windowId;

    public RenderSurfaceHandle SurfaceHandle => _surfaceHandle;

    public ITextAtlasDevice CreateTextAtlasDevice() => _textAtlas;

    public IGraphicsPipeline CreateGraphicsPipeline(in IGraphicsShaderProgram shaderProgram)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shaderProgram);
        var pipeline = CreateGraphicsPipelineCore(in shaderProgram);
        _graphicsPipelines.Add(pipeline);
        return pipeline;
    }

    public IGraphicsPipeline CreateTextPipeline(in IGraphicsShaderProgram shaderProgram)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shaderProgram);
        var pipeline = CreateTextPipelineCore(in shaderProgram);
        _graphicsPipelines.Add(pipeline);
        return pipeline;
    }

    Vk IVulkanTextAtlasCommandContext.Api => _renderer.Api;
    Device IVulkanTextAtlasCommandContext.Device => _device;
    PhysicalDeviceMemoryProperties IVulkanTextAtlasCommandContext.MemoryProperties => _memoryProperties;
    CommandBuffer IVulkanTextAtlasCommandContext.CommandBuffer => _commandBuffer;

    bool IVulkanTextAtlasCommandContext.BeginUploadCommands()
    {
        if (_disposed || _inFrame)
        {
            return false;
        }

        var api = _renderer.Api;
        if (api.ResetCommandBuffer(_commandBuffer, 0) != Result.Success)
        {
            return false;
        }

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        return api.BeginCommandBuffer(_commandBuffer, begin) == Result.Success;
    }

    bool IVulkanTextAtlasCommandContext.EndUploadCommands()
    {
        var api = _renderer.Api;
        if (api.EndCommandBuffer(_commandBuffer) != Result.Success)
        {
            return false;
        }

        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };
        if (api.QueueSubmit(_graphicsQueue, 1, &submit, _renderFence) != Result.Success)
        {
            return false;
        }

        if (api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success)
        {
            return false;
        }

        return api.ResetFences(_device, 1, _renderFence) == Result.Success;
    }

    public bool Resize(WindowMetrics metrics)
    {
        if (_disposed)
        {
            return false;
        }

        if (metrics.Width == 0 || metrics.Height == 0)
        {
            _metrics = metrics;
            return true;
        }

        if (_inFrame)
        {
            return false;
        }

        if (_metrics.Width == metrics.Width && _metrics.Height == metrics.Height)
        {
            return true;
        }

        var api = _renderer.Api;
        var device = _renderer.GetDevice();

        if (_headless)
        {
            api.DeviceWaitIdle(device);
            var newExtent = new Extent2D(Math.Max(1u, metrics.Width), Math.Max(1u, metrics.Height));
            var replacement = CreateHeadlessTarget(newExtent);
            var previous = new HeadlessTarget(_headlessImage, _headlessMemory, _headlessImageView, _headlessFramebuffer);
            _headlessImage = replacement.Image;
            _headlessMemory = replacement.Memory;
            _headlessImageView = replacement.View;
            _headlessFramebuffer = replacement.Framebuffer;
            _extent = newExtent;
            _metrics = metrics;
            DestroyHeadlessTarget(api, device, previous);
            return true;
        }

        api.DeviceWaitIdle(device);

        foreach (var frameBuffer in _frameBuffers)
        {
            api.DestroyFramebuffer(device, frameBuffer, null);
        }

        foreach (var imageView in _imageViews)
        {
            api.DestroyImageView(device, imageView, null);
        }

        var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
        swapchainExtension.DestroySwapchain(device, _swapchain, null);

        _extent = new Extent2D(Math.Max(1, metrics.Width), Math.Max(1, metrics.Height));
        _metrics = metrics;

        if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
        {
            return false;
        }

        _swapchain = CreateSwapchain(_renderer.Api, _device, _graphicsFamily, _presentFamily, _extent, capabilities, formats, modes);
        (_imageViews, _frameBuffers) = CreateSwapchainImageViews(_renderer.Api, swapchainExtension, _device, _extent, _renderPass, _swapchain, _imageFormat);
        return _frameBuffers.Length > 0;
    }

    /// <summary>
    /// Copies the completed headless color target to caller-owned R/G/B/A bytes.
    /// The destination layout is row-major with four bytes per pixel.
    /// </summary>
    public bool ReadbackRgba8(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_headless)
        {
            throw new InvalidOperationException("Readback is available only for a headless Vulkan session.");
        }

        var byteCount = checked((int)((ulong)_extent.Width * _extent.Height * 4));
        if (destination.Length < byteCount)
        {
            throw new ArgumentException($"The readback destination must contain at least {byteCount} bytes.", nameof(destination));
        }

        if (_inFrame)
        {
            return false;
        }

        var api = _renderer.Api;
        api.DeviceWaitIdle(_device);
        var staging = CreateBuffer((ulong)byteCount, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        try
        {
            if (api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success ||
                api.ResetFences(_device, 1, _renderFence) != Result.Success ||
                api.ResetCommandBuffer(_commandBuffer, 0) != Result.Success)
            {
                return false;
            }

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            if (api.BeginCommandBuffer(_commandBuffer, beginInfo) != Result.Success)
            {
                return false;
            }

            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _headlessImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            api.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                new[] { toTransfer });

            var copy = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = _extent.Width,
                BufferImageHeight = _extent.Height,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1)
            };
            api.CmdCopyImageToBuffer(_commandBuffer, _headlessImage, ImageLayout.TransferSrcOptimal, staging.Buffer, new[] { copy });

            var toColor = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _headlessImage,
                SubresourceRange = toTransfer.SubresourceRange
            };
            api.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                DependencyFlags.None,
                ReadOnlySpan<MemoryBarrier>.Empty,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                new[] { toColor });

            EnsureSuccess(api.EndCommandBuffer(_commandBuffer), "EndCommandBuffer(headless readback)");
            var commandBuffer = _commandBuffer;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer
            };
            EnsureSuccess(api.QueueSubmit(_graphicsQueue, 1, &submit, _renderFence), "QueueSubmit(headless readback)");
            EnsureSuccess(api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue), "WaitForFences(headless readback)");

            void* mapped = null;
            EnsureSuccess(api.MapMemory(_device, staging.Memory, 0, staging.AllocationSize, 0, &mapped), "MapMemory(headless readback)");
            try
            {
                if (!staging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
                {
                    var range = new MappedMemoryRange
                    {
                        SType = StructureType.MappedMemoryRange,
                        Memory = staging.Memory,
                        Size = (nuint)byteCount
                    };
                    EnsureSuccess(api.InvalidateMappedMemoryRanges(_device, 1, &range), "InvalidateMappedMemoryRanges(headless readback)");
                }

                fixed (byte* target = destination)
                {
                    System.Buffer.MemoryCopy(mapped, target, byteCount, byteCount);
                }
            }
            finally
            {
                api.UnmapMemory(_device, staging.Memory);
            }

            return true;
        }
        finally
        {
            DestroyAllocation(staging);
        }
    }

    private VulkanGraphicsPipeline CreateGraphicsPipelineCore(in IGraphicsShaderProgram shaderProgram)
    {
        if (shaderProgram.Vertex.Abi.Stage != ShaderStage.Vertex ||
            shaderProgram.Fragment.Abi.Stage != ShaderStage.Fragment)
        {
            throw new ArgumentException("Graphics shader programs must contain vertex and fragment stages.", nameof(shaderProgram));
        }

        ValidateGraphicsArtifact(shaderProgram.Vertex, nameof(shaderProgram));
        ValidateGraphicsArtifact(shaderProgram.Fragment, nameof(shaderProgram));

        var vertexEntryPoint = SpirvEntryPointReader.ReadGraphicsEntryPoint(shaderProgram.Vertex.Spirv, vertex: true);
        var fragmentEntryPoint = SpirvEntryPointReader.ReadGraphicsEntryPoint(shaderProgram.Fragment.Spirv, vertex: false);
        if (!string.Equals(vertexEntryPoint, shaderProgram.Vertex.EntryPoint, StringComparison.Ordinal) ||
            !string.Equals(fragmentEntryPoint, shaderProgram.Fragment.EntryPoint, StringComparison.Ordinal))
        {
            throw new ArgumentException("Graphics shader entry points must match their SPIR-V OpEntryPoint names.", nameof(shaderProgram));
        }

        var api = _renderer.Api;
        var vertexWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(shaderProgram.Vertex.Spirv);
        var fragmentWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(shaderProgram.Fragment.Spirv);
        ShaderModule vertexModule = default;
        ShaderModule fragmentModule = default;
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        try
        {
            fixed (uint* vertexCode = vertexWords)
            {
                var shaderInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(vertexWords.Length * sizeof(uint)),
                    PCode = vertexCode
                };
                EnsureSuccess(api.CreateShaderModule(_device, shaderInfo, null, out vertexModule), "CreateShaderModule(vertex)");
            }

            fixed (uint* fragmentCode = fragmentWords)
            {
                var shaderInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(fragmentWords.Length * sizeof(uint)),
                    PCode = fragmentCode
                };
                EnsureSuccess(api.CreateShaderModule(_device, shaderInfo, null, out fragmentModule), "CreateShaderModule(fragment)");
            }

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = GetPushConstantSize(shaderProgram)
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            EnsureSuccess(api.CreatePipelineLayout(_device, pipelineLayoutInfo, null, out pipelineLayout), "CreatePipelineLayout(graphics)");

            var vertexEntryPointBytes = System.Text.Encoding.UTF8.GetBytes(shaderProgram.Vertex.EntryPoint + "\0");
            var fragmentEntryPointBytes = System.Text.Encoding.UTF8.GetBytes(shaderProgram.Fragment.EntryPoint + "\0");
            fixed (byte* vertexEntryPointPtr = vertexEntryPointBytes)
            fixed (byte* fragmentEntryPointPtr = fragmentEntryPointBytes)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = vertexEntryPointPtr
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = fragmentEntryPointPtr
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = Silk.NET.Vulkan.PrimitiveTopology.TriangleList
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1f
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.SrcAlpha,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment
                };
                var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0
                };
                var pipelineOutputs = stackalloc Pipeline[1];
                EnsureSuccess(api.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, pipelineOutputs), "CreateGraphicsPipelines");
                pipeline = pipelineOutputs[0];
            }

            api.DestroyShaderModule(_device, vertexModule, null);
            api.DestroyShaderModule(_device, fragmentModule, null);
            vertexModule = default;
            fragmentModule = default;
            return new VulkanGraphicsPipeline(this, pipelineLayout, pipeline, GetPushConstantSize(shaderProgram));
        }
        catch
        {
            if (vertexModule.Handle != default)
            {
                api.DestroyShaderModule(_device, vertexModule, null);
            }

            if (fragmentModule.Handle != default)
            {
                api.DestroyShaderModule(_device, fragmentModule, null);
            }

            if (pipeline.Handle != default)
            {
                api.DestroyPipeline(_device, pipeline, null);
            }

            if (pipelineLayout.Handle != default)
            {
                api.DestroyPipelineLayout(_device, pipelineLayout, null);
            }

            throw;
        }
    }

    private VulkanTextGraphicsPipeline CreateTextPipelineCore(in IGraphicsShaderProgram shaderProgram)
    {
        if (shaderProgram.Vertex.Abi.Stage != ShaderStage.Vertex ||
            shaderProgram.Fragment.Abi.Stage != ShaderStage.Fragment)
        {
            throw new ArgumentException("Graphics shader programs must contain vertex and fragment stages.", nameof(shaderProgram));
        }

        if (!TextShaderArtifactContract.TryDescribe(shaderProgram, out var layout, out var diagnostic))
        {
            throw new ArgumentException(diagnostic.Message, nameof(shaderProgram));
        }

        var vertexEntryPoint = SpirvEntryPointReader.ReadGraphicsEntryPoint(shaderProgram.Vertex.Spirv, vertex: true);
        var fragmentEntryPoint = SpirvEntryPointReader.ReadGraphicsEntryPoint(shaderProgram.Fragment.Spirv, vertex: false);
        if (!string.Equals(vertexEntryPoint, shaderProgram.Vertex.EntryPoint, StringComparison.Ordinal) ||
            !string.Equals(fragmentEntryPoint, shaderProgram.Fragment.EntryPoint, StringComparison.Ordinal))
        {
            throw new ArgumentException("Graphics shader entry points must match their SPIR-V OpEntryPoint names.", nameof(shaderProgram));
        }

        var api = _renderer.Api;
        var vertexWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(shaderProgram.Vertex.Spirv);
        var fragmentWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(shaderProgram.Fragment.Spirv);
        ShaderModule vertexModule = default;
        ShaderModule fragmentModule = default;
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        DescriptorSetLayout descriptorSetLayout = default;
        DescriptorPool descriptorPool = default;
        DescriptorSet descriptorSet = default;
        try
        {
            fixed (uint* vertexCode = vertexWords)
            {
                var shaderInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(vertexWords.Length * sizeof(uint)),
                    PCode = vertexCode
                };
                EnsureSuccess(api.CreateShaderModule(_device, shaderInfo, null, out vertexModule), "CreateShaderModule(text vertex)");
            }

            fixed (uint* fragmentCode = fragmentWords)
            {
                var shaderInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)(fragmentWords.Length * sizeof(uint)),
                    PCode = fragmentCode
                };
                EnsureSuccess(api.CreateShaderModule(_device, shaderInfo, null, out fragmentModule), "CreateShaderModule(text fragment)");
            }

            var storageBinding = new DescriptorSetLayoutBinding
            {
                Binding = layout.VertexStorageBinding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit
            };
            var samplerBinding = new DescriptorSetLayoutBinding
            {
                Binding = layout.TextureBinding,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            };
            var bindings = stackalloc DescriptorSetLayoutBinding[2] { storageBinding, samplerBinding };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
            };
            EnsureSuccess(api.CreateDescriptorSetLayout(_device, layoutInfo, null, out descriptorSetLayout), "CreateDescriptorSetLayout(text)");

            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 },
                new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes
            };
            EnsureSuccess(api.CreateDescriptorPool(_device, poolInfo, null, out descriptorPool), "CreateDescriptorPool(text)");

            var setLayouts = stackalloc DescriptorSetLayout[1] { descriptorSetLayout };
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = setLayouts
            };
            EnsureSuccess(api.AllocateDescriptorSets(_device, allocateInfo, out descriptorSet), "AllocateDescriptorSets(text)");

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = layout.PushConstantSize
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            EnsureSuccess(api.CreatePipelineLayout(_device, pipelineLayoutInfo, null, out pipelineLayout), "CreatePipelineLayout(text)");

            var vertexEntryPointBytes = Encoding.UTF8.GetBytes(shaderProgram.Vertex.EntryPoint + "\0");
            var fragmentEntryPointBytes = Encoding.UTF8.GetBytes(shaderProgram.Fragment.EntryPoint + "\0");
            fixed (byte* vertexEntryPointPtr = vertexEntryPointBytes)
            fixed (byte* fragmentEntryPointPtr = fragmentEntryPointBytes)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = vertexEntryPointPtr
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = fragmentEntryPointPtr
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = Silk.NET.Vulkan.PrimitiveTopology.TriangleList
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1f
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.SrcAlpha,
                    DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment
                };
                var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0
                };
                var pipelineOutputs = stackalloc Pipeline[1];
                EnsureSuccess(api.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, pipelineOutputs), "CreateGraphicsPipelines(text)");
                pipeline = pipelineOutputs[0];
            }

            api.DestroyShaderModule(_device, vertexModule, null);
            api.DestroyShaderModule(_device, fragmentModule, null);
            vertexModule = default;
            fragmentModule = default;

            return new VulkanTextGraphicsPipeline(this, pipelineLayout, pipeline, descriptorSetLayout, descriptorPool, descriptorSet, layout.PushConstantSize, layout.VertexStorageBinding, layout.TextureBinding);
        }
        catch
        {
            if (vertexModule.Handle != default)
            {
                api.DestroyShaderModule(_device, vertexModule, null);
            }

            if (fragmentModule.Handle != default)
            {
                api.DestroyShaderModule(_device, fragmentModule, null);
            }

            if (pipeline.Handle != default)
            {
                api.DestroyPipeline(_device, pipeline, null);
            }

            if (pipelineLayout.Handle != default)
            {
                api.DestroyPipelineLayout(_device, pipelineLayout, null);
            }

            if (descriptorPool.Handle != default)
            {
                api.DestroyDescriptorPool(_device, descriptorPool, null);
            }

            if (descriptorSetLayout.Handle != default)
            {
                api.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);
            }

            throw;
        }
    }

    private static void ValidateGraphicsArtifact(IShaderArtifact artifact, string parameterName)
    {
        if (artifact.Abi.Stage == ShaderStage.Unknown || string.IsNullOrWhiteSpace(artifact.EntryPoint))
        {
            throw new ArgumentException("Unsupported or incomplete DeltaShader graphics artifact ABI.", parameterName);
        }

        if (artifact.Spirv.Length == 0 || (artifact.Spirv.Length & 3) != 0)
        {
            throw new ArgumentException("Graphics SPIR-V must be non-empty and word aligned.", parameterName);
        }

        if (artifact.Abi.Resources.Count != 0)
        {
            throw new ArgumentException("The initial fullscreen graphics path does not support descriptor resources.", parameterName);
        }
    }

    private static uint GetPushConstantSize(in IGraphicsShaderProgram shaderProgram)
    {
        var vertexSize = shaderProgram.Vertex.Abi.PushConstants.Count > 0 ? shaderProgram.Vertex.Abi.PushConstants[0].Size : 0;
        var fragmentSize = shaderProgram.Fragment.Abi.PushConstants.Count > 0 ? shaderProgram.Fragment.Abi.PushConstants[0].Size : 0;
        if (vertexSize != 0 && fragmentSize != 0 && vertexSize != fragmentSize)
        {
            throw new ArgumentException("Graphics shader stages must use the same push-constant size.", nameof(shaderProgram));
        }

        var size = Math.Max(vertexSize, fragmentSize);
        if (size == 0 || size > 128 || (size & 3) != 0)
        {
            throw new ArgumentException("Graphics shader push-constant metadata must declare a four-byte aligned size up to 128 bytes.", nameof(shaderProgram));
        }

        return size;
    }

    internal void DestroyGraphicsPipeline(VulkanGraphicsPipeline pipeline)
    {
        if (!_graphicsPipelines.Remove(pipeline))
        {
            return;
        }

        if (pipeline.Pipeline.Handle != default)
        {
            _renderer.Api.DestroyPipeline(_device, pipeline.Pipeline, null);
        }
        if (pipeline.PipelineLayout.Handle != default)
        {
            _renderer.Api.DestroyPipelineLayout(_device, pipeline.PipelineLayout, null);
        }
        pipeline.MarkDestroyed();
    }

    internal void DestroyTextGraphicsPipeline(VulkanTextGraphicsPipeline pipeline)
    {
        if (!_graphicsPipelines.Remove(pipeline))
        {
            return;
        }

        var api = _renderer.Api;
        if (pipeline.Pipeline.Handle != default)
        {
            api.DestroyPipeline(_device, pipeline.Pipeline, null);
        }
        if (pipeline.PipelineLayout.Handle != default)
        {
            api.DestroyPipelineLayout(_device, pipeline.PipelineLayout, null);
        }
        if (pipeline.DescriptorPool.Handle != default)
        {
            api.DestroyDescriptorPool(_device, pipeline.DescriptorPool, null);
        }
        if (pipeline.DescriptorSetLayout.Handle != default)
        {
            api.DestroyDescriptorSetLayout(_device, pipeline.DescriptorSetLayout, null);
        }

        var instanceBuffer = pipeline.ReleaseInstanceBuffer();
        if (VulkanBufferAllocation.IsLive(in instanceBuffer))
        {
            DestroyAllocation(instanceBuffer);
        }

        pipeline.MarkDestroyed();
    }

    private static void EnsureSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }

    private static uint GetTextPushConstantSize(in IGraphicsShaderProgram shaderProgram)
    {
        var vertexSize = shaderProgram.Vertex.Abi.PushConstants.Count > 0 ? shaderProgram.Vertex.Abi.PushConstants[0].Size : 0;
        var fragmentSize = shaderProgram.Fragment.Abi.PushConstants.Count > 0 ? shaderProgram.Fragment.Abi.PushConstants[0].Size : 0;
        var size = Math.Max(vertexSize, fragmentSize);
        if (size != (uint)sizeof(TextPushConstants))
        {
            throw new ArgumentException("Text shader push-constant metadata must match the renderer text ABI.", nameof(shaderProgram));
        }

        return size;
    }

    private SwapchainKHR CreateSwapchain(Vk api, Device device, uint graphicsFamily, uint presentFamily, Extent2D extent,
        SurfaceCapabilitiesKHR capabilities, SurfaceFormatKHR[] formats, PresentModeKHR[] presentModes)
    {
        uint maxImages = capabilities.MaxImageCount;
        maxImages = maxImages == 0 ? 2 : maxImages;
        var targetImageCount = Math.Clamp(2u, capabilities.MinImageCount, maxImages);
        var chosenFormat = new SurfaceFormatKHR
        {
            Format = ChooseSurfaceFormat(formats),
            ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr
        };

        var chosenPresentMode = ChoosePresentMode(presentModes);

        SwapchainCreateInfoKHR swapCreateInfo;
        if (graphicsFamily == presentFamily)
        {
            swapCreateInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = targetImageCount,
                ImageFormat = chosenFormat.Format,
                ImageColorSpace = chosenFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                QueueFamilyIndexCount = 0,
                PQueueFamilyIndices = null,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = chosenPresentMode,
                Clipped = true,
                OldSwapchain = default,
                Flags = SwapchainCreateFlagsKHR.None
            };
        }
        else
        {
            var queueFamilyIndices = stackalloc uint[2];
            queueFamilyIndices[0] = graphicsFamily;
            queueFamilyIndices[1] = presentFamily;
            swapCreateInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = targetImageCount,
                ImageFormat = chosenFormat.Format,
                ImageColorSpace = chosenFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = chosenPresentMode,
                Clipped = true,
                OldSwapchain = default,
                Flags = SwapchainCreateFlagsKHR.None
            };
        }

        var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
        var result = swapchainExtension.CreateSwapchain(device, swapCreateInfo, null, out var swapchain);
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"CreateSwapchain failed: {result}");
        }

        return swapchain;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes swapchain image counts through unsafe out pointers that the analyzer cannot model.")]
    private static (ImageView[] ImageViews, Framebuffer[] Framebuffers) CreateSwapchainImageViews(
        Vk api,
        KhrSwapchain khrSwapchain,
        Device device,
        Extent2D extent,
        RenderPass renderPass,
        SwapchainKHR swapchain,
        Format imageFormat
    )
    {
        uint imageCount = 0;
        var countResult = khrSwapchain.GetSwapchainImages(device, swapchain, &imageCount, null);
        if (countResult != Result.Success || imageCount == 0)
        {
            throw new InvalidOperationException($"GetSwapchainImages(count) failed: {countResult}");
        }

        var imageViews = new ImageView[imageCount];
        var frameBuffers = new Framebuffer[imageCount];

        var images = new Image[imageCount];
        fixed (Image* pImages = images)
        {
            var imagesResult = khrSwapchain.GetSwapchainImages(device, swapchain, &imageCount, pImages);
            if (imagesResult != Result.Success)
            {
                throw new InvalidOperationException($"GetSwapchainImages(data) failed: {imagesResult}");
            }
        }

        try
        {
            for (uint i = 0; i < imageCount; i++)
            {
                var imageViewCreateInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = images[i],
                    ViewType = ImageViewType.Type2D,
                    Format = imageFormat,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
                EnsureSuccess(api.CreateImageView(device, imageViewCreateInfo, null, out imageViews[i]), $"CreateImageView[{i}]");

                var imageView = imageViews[i];
                var framebufferCreateInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = extent.Width,
                    Height = extent.Height,
                    Layers = 1
                };
                EnsureSuccess(api.CreateFramebuffer(device, framebufferCreateInfo, null, out frameBuffers[i]), $"CreateFramebuffer[{i}]");
            }
        }
        catch (Exception original)
        {
            try
            {
                DestroySwapchainImageViews(api, device, imageViews, frameBuffers);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Swapchain image cleanup failed after creation failure.", original, cleanupFailure);
            }

            throw;
        }

        return (imageViews, frameBuffers);
    }

    private static void DestroySwapchainImageViews(Vk api, Device device, ImageView[] imageViews, Framebuffer[] frameBuffers)
    {
        List<Exception>? failures = null;
        try
        {
            VulkanResourceCleanup.CleanupInReverse(frameBuffers, frameBuffer =>
            {
                if (frameBuffer.Handle != default)
                {
                    api.DestroyFramebuffer(device, frameBuffer, null);
                }
            });
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            VulkanResourceCleanup.CleanupInReverse(imageViews, imageView =>
            {
                if (imageView.Handle != default)
                {
                    api.DestroyImageView(device, imageView, null);
                }
            });
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more swapchain image resources failed during cleanup.", failures);
        }
    }

    private static RenderPass CreateRenderPass(Vk api, Device device, Format format, ImageLayout finalLayout = ImageLayout.PresentSrcKhr)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = finalLayout,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare
        };

        var attachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachmentRef
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        EnsureSuccess(api.CreateRenderPass(device, renderPassInfo, null, out var renderPass), "CreateRenderPass");
        return renderPass;
    }

    private static unsafe Format ChooseSurfaceFormat(ReadOnlySpan<SurfaceFormatKHR> formats)
    {
        foreach (var format in formats)
        {
            if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format.Format;
            }
        }

        return formats.Length == 0 ? Format.B8G8R8A8Unorm : formats[0].Format;
    }

    private static unsafe PresentModeKHR ChoosePresentMode(ReadOnlySpan<PresentModeKHR> presentModes)
    {
        foreach (var mode in presentModes)
        {
            if (mode == PresentModeKHR.MailboxKhr)
            {
                return mode;
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        var api = _renderer.Api;
        if (_device.Handle != default)
        {
            api.DeviceWaitIdle(_device);
            foreach (var graph in _renderGraphs)
            {
                graph.DisposeSynchronously();
            }

            _renderGraphs.Clear();
            _resourceRegistry.Dispose(this);
            _textAtlas.Dispose();

            foreach (var pipeline in _graphicsPipelines.ToArray())
            {
                switch (pipeline)
                {
                    case VulkanGraphicsPipeline graphicsPipeline:
                        DestroyGraphicsPipeline(graphicsPipeline);
                        break;
                    case VulkanTextGraphicsPipeline textPipeline:
                        DestroyTextGraphicsPipeline(textPipeline);
                        break;
                }
            }

            if (_headless)
            {
                DestroyHeadlessTarget(api, _device, new HeadlessTarget(_headlessImage, _headlessMemory, _headlessImageView, _headlessFramebuffer));
                _headlessImage = default;
                _headlessMemory = default;
                _headlessImageView = default;
                _headlessFramebuffer = default;
            }

            for (var i = 0; i < _frameBuffers.Length; i++)
            {
                api.DestroyFramebuffer(_device, _frameBuffers[i], null);
            }

            for (var i = 0; i < _imageViews.Length; i++)
            {
                api.DestroyImageView(_device, _imageViews[i], null);
            }

            if (!_headless)
            {
                var swapchainExtension = _khrSwapchain ?? throw new InvalidOperationException("Vulkan swapchain extension is not initialized for a window session.");
                swapchainExtension.DestroySwapchain(_device, _swapchain, null);
            }

            api.DestroyRenderPass(_device, _renderPass, null);
            if (_imageAvailable.Handle != default)
            {
                api.DestroySemaphore(_device, _imageAvailable, null);
            }

            if (_renderComplete.Handle != default)
            {
                api.DestroySemaphore(_device, _renderComplete, null);
            }

            api.DestroyFence(_device, _renderFence, null);
            api.DestroyCommandPool(_device, _commandPool, null);
            if (_surfaceLease is not null && !_surfaceLease.TryRelease(out var surfaceDiagnostics))
            {
                throw new InvalidOperationException($"Failed to destroy Vulkan surface: {surfaceDiagnostics}");
            }
        }

        return ValueTask.CompletedTask;
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct GraphicsPushConstants
{
    public float ResolutionX;
    public float ResolutionY;
    public float TimeSeconds;
    public float Reserved;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct UiQuadPushConstants
{
    public float ResolutionX;
    public float ResolutionY;
    public float ReservedX;
    public float ReservedY;
    public float X;
    public float Y;
    public float Width;
    public float Height;
    public float Red;
    public float Green;
    public float Blue;
    public float Alpha;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct TextPushConstants
{
    public float ResolutionX;
    public float ResolutionY;
    public float TimeSeconds;
    public float Reserved;
    public float TextRed;
    public float TextGreen;
    public float TextBlue;
    public float TextAlpha;
    public float OutlineRed;
    public float OutlineGreen;
    public float OutlineBlue;
    public float OutlineAlpha;
    public float OutlineWidth;
    public float Reserved1;
    public float Reserved2;
    public float Reserved3;
}

public sealed unsafe class VulkanGraphicsPipeline : IGraphicsPipeline
{
    internal VulkanGraphicsPipeline(VulkanWindowSession owner, PipelineLayout pipelineLayout, Pipeline pipeline, uint pushConstantSize)
    {
        Owner = owner;
        PipelineLayout = pipelineLayout;
        Pipeline = pipeline;
        PushConstantSize = pushConstantSize;
    }

    internal VulkanWindowSession Owner { get; }
    internal PipelineLayout PipelineLayout { get; private set; }
    internal Pipeline Pipeline { get; private set; }
    internal uint PushConstantSize { get; }
    internal bool IsAlive => Pipeline.Handle != default && PipelineLayout.Handle != default;

    public ValueTask DisposeAsync()
    {
        Owner.DestroyGraphicsPipeline(this);
        return ValueTask.CompletedTask;
    }

    internal void MarkDestroyed()
    {
        PipelineLayout = default;
        Pipeline = default;
    }
}

public sealed unsafe class VulkanTextGraphicsPipeline : IGraphicsPipeline
{
    internal VulkanTextGraphicsPipeline(VulkanWindowSession owner, PipelineLayout pipelineLayout, Pipeline pipeline, DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool, DescriptorSet descriptorSet, uint pushConstantSize, uint storageBinding, uint textureBinding)
    {
        Owner = owner;
        PipelineLayout = pipelineLayout;
        Pipeline = pipeline;
        DescriptorSetLayout = descriptorSetLayout;
        DescriptorPool = descriptorPool;
        DescriptorSet = descriptorSet;
        PushConstantSize = pushConstantSize;
        StorageBinding = storageBinding;
        TextureBinding = textureBinding;
    }

    internal VulkanWindowSession Owner { get; }
    internal PipelineLayout PipelineLayout { get; private set; }
    internal Pipeline Pipeline { get; private set; }
    internal DescriptorSetLayout DescriptorSetLayout { get; private set; }
    internal DescriptorPool DescriptorPool { get; private set; }
    internal DescriptorSet DescriptorSet { get; private set; }
    internal uint PushConstantSize { get; }
    internal uint StorageBinding { get; }
    internal uint TextureBinding { get; }
    internal bool IsAlive => Pipeline.Handle != default && PipelineLayout.Handle != default && DescriptorSetLayout.Handle != default;

    private readonly VulkanBufferLease _instanceBuffer = new();
    private TextGlyphInstance[] _orderedGlyphs = Array.Empty<TextGlyphInstance>();
    private TextGlyphGpu[] _gpuGlyphs = Array.Empty<TextGlyphGpu>();
    private TextBatchRange[] _batchGlyphs = Array.Empty<TextBatchRange>();

    internal Span<TextGlyphInstance> OrderedGlyphs => _orderedGlyphs;
    internal Span<TextGlyphGpu> PackedGlyphs => _gpuGlyphs;
    internal Span<TextBatchRange> BatchGlyphs => _batchGlyphs;

    internal bool EnsureInstanceCapacity(uint glyphCount)
    {
        var requiredBytes = checked((ulong)Math.Max(1u, glyphCount) * (ulong)Unsafe.SizeOf<TextGlyphGpu>());
        EnsureGlyphArrayCapacity(glyphCount);
        if (_instanceBuffer.IsLive && _instanceBuffer.Value.AllocationSize >= requiredBytes)
        {
            return true;
        }

        var previous = _instanceBuffer.Release();
        if (VulkanBufferAllocation.IsLive(in previous))
        {
            Owner.DestroyAllocation(previous);
        }

        var capacity = NextCapacity(requiredBytes);
        var replacement = Owner.CreateBuffer(
                capacity,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _instanceBuffer.Assign(replacement);

        return true;
    }

    internal void UploadGlyphs(ReadOnlySpan<TextGlyphInstance> glyphs)
    {
        if (!_instanceBuffer.IsLive)
        {
            throw new InvalidOperationException("Text instance buffer was not initialized.");
        }

        if (glyphs.Length > _gpuGlyphs.Length)
        {
            throw new InvalidOperationException("Text GPU instance storage was not sized for the upload.");
        }

        TextGlyphGpu.Pack(glyphs, _gpuGlyphs.AsSpan(0, glyphs.Length));
        var bytes = MemoryMarshal.AsBytes(_gpuGlyphs.AsSpan(0, glyphs.Length));
        fixed (byte* source = bytes)
        {
            void* mapped = null;
            var allocation = _instanceBuffer.Value;
            Owner.Api.MapMemory(Owner.Device, allocation.Memory, 0, allocation.AllocationSize, 0, &mapped);
            try
            {
                System.Buffer.MemoryCopy(source, mapped, allocation.AllocationSize, (nuint)bytes.Length);
                if (!allocation.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
                {
                    var range = new MappedMemoryRange
                    {
                        SType = StructureType.MappedMemoryRange,
                        Memory = allocation.Memory,
                        Size = (nuint)bytes.Length
                    };
                    Owner.Api.FlushMappedMemoryRanges(Owner.Device, 1, &range);
                }
            }
            finally
            {
                Owner.Api.UnmapMemory(Owner.Device, allocation.Memory);
            }
        }
    }

    private void EnsureGlyphArrayCapacity(uint glyphCount)
    {
        if (_orderedGlyphs.Length < glyphCount)
        {
            Array.Resize(ref _orderedGlyphs, Math.Max(_orderedGlyphs.Length * 2, (int)glyphCount));
        }

        if (_gpuGlyphs.Length < glyphCount)
        {
            Array.Resize(ref _gpuGlyphs, Math.Max(_gpuGlyphs.Length * 2, (int)glyphCount));
        }

        if (_batchGlyphs.Length < glyphCount)
        {
            Array.Resize(ref _batchGlyphs, Math.Max(_batchGlyphs.Length * 2, (int)glyphCount));
        }
    }

    internal void UpdateDescriptorSet(VulkanTextAtlasPage atlasPage)
    {
        unsafe
        {
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = _instanceBuffer.Value.Buffer,
                Offset = 0,
                Range = _instanceBuffer.Value.AllocationSize
            };
            var imageInfo = new DescriptorImageInfo
            {
                Sampler = atlasPage.Sampler,
                ImageView = atlasPage.ImageView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = DescriptorSet,
                DstBinding = StorageBinding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfo
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = DescriptorSet,
                DstBinding = TextureBinding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo
            };
            Owner.Api.UpdateDescriptorSets(Owner.Device, 2, writes, 0, null);
        }
    }

    private static ulong NextCapacity(ulong requiredBytes)
    {
        var capacity = 64ul;
        while (capacity < requiredBytes)
        {
            capacity <<= 1;
        }

        return capacity;
    }

    public ValueTask DisposeAsync()
    {
        Owner.DestroyTextGraphicsPipeline(this);
        return ValueTask.CompletedTask;
    }

    internal BufferAllocation ReleaseInstanceBuffer() => _instanceBuffer.Release();

    internal void MarkDestroyed()
    {
        PipelineLayout = default;
        Pipeline = default;
        DescriptorSetLayout = default;
        DescriptorPool = default;
        DescriptorSet = default;
        _orderedGlyphs = Array.Empty<TextGlyphInstance>();
        _gpuGlyphs = Array.Empty<TextGlyphGpu>();
        _batchGlyphs = Array.Empty<TextBatchRange>();
    }
}
