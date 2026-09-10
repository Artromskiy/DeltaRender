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
    private static readonly string[] VulkanLoaderLibraryNames = ["libvulkan.1.dylib", "libvulkan.dylib", "vulkan"];
    private readonly DefaultNativeContext? _nativeContext;
    private bool _initializedHeadless;

    public VulkanRenderer(VulkanRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;

        if (OperatingSystem.IsMacOS())
        {
            var software = UsesSoftwareVulkan();
            var loaderPath = software ? Environment.GetEnvironmentVariable("DELTA_RENDER_VULKAN_LOADER") : null;
            _nativeContext = new DefaultNativeContext(
                loaderPath is { Length: > 0 } ? [loaderPath] : software ? VulkanLoaderLibraryNames : MoltenVkLibraryNames);
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

    internal ShaderCapabilities EnabledShaderCapabilities => _enabledShaderCapabilities;

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
    private ShaderCapabilities _enabledShaderCapabilities;
    private uint _graphicsFamily = uint.MaxValue;
    private uint _computeFamily = uint.MaxValue;
    private uint _transferFamily = uint.MaxValue;
    private uint _presentFamily = uint.MaxValue;
    private Queue _graphicsQueue;
    private Queue _computeQueue;
    private Queue _transferQueue;
    private Queue _presentQueue;
    private uint[] _queueFamilies = [];

    public bool IsValidationEnabled { get; private set; }

    private static bool UsesSoftwareVulkan()
    {
        var driver = Environment.GetEnvironmentVariable("DELTA_RENDER_VULKAN_DRIVER");
        return string.Equals(driver, "swiftshader", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driver, "lavapipe", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync() => DisposeResourcesAsync();

    public IRenderFrameSession CreateWindowSession(IRenderWindow window, RenderSessionOptions options = default)
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
            if (_physicalDevice.Handle == default)
            {
                if (!VulkanDeviceQueries.TrySelectPhysicalDevice(Api, Instance, SurfaceExtension, surface, sessionDiagnostics, out var physicalDevice))
                {
                    throw new InvalidOperationException("Failed to find a Vulkan device/queue pair for this surface.");
                }

                _physicalDevice = physicalDevice;
            }

            if (Device.Handle == default && !CreateLogicalDevice(surface, sessionDiagnostics))
            {
                throw new InvalidOperationException("Failed to create Vulkan logical device.");
            }

            if (_khrSwapchain is null && !Api.TryGetDeviceExtension(Instance, Device, out _khrSwapchain, string.Empty))
            {
                throw new InvalidOperationException("Failed to load VK_KHR_swapchain device extension.");
            }

            return VulkanRenderSession.Create(this, surfaceLease, window.Metrics, options);
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
    public IRenderFrameSession CreateHeadlessSession(uint width, uint height, RenderSessionOptions options = default)
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

        return VulkanRenderSession.CreateHeadless(this, new PixelExtent(width, height), options);
    }

    public IRenderFrameSession CreateComputeSession(RenderSessionOptions options = default)
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

        return VulkanRenderSession.CreateCompute(this, options);
    }

    internal VulkanDeviceContext GetDeviceContext(bool windowed)
    {
        var graphicsQueue = _graphicsQueue;
        var graphicsFamily = _graphicsFamily;
        return new VulkanDeviceContext(
            Api,
            _physicalDevice,
            Device,
            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice),
            graphicsQueue,
            _computeQueue,
            _transferQueue,
            windowed ? _presentQueue : graphicsQueue,
            graphicsFamily,
            _computeFamily,
            _transferFamily,
            _queueFamilies,
            windowed ? _presentFamily : graphicsFamily);
    }

    internal KhrSwapchain GetKhrSwapchain() => SwapchainExtension;

    internal bool TryReinitializeDevice(bool windowed, SurfaceKHR surface, RenderDiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!IsInitialized || _physicalDevice.Handle == default)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-RECOVERY", "The Vulkan instance or physical device is not initialized.");
            return false;
        }

        if (_khrSwapchain is not null)
        {
            _khrSwapchain.Dispose();
            _khrSwapchain = null;
        }

        // Device-loss recovery must not wait on the lost device.
        if (Device.Handle != default)
        {
            Api.DestroyDevice(Device, null);
            Device = default;
        }

        var created = windowed
            ? CreateLogicalDevice(surface, diagnostics)
            : CreateLogicalDeviceForHeadless(diagnostics);
        if (!created)
        {
            return false;
        }

        if (windowed && !Api.TryGetDeviceExtension(Instance, Device, out _khrSwapchain, string.Empty))
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-RECOVERY", "Failed to reload VK_KHR_swapchain after device recovery.");
            Api.DestroyDevice(Device, null);
            Device = default;
            return false;
        }

        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-RECOVERY", "Vulkan logical device reinitialized; session resources must be recreated.");
        return true;
    }

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
            !VulkanDeviceQueries.TrySelectHeadlessPhysicalDevice(Api, Instance, diagnostics, out _physicalDevice, out _graphicsFamily) ||
            !CreateLogicalDeviceForHeadless(diagnostics))
        {
            return;
        }

        _initializedHeadless = true;
        IsInitialized = true;
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

        var enabledLayers = Array.Empty<string>();
        if (Options.EnableValidation)
        {
            if (IsInstanceLayerPresent("VK_LAYER_KHRONOS_validation"))
            {
                enabledLayers = ["VK_LAYER_KHRONOS_validation"];
            }
            else
            {
                diagnostics.Add(RenderDiagnosticSeverity.Warning, "VK-VALID", "VK_LAYER_KHRONOS_validation is not available; continuing without the validation layer.");
            }
        }

        var extensionList = requiredExtensions.ToArray();
        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(extensionList);
        var layerPointers = (byte**)SilkMarshal.StringArrayToPtr(enabledLayers);

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
                    EnabledLayerCount = (uint)enabledLayers.Length,
                    PpEnabledLayerNames = layerPointers,
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
            SilkMarshal.Free((nint)layerPointers);
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

    private unsafe bool IsInstanceLayerPresent(string layerName)
    {
        uint layerCount = 0;
        _ = Api.EnumerateInstanceLayerProperties(&layerCount, null);
        if (layerCount == 0)
        {
            return false;
        }

        var properties = new LayerProperties[(int)layerCount];
        _ = Api.EnumerateInstanceLayerProperties(&layerCount, properties);
        foreach (var property in properties)
        {
            var candidate = Marshal.PtrToStringAnsi((nint)property.LayerName);
            if (candidate == layerName)
            {
                return true;
            }
        }

        return false;
    }


    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family counts through unsafe out pointers that the analyzer cannot model.")]
    private unsafe bool CreateLogicalDevice(SurfaceKHR surface, RenderDiagnosticBag diagnostics)
    {
        if (_physicalDevice.Handle == default)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-DEVICE", "Physical device not selected.");
            return false;
        }

        var families = VulkanDeviceQueries.GetQueueFamilyProperties(Api, _physicalDevice);
        if (families.Length == 0)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "No queue families were reported.");
            return false;
        }

        uint graphicsFamily = uint.MaxValue;
        uint presentFamily = uint.MaxValue;

        for (var i = 0; i < families.Length; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit) && graphicsFamily == uint.MaxValue)
            {
                graphicsFamily = (uint)i;
            }

            _ = SurfaceExtension.GetPhysicalDeviceSurfaceSupport(_physicalDevice, (uint)i, surface, out var canPresent);
            if (canPresent && presentFamily == uint.MaxValue)
            {
                presentFamily = (uint)i;
            }
        }

        if (graphicsFamily == uint.MaxValue || presentFamily == uint.MaxValue)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", "Could not identify a compatible graphics/present queue family.");
            return false;
        }

        var queueFamilies = VulkanQueueFamilies.Select(families, graphicsFamily, presentFamily);
        _graphicsFamily = queueFamilies.Graphics;
        _computeFamily = queueFamilies.Compute;
        _transferFamily = queueFamilies.Transfer;
        _presentFamily = queueFamilies.Present;
        _queueFamilies = queueFamilies.ToUniqueArray();

        var deviceExtensions = new List<string> { KhrSwapchain.ExtensionName };
        AddPortabilitySubsetExtensionIfSupported(deviceExtensions, diagnostics);

        if (!TryCreateLogicalDevice(
                _physicalDevice,
                _graphicsFamily,
                _computeFamily,
                _transferFamily,
                _presentFamily,
                includePresentFamily: true,
                deviceExtensions.ToArray(),
                diagnostics,
                out var device,
                out var shaderCapabilities))
        {
            return false;
        }

        Device = device;
        _enabledShaderCapabilities = shaderCapabilities;
        _graphicsQueue = Api.GetDeviceQueue(Device, _graphicsFamily, 0);
        _computeQueue = Api.GetDeviceQueue(Device, _computeFamily, 0);
        _transferQueue = Api.GetDeviceQueue(Device, _transferFamily, 0);
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

        var deviceExtensions = new List<string>();
        AddPortabilitySubsetExtensionIfSupported(deviceExtensions, diagnostics);

        var families = VulkanDeviceQueries.GetQueueFamilyProperties(Api, _physicalDevice);
        var queueFamilies = VulkanQueueFamilies.Select(families, _graphicsFamily, _graphicsFamily);
        _graphicsFamily = queueFamilies.Graphics;
        _computeFamily = queueFamilies.Compute;
        _transferFamily = queueFamilies.Transfer;
        _presentFamily = queueFamilies.Present;
        _queueFamilies = queueFamilies.ToUniqueArray();

        if (!TryCreateLogicalDevice(
                _physicalDevice,
                _graphicsFamily,
                _computeFamily,
                _transferFamily,
                _presentFamily,
                includePresentFamily: false,
                deviceExtensions.ToArray(),
                diagnostics,
                out var device,
                out var shaderCapabilities))
        {
            return false;
        }

        Device = device;
        _enabledShaderCapabilities = shaderCapabilities;
        _graphicsQueue = Api.GetDeviceQueue(Device, _graphicsFamily, 0);
        _computeQueue = Api.GetDeviceQueue(Device, _computeFamily, 0);
        _transferQueue = Api.GetDeviceQueue(Device, _transferFamily, 0);
        _presentQueue = _graphicsQueue;
        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-DEVICE", "Headless logical device created.");
        return true;
    }

    private void AddPortabilitySubsetExtensionIfSupported(List<string> deviceExtensions, RenderDiagnosticBag diagnostics)
    {
        const string portabilitySubsetExtension = "VK_KHR_portability_subset";
        if (!VulkanDeviceQueries.DeviceSupportsSwapchainExtensions(Api, _physicalDevice, portabilitySubsetExtension))
        {
            return;
        }

        deviceExtensions.Add(portabilitySubsetExtension);
        diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-PORTABILITY", "VK_KHR_portability_subset enabled on the selected device.");
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Vulkan FFI writes queue-family pointers and native extension arrays that the analyzer cannot model.")]
    private unsafe bool TryCreateLogicalDevice(
        PhysicalDevice physicalDevice,
        uint graphicsFamily,
        uint computeFamily,
        uint transferFamily,
        uint presentFamily,
        bool includePresentFamily,
        string[] deviceExtensions,
        RenderDiagnosticBag diagnostics,
        out Device device,
        out ShaderCapabilities shaderCapabilities)
    {
        device = default;
        shaderCapabilities = ShaderCapabilities.None;
        Span<uint> queueFamilies = stackalloc uint[4];
        var queueFamilyCount = 0;
        AddQueueFamily(queueFamilies, ref queueFamilyCount, graphicsFamily);
        AddQueueFamily(queueFamilies, ref queueFamilyCount, computeFamily);
        AddQueueFamily(queueFamilies, ref queueFamilyCount, transferFamily);
        if (includePresentFamily)
        {
            AddQueueFamily(queueFamilies, ref queueFamilyCount, presentFamily);
        }

        var queuePriorities = new float[queueFamilyCount];
        Array.Fill(queuePriorities, 1.0f);
        var queueCreateInfos = new DeviceQueueCreateInfo[queueFamilyCount];
        var extensionPointers = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions);

        try
        {
            var availableFeatures = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };
            var availableFloat16Features = new PhysicalDeviceShaderFloat16Int8Features
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };
            var availableStorage16Features = new PhysicalDevice16BitStorageFeatures
            {
                SType = StructureType.PhysicalDevice16BitStorageFeatures,
            };
            availableStorage16Features.PNext = &availableFloat16Features;
            availableFeatures.PNext = &availableStorage16Features;
            Api.GetPhysicalDeviceFeatures2(physicalDevice, &availableFeatures);

            var enabledFeatures = default(PhysicalDeviceFeatures);
            var enabledFloat16Features = new PhysicalDeviceShaderFloat16Int8Features
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };
            var enabledStorage16Features = new PhysicalDevice16BitStorageFeatures
            {
                SType = StructureType.PhysicalDevice16BitStorageFeatures,
                PNext = &enabledFloat16Features,
            };

            if (availableFeatures.Features.ShaderFloat64)
            {
                enabledFeatures.ShaderFloat64 = true;
                shaderCapabilities |= ShaderCapabilities.DoublePrecisionFloatingPoint;
            }

            if (availableFloat16Features.ShaderFloat16 && availableStorage16Features.StorageBuffer16BitAccess)
            {
                enabledFloat16Features.ShaderFloat16 = true;
                enabledStorage16Features.StorageBuffer16BitAccess = true;
                shaderCapabilities |= ShaderCapabilities.HalfPrecisionFloatingPoint;
            }

            fixed (float* priorityPointer = queuePriorities)
            fixed (DeviceQueueCreateInfo* queueCreateInfoPointer = queueCreateInfos)
            {
                for (var i = 0; i < queueFamilyCount; i++)
                {
                    queueCreateInfos[i] = new DeviceQueueCreateInfo
                    {
                        SType = StructureType.DeviceQueueCreateInfo,
                        QueueFamilyIndex = queueFamilies[i],
                        QueueCount = 1,
                        PQueuePriorities = priorityPointer + i,
                    };
                }

                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &enabledStorage16Features,
                    QueueCreateInfoCount = (uint)queueFamilyCount,
                    PQueueCreateInfos = queueCreateInfoPointer,
                    PEnabledFeatures = &enabledFeatures,
                    EnabledExtensionCount = (uint)deviceExtensions.Length,
                    PpEnabledExtensionNames = extensionPointers,
                    EnabledLayerCount = 0,
                    Flags = 0,
                };
                var result = Api.CreateDevice(physicalDevice, createInfo, null, out device);
                if (result == Result.Success)
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Info, "VK-FEATURES", $"Enabled shader capabilities: {shaderCapabilities}.");
                    return true;
                }

                diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-DEVICE", $"CreateDevice failed: {result}");
                device = default;
                return false;
            }
        }

        finally
        {
            if (extensionPointers != null)
            {
                SilkMarshal.Free((nint)extensionPointers);
            }
        }
    }

    private static void AddQueueFamily(Span<uint> queueFamilies, ref int queueFamilyCount, uint family)
    {
        for (var index = 0; index < queueFamilyCount; index++)
        {
            if (queueFamilies[index] == family)
            {
                return;
            }
        }

        queueFamilies[queueFamilyCount++] = family;
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
