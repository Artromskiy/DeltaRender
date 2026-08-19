using System.Runtime.InteropServices;
using System.Text;
using Delta.Render.Core;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

public sealed unsafe class VulkanRenderer : IAsyncDisposable
{
    private readonly INativeContext? _nativeContext;

    public VulkanRenderer(VulkanRendererOptions options)
    {
        Options = options;

        if (OperatingSystem.IsMacOS())
        {
            _nativeContext = new DefaultNativeContext(new[] { "libMoltenVK.dylib", "MoltenVK" });
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

    private KhrSurface _khrSurface = null!;
    private KhrSwapchain _khrSwapchain = null!;
    private ExtDebugUtils _extDebug = null!;

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

    public IRenderWindowFrameSession CreateWindowSession(IRenderWindow window)
    {
        var sessionDiagnostics = new RenderDiagnosticBag();
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
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

        var surface = new SurfaceKHR { Handle = surfaceHandle };

        if (_physicalDevice.Handle == default)
        {
            if (!SelectPhysicalDevice(surface, sessionDiagnostics))
            {
                if (!window.VulkanSurfaceSource.TryDestroySurface((ulong)Instance.Handle, surfaceHandle, out var destroyDiagnostics))
                {
                    sessionDiagnostics.Merge(destroyDiagnostics);
                }

                throw new InvalidOperationException("Failed to find a Vulkan device/queue pair for this surface.");
            }
        }

        if (Device.Handle == default)
        {
            if (!CreateLogicalDevice(surface, sessionDiagnostics))
            {
                if (!window.VulkanSurfaceSource.TryDestroySurface((ulong)Instance.Handle, surfaceHandle, out var destroyDiagnostics))
                {
                    sessionDiagnostics.Merge(destroyDiagnostics);
                }

                throw new InvalidOperationException("Failed to create Vulkan logical device.");
            }
        }

        try
        {
            if (_khrSwapchain is null && !Api.TryGetDeviceExtension(Instance, Device, out _khrSwapchain, string.Empty))
            {
                throw new InvalidOperationException("Failed to load VK_KHR_swapchain device extension.");
            }

            return new VulkanWindowSession(this, surface, window.Id, window.Metrics);
        }
        catch
        {
            if (!window.VulkanSurfaceSource.TryDestroySurface((ulong)Instance.Handle, surfaceHandle, out var destroyDiagnostics))
            {
                sessionDiagnostics.Merge(destroyDiagnostics);
            }

            throw;
        }
    }

    internal Queue GetGraphicsQueue() => _graphicsQueue;
    internal Queue GetPresentQueue() => _presentQueue;
    internal uint GetGraphicsFamily() => _graphicsFamily;
    internal uint GetPresentFamily() => _presentFamily;
    internal PhysicalDevice GetPhysicalDevice() => _physicalDevice;
    internal KhrSurface GetKhrSurface() => _khrSurface;
    internal KhrSwapchain GetKhrSwapchain() => _khrSwapchain;
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

    private unsafe bool InitializeInstance(IVulkanWindowSurfaceSource surfaceSource, RenderDiagnosticBag diagnostics)
    {
        if (!surfaceSource.TryGetRequiredInstanceExtensions(out var platformExtensions, out var extensionDiagnostics))
        {
            diagnostics.Merge(extensionDiagnostics);
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-INSTANCE", "Failed to get SDL Vulkan extensions.");
            return false;
        }

        diagnostics.Merge(extensionDiagnostics);

        var requiredExtensions = new HashSet<string>(platformExtensions)
        {
            KhrSurface.ExtensionName
        };

        var portabilityEnumerationEnabled = surfaceSource.SupportsPortabilityEnumeration &&
                                            IsInstanceExtensionPresent("VK_KHR_portability_enumeration");
        if (portabilityEnumerationEnabled)
        {
            requiredExtensions.Add("VK_KHR_portability_enumeration");
        }
        else if (surfaceSource.SupportsPortabilityEnumeration)
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

        if (!Api.TryGetInstanceExtension(Instance, out _khrSurface))
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "VK-INSTANCE", "VK_KHR_surface was not available in created instance.");
            return false;
        }

        if (IsValidationEnabled)
        {
            if (Api.TryGetInstanceExtension(Instance, out _extDebug))
            {
                _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugUtilsCallback);
                _ = _extDebug.CreateDebugUtilsMessenger(
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

            _ = _khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice, i, surface, out var canPresent);
            if (!hasPresent && canPresent)
            {
                hasPresent = true;
            }
        }

        return hasGraphics && hasPresent;
    }

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

    private unsafe bool HasSwapChainDetails(SurfaceKHR surface, PhysicalDevice physicalDevice)
    {
        _ = _khrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out _);
        uint formatCount = 0;
        _ = _khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, null);
        uint modeCount = 0;
        _ = _khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, &modeCount, null);
        return formatCount != 0 && modeCount != 0;
    }

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

            _ = _khrSurface.GetPhysicalDeviceSurfaceSupport(_physicalDevice, i, surface, out var canPresent);
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

    internal unsafe bool QuerySwapchainSupport(SurfaceKHR surface, out SurfaceCapabilitiesKHR capabilities, out SurfaceFormatKHR[] formats, out PresentModeKHR[] presentModes)
    {
        capabilities = default;
            formats = Array.Empty<SurfaceFormatKHR>();
            presentModes = Array.Empty<PresentModeKHR>();

        if (surface.Handle == 0 || _physicalDevice.Handle == default)
        {
            return false;
        }

        var capabilitiesResult = _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, surface, out capabilities);
        if (capabilitiesResult != Result.Success)
        {
            return false;
        }

        uint formatCount = 0;
        _ = _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, &formatCount, null);
        if (formatCount == 0)
        {
            return false;
        }

        formats = new SurfaceFormatKHR[(int)formatCount];
        fixed (SurfaceFormatKHR* pFormats = formats)
        {
            _ = _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, &formatCount, pFormats);
        }

        uint presentModeCount = 0;
        _ = _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, surface, &presentModeCount, null);
        if (presentModeCount == 0)
        {
                presentModes = Array.Empty<PresentModeKHR>();
                return false;
            }

        presentModes = new PresentModeKHR[(int)presentModeCount];
        fixed (PresentModeKHR* pPresentModes = presentModes)
        {
            _ = _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, surface, &presentModeCount, pPresentModes);
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
            _extDebug.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
            _debugMessenger = default;
        }

        if (Instance.Handle != default)
        {
            Api.DestroyInstance(Instance, null);
            Instance = default;
        }

        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}

public sealed unsafe class VulkanWindowSession : IRenderWindowFrameSession
{
    private bool _disposed;
    private bool _inFrame;

    private readonly VulkanRenderer _renderer;
    private readonly RenderWindowId _windowId;
    private readonly SurfaceKHR _surface;
    private readonly KhrSurface _khrSurface;
    private readonly KhrSwapchain _khrSwapchain;
    private readonly Queue _graphicsQueue;
    private readonly Queue _presentQueue;
    private readonly Device _device;
    private readonly uint _graphicsFamily;
    private readonly uint _presentFamily;

    private readonly RenderPass _renderPass;
    private SwapchainKHR _swapchain;
    private ImageView[] _imageViews;
    private Framebuffer[] _frameBuffers;

    private readonly VulkanSemaphore _imageAvailable;
    private readonly VulkanSemaphore _renderComplete;
    private readonly Fence _renderFence;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly List<VulkanGraphicsPipeline> _graphicsPipelines = new();

    private ClearColorValue _clearColor = new(0.1f, 0.12f, 0.2f, 1f);

    private Extent2D _extent;
    private readonly Format _imageFormat;
    private WindowMetrics _metrics;
    private uint _activeImageIndex;
    private VulkanGraphicsPipeline? _pendingGraphicsPipeline;
    private GraphicsFrameParameters _pendingFrameParameters;

    internal VulkanWindowSession(VulkanRenderer renderer, SurfaceKHR surface, RenderWindowId windowId, WindowMetrics metrics)
    {
        _renderer = renderer;
        _windowId = windowId;
        _surface = surface;

        _device = renderer.GetDevice();
        _graphicsQueue = renderer.GetGraphicsQueue();
        _presentQueue = renderer.GetPresentQueue();
        _graphicsFamily = renderer.GetGraphicsFamily();
        _presentFamily = renderer.GetPresentFamily();
        _khrSurface = renderer.GetKhrSurface();
        _khrSwapchain = renderer.GetKhrSwapchain();

        _extent = new Extent2D(Math.Max(1, metrics.Width), Math.Max(1, metrics.Height));
        _metrics = metrics;
        if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
        {
            throw new InvalidOperationException("No swapchain support for selected surface.");
        }

        _imageFormat = ChooseSurfaceFormat(formats);
        _renderPass = CreateRenderPass(renderer.Api, _device, _imageFormat);

        _swapchain = CreateSwapchain(renderer.Api, _device, _graphicsFamily, _presentFamily, _extent, capabilities, formats, modes);
        (_imageViews, _frameBuffers) = CreateSwapchainImageViews(renderer.Api, _khrSwapchain, _device, _extent, _renderPass, _swapchain, _imageFormat);

        var semaphoreCreate = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        EnsureSuccess(renderer.Api.CreateSemaphore(_device, semaphoreCreate, null, out var imageAvailable), "CreateSemaphore(imageAvailable)");
        EnsureSuccess(renderer.Api.CreateSemaphore(_device, semaphoreCreate, null, out var renderComplete), "CreateSemaphore(renderComplete)");
        _imageAvailable = imageAvailable;
        _renderComplete = renderComplete;

        var fenceCreateInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
            PNext = null
        };
        EnsureSuccess(renderer.Api.CreateFence(_device, fenceCreateInfo, null, out var renderFence), "CreateFence");
        _renderFence = renderFence;

        var commandPoolCreateInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        EnsureSuccess(renderer.Api.CreateCommandPool(_device, commandPoolCreateInfo, null, out var commandPool), "CreateCommandPool");
        _commandPool = commandPool;

        var commandBufferAllocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandBufferCount = 1,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary
        };
        EnsureSuccess(renderer.Api.AllocateCommandBuffers(_device, commandBufferAllocateInfo, out var commandBuffer), "AllocateCommandBuffers");
        _commandBuffer = commandBuffer;
    }

    public RenderWindowId WindowId => _windowId;

    public IGraphicsPipeline CreateGraphicsPipeline(in GraphicsShaderProgram shaderProgram)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pipeline = CreateGraphicsPipelineCore(in shaderProgram);
        _graphicsPipelines.Add(pipeline);
        return pipeline;
    }

    public bool DrawFullscreenTriangle(IGraphicsPipeline pipeline, in GraphicsFrameParameters parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inFrame || pipeline is not VulkanGraphicsPipeline graphicsPipeline ||
            !ReferenceEquals(graphicsPipeline.Owner, this) || !graphicsPipeline.IsAlive || !parameters.IsValid)
        {
            return false;
        }

        _pendingGraphicsPipeline = graphicsPipeline;
        _pendingFrameParameters = parameters;
        return true;
    }

    public RenderFrameState BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inFrame)
        {
            return RenderFrameState.ResizeRequested(_metrics);
        }

        if (_frameBuffers.Length == 0)
        {
            return RenderFrameState.NotReady(_metrics);
        }

        var api = _renderer.Api;

        uint imageIndex = 0;
        var acquireResult = _khrSwapchain.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref imageIndex);
        if (acquireResult == Result.ErrorOutOfDateKhr || acquireResult == Result.SuboptimalKhr)
        {
            return RenderFrameState.ResizeRequested(_metrics);
        }

        if (acquireResult != Result.Success)
        {
            return RenderFrameState.NotReady(_metrics);
        }

        if (imageIndex >= (uint)_frameBuffers.Length)
        {
            throw new InvalidOperationException($"Vulkan returned swapchain image index {imageIndex}, but only {_frameBuffers.Length} framebuffers exist.");
        }

        if (api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success ||
            api.ResetFences(_device, 1, _renderFence) != Result.Success ||
            api.ResetCommandBuffer(_commandBuffer, 0) != Result.Success)
        {
            return RenderFrameState.NotReady(_metrics);
        }

        _inFrame = true;
        _activeImageIndex = imageIndex;
        return RenderFrameState.Ready(imageIndex, _metrics);
    }

    public bool EndFrame(in RenderFrameState frameState, ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inFrame || !frameState.IsValid || frameState.ImageIndex != _activeImageIndex)
        {
            return false;
        }

        return EndFrame(in frameState, in _clearColor, dirtyRecords);
    }

    public bool RenderClearFrame(float r, float g, float b, float a)
    {
        _clearColor = new ClearColorValue(r, g, b, a);
        var frameState = BeginFrame();
        if (!frameState.IsValid)
        {
            return false;
        }

        return EndFrame(in frameState, ReadOnlySpan<RenderRecordChange>.Empty);
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

        api.DeviceWaitIdle(device);

        foreach (var frameBuffer in _frameBuffers)
        {
            api.DestroyFramebuffer(device, frameBuffer, null);
        }

        foreach (var imageView in _imageViews)
        {
            api.DestroyImageView(device, imageView, null);
        }

        _khrSwapchain.DestroySwapchain(device, _swapchain, null);

        _extent = new Extent2D(Math.Max(1, metrics.Width), Math.Max(1, metrics.Height));
        _metrics = metrics;

        if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
        {
            return false;
        }

        _swapchain = CreateSwapchain(_renderer.Api, _device, _graphicsFamily, _presentFamily, _extent, capabilities, formats, modes);
        (_imageViews, _frameBuffers) = CreateSwapchainImageViews(_renderer.Api, _khrSwapchain, _device, _extent, _renderPass, _swapchain, _imageFormat);
        return _frameBuffers.Length > 0;
    }

    private bool EndFrame(in RenderFrameState frameState, in ClearColorValue clearColor, ReadOnlySpan<RenderRecordChange> dirtyRecords)
    {
        _ = frameState;
        _ = dirtyRecords;
        var api = _renderer.Api;
        var success = false;

        try
        {
            var clearValue = new ClearValue(clearColor);
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _frameBuffers[(int)_activeImageIndex],
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D(0, 0),
                    Extent = _extent
                },
                ClearValueCount = 1,
                PClearValues = &clearValue
            };

            var commandBufferBeginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            if (api.BeginCommandBuffer(_commandBuffer, commandBufferBeginInfo) != Result.Success)
            {
                return false;
            }

            api.CmdBeginRenderPass(_commandBuffer, &renderPassInfo, SubpassContents.Inline);
            if (_pendingGraphicsPipeline is { IsAlive: true })
            {
                var pipeline = _pendingGraphicsPipeline;
                api.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics, pipeline.Pipeline);

                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent };
                api.CmdSetViewport(_commandBuffer, 0, 1, new[] { viewport });
                api.CmdSetScissor(_commandBuffer, 0, 1, new[] { scissor });

                var pushConstants = new GraphicsPushConstants
                {
                    ResolutionX = _pendingFrameParameters.ResolutionX,
                    ResolutionY = _pendingFrameParameters.ResolutionY,
                    TimeSeconds = _pendingFrameParameters.TimeSeconds,
                    Reserved = 0
                };
                api.CmdPushConstants(
                    _commandBuffer,
                    pipeline.PipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0,
                    (uint)sizeof(GraphicsPushConstants),
                    &pushConstants);
                api.CmdDraw(_commandBuffer, 3, 1, 0, 0);
            }
            api.CmdEndRenderPass(_commandBuffer);
            if (api.EndCommandBuffer(_commandBuffer) != Result.Success)
            {
                return false;
            }

        var waitStages = stackalloc PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
        var imageAvailable = _imageAvailable;
        var renderComplete = _renderComplete;
        var commandBuffer = _commandBuffer;
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

            if (api.QueueSubmit(_graphicsQueue, 1, &submitInfo, _renderFence) != Result.Success)
            {
                return false;
            }

            if (api.WaitForFences(_device, 1, _renderFence, true, ulong.MaxValue) != Result.Success)
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

            var presentResult = _khrSwapchain.QueuePresent(_presentQueue, presentInfo);
            success = (presentResult == Result.Success || presentResult == Result.SuboptimalKhr) &&
                api.QueueWaitIdle(_presentQueue) == Result.Success;
        }
        finally
        {
            _inFrame = false;
            _pendingGraphicsPipeline = null;
        }

        return success;
    }

    private VulkanGraphicsPipeline CreateGraphicsPipelineCore(in GraphicsShaderProgram shaderProgram)
    {
        if (shaderProgram.Vertex.Stage != Delta.Shader.Abstractions.ShaderStage.Vertex ||
            shaderProgram.Fragment.Stage != Delta.Shader.Abstractions.ShaderStage.Fragment)
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
                Size = (uint)sizeof(GraphicsPushConstants)
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
                    Topology = PrimitiveTopology.TriangleList
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
            return new VulkanGraphicsPipeline(this, pipelineLayout, pipeline);
        }
        catch
        {
            if (vertexModule.Handle != default) api.DestroyShaderModule(_device, vertexModule, null);
            if (fragmentModule.Handle != default) api.DestroyShaderModule(_device, fragmentModule, null);
            if (pipeline.Handle != default) api.DestroyPipeline(_device, pipeline, null);
            if (pipelineLayout.Handle != default) api.DestroyPipelineLayout(_device, pipelineLayout, null);
            throw;
        }
    }

    private static void ValidateGraphicsArtifact(Delta.Shader.Abstractions.ShaderArtifact artifact, string parameterName)
    {
        if (artifact.FormatVersion != Delta.Shader.Abstractions.ShaderArtifact.CurrentFormatVersion)
        {
            throw new ArgumentException("Unsupported Delta.Shader artifact format.", parameterName);
        }

        var manifest = artifact.Manifest;
        if (manifest.Version != Delta.Shader.Abstractions.ShaderAbiManifest.CurrentVersion ||
            string.IsNullOrWhiteSpace(manifest.EntryPointName))
        {
            throw new ArgumentException("Unsupported or incomplete Delta.Shader graphics ABI manifest.", parameterName);
        }

        if (artifact.Spirv.Length == 0 || (artifact.Spirv.Length & 3) != 0)
        {
            throw new ArgumentException("Graphics SPIR-V must be non-empty and word aligned.", parameterName);
        }

        if (manifest.Resources.Count != 0)
        {
            throw new ArgumentException("The initial fullscreen graphics path does not support descriptor resources.", parameterName);
        }
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

    private static void EnsureSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
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

        var result = _khrSwapchain.CreateSwapchain(device, swapCreateInfo, null, out var swapchain);
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"CreateSwapchain failed: {result}");
        }

        return swapchain;
    }

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

        return (imageViews, frameBuffers);
    }

    private static RenderPass CreateRenderPass(Vk api, Device device, Format format)
    {
        var colorAttachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
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

            foreach (var pipeline in _graphicsPipelines.ToArray())
            {
                DestroyGraphicsPipeline(pipeline);
            }

            for (var i = 0; i < _frameBuffers.Length; i++)
            {
                api.DestroyFramebuffer(_device, _frameBuffers[i], null);
            }

            for (var i = 0; i < _imageViews.Length; i++)
            {
                api.DestroyImageView(_device, _imageViews[i], null);
            }

            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            api.DestroyRenderPass(_device, _renderPass, null);
            api.DestroySemaphore(_device, _imageAvailable, null);
            api.DestroySemaphore(_device, _renderComplete, null);
            api.DestroyFence(_device, _renderFence, null);
            api.DestroyCommandPool(_device, _commandPool, null);
            _khrSurface.DestroySurface(_renderer.Instance, _surface, null);
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

public sealed unsafe class VulkanGraphicsPipeline : IGraphicsPipeline
{
    internal VulkanGraphicsPipeline(VulkanWindowSession owner, PipelineLayout pipelineLayout, Pipeline pipeline)
    {
        Owner = owner;
        PipelineLayout = pipelineLayout;
        Pipeline = pipeline;
    }

    internal VulkanWindowSession Owner { get; }
    internal PipelineLayout PipelineLayout { get; private set; }
    internal Pipeline Pipeline { get; private set; }
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
