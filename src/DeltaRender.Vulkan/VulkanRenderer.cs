using System.Diagnostics.CodeAnalysis;
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

            return VulkanRenderSession.Create(this, surfaceLease, window.Metrics);
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
    public IRenderFrameSession CreateHeadlessSession(uint width, uint height)
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

        return VulkanRenderSession.CreateHeadless(this, new PixelExtent(width, height));
    }

    public IRenderFrameSession CreateComputeSession()
    {
        var diagnostics = new RenderDiagnosticBag();
        if (!IsInitialized)
        {
            InitializeForHeadless(diagnostics);
            if (!IsInitialized)
            {
                throw new InvalidOperationException($"Failed to initialize compute Vulkan rendering. Diagnostics: {diagnostics}");
            }
        }

        return VulkanRenderSession.CreateCompute(this);
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
