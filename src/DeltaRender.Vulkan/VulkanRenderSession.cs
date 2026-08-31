using System.Diagnostics.CodeAnalysis;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderSession : IRenderFrameSession
{
    private static long _nextTarget;
    private static long _nextResource;

    private readonly VulkanRenderer _renderer;
    private readonly bool _profilingEnabled;
    private readonly VulkanRenderProfiler? _profiler;
    private readonly VulkanSurfaceLease? _surfaceLease;
    private readonly SurfaceKHR _surface;
    private readonly bool _windowed;
    private readonly VulkanFrameSlots _frameSlots;
    private VulkanFrameSlotResources[] _frameResources = [];
    private readonly bool _hasTarget;
    private PhysicalDevice _physicalDevice;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private Device _device;
    private uint _graphicsFamily;
    private uint _presentFamily;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private RenderPass _renderPass;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _frameFence;
    private VulkanSemaphore _imageAvailable;
    private VulkanSemaphore _renderComplete;
    private readonly VulkanResourceRegistry _resources = new();
    private readonly VulkanPipelineCache<IGraphicsShaderProgram, VulkanGraphPipeline> _rasterPipelines = new(ReferenceEqualityComparer.Instance);
    private readonly VulkanPipelineCache<IShaderArtifact, VulkanGraphPipeline> _computePipelines = new(ReferenceEqualityComparer.Instance);
    private readonly VulkanTransientResourcePool<TransientBufferKey, BufferAllocation> _transientBuffers = new();
    private readonly VulkanTransientResourcePool<TransientTextureKey, PersistentTexture> _transientTextures = new();
    private readonly List<DeferredBuffer> _deferredBuffers = [];
    private readonly List<DeferredTexture> _deferredTextures = [];
    private VulkanRenderGraph? _graph;

    private SwapchainKHR _swapchain;
    private ImageView[] _swapchainViews = [];
    private Framebuffer[] _swapchainFramebuffers = [];
    private Image _targetImage;
    private DeviceMemory _targetMemory;
    private ImageView _targetView;
    private Framebuffer _targetFramebuffer;
    private Image _depthImage;
    private ImageView _depthView;
    private Format _depthFormat;
    private bool _hasDepthStencilAttachment;
    private DepthStencilAttachmentDescription _depthDescription;
    private Extent2D _extent;
    private Format _format;
    private uint _activeImage;
    private uint _nextGeneration = 1;
    private RenderTargetHandle _target;
    private bool _recording;
    private bool _disposed;
    private VulkanStagingBuffer _stagingBuffer = null!;

    private VulkanRenderSession(VulkanRenderer renderer, VulkanSurfaceLease? surfaceLease, WindowMetrics metrics, bool windowed, RenderSessionOptions options)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _profilingEnabled = options.EnableProfiling;
        _profiler = null;
        _surfaceLease = surfaceLease;
        _windowed = windowed;
        _frameSlots = new VulkanFrameSlots(options.FramesInFlight);
        _stagingBuffer = null!;
        _hasTarget = true;
        _surface = surfaceLease is null ? default : new SurfaceKHR { Handle = surfaceLease.Handle };
        var deviceContext = renderer.GetDeviceContext(windowed);
        _physicalDevice = deviceContext.PhysicalDevice;
        _device = deviceContext.Device;
        _graphicsQueue = deviceContext.GraphicsQueue;
        _presentQueue = deviceContext.PresentQueue;
        _graphicsFamily = deviceContext.GraphicsFamily;
        _presentFamily = deviceContext.PresentFamily;
        _memoryProperties = deviceContext.MemoryProperties;
        _profiler = _profilingEnabled ? new VulkanRenderProfiler(renderer.Api, _device, _physicalDevice, _graphicsFamily) : null;
        var drawableExtent = metrics.DrawableExtent;
        _extent = new Extent2D(Math.Max(1u, drawableExtent.Width), Math.Max(1u, drawableExtent.Height));

        RenderPass renderPass = default;
        CommandPool commandPool = default;
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        VulkanSemaphore imageAvailable = default;
        VulkanSemaphore renderComplete = default;
        SwapchainKHR swapchain = default;
        ImageView[] views = [];
        Framebuffer[] framebuffers = [];
        HeadlessTarget headless = default;
        VulkanFrameSlotResources[] frameResources = [];
        KhrSwapchain? swapchainExtension = null;
        try
        {
            if (_windowed)
            {
                swapchainExtension = renderer.GetKhrSwapchain();
                if (!renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
                {
                    throw new InvalidOperationException("The Vulkan surface has no swapchain support.");
                }

                _format = ChooseSurfaceFormat(formats);
                renderPass = CreateRenderPass(renderer.Api, _device, _format, ImageLayout.PresentSrcKhr);
                swapchain = CreateSwapchain(_surface, _extent, capabilities, formats, modes);
                (views, framebuffers) = CreateSwapchainViews(renderer.Api, renderer.GetKhrSwapchain(), _device, _extent, renderPass, swapchain, _format);
                VulkanCall.Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out imageAvailable), "CreateSemaphore(image available)");
                VulkanCall.Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out renderComplete), "CreateSemaphore(render complete)");
            }
            else
            {
                _format = Format.R8G8B8A8Unorm;
                renderPass = CreateRenderPass(renderer.Api, _device, _format, ImageLayout.ColorAttachmentOptimal);
                headless = CreateHeadlessTarget(_extent, renderPass);
            }

            var commandResources = CreateCommandResources(renderer.Api, _device, _graphicsFamily);
            fence = commandResources.Fence;
            commandPool = commandResources.CommandPool;
            commandBuffer = commandResources.CommandBuffer;

            frameResources = new VulkanFrameSlotResources[_frameSlots.Count];
            for (int index = 0; index < frameResources.Length; index++)
            {
                var slot = new VulkanFrameSlotResources(this);
                frameResources[index] = slot;
                if (index == 0)
                {
                    slot.Fence = fence;
                    slot.CommandPool = commandPool;
                    slot.CommandBuffer = commandBuffer;
                    slot.ImageAvailable = imageAvailable;
                    slot.RenderComplete = renderComplete;
                    if (!_windowed)
                    {
                        slot.TargetImage = headless.Image;
                        slot.TargetMemory = headless.Memory;
                        slot.TargetView = headless.View;
                        slot.TargetFramebuffer = headless.Framebuffer;
                    }
                }
                else
                {
                    if (_windowed)
                    {
                        VulkanCall.Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out slot.ImageAvailable), "CreateSemaphore(image available)");
                        VulkanCall.Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out slot.RenderComplete), "CreateSemaphore(render complete)");
                    }
                    else
                    {
                        var target = CreateHeadlessTarget(_extent, renderPass);
                        slot.TargetImage = target.Image;
                        slot.TargetMemory = target.Memory;
                        slot.TargetView = target.View;
                        slot.TargetFramebuffer = target.Framebuffer;
                    }

                    var resources = CreateCommandResources(renderer.Api, _device, _graphicsFamily);
                    slot.Fence = resources.Fence;
                    slot.CommandPool = resources.CommandPool;
                    slot.CommandBuffer = resources.CommandBuffer;
                }
            }

            _renderPass = renderPass;
            _commandPool = commandPool;
            _commandBuffer = commandBuffer;
            _frameFence = fence;
            _imageAvailable = imageAvailable;
            _renderComplete = renderComplete;
            _swapchain = swapchain;
            _swapchainViews = views;
            _swapchainFramebuffers = framebuffers;
            _targetImage = headless.Image;
            _targetMemory = headless.Memory;
            _targetView = headless.View;
            _targetFramebuffer = headless.Framebuffer;
            _frameResources = frameResources;
            _target = new RenderTargetHandle(unchecked((ulong)Interlocked.Increment(ref _nextTarget)), _nextGeneration++);
            ActivateFrameSlot(0);
            surfaceLease?.TransferToSession();
        }
        catch
        {
            DestroyFrameResources(renderer.Api, _device, frameResources, skipFirst: true);
            DestroyPartial(renderer.Api, _device, swapchainExtension, renderPass, swapchain, views, framebuffers, headless, imageAvailable, renderComplete, fence, commandPool, commandBuffer);
            throw;
        }
    }

    private VulkanRenderSession(VulkanRenderer renderer, RenderSessionOptions options)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _profilingEnabled = options.EnableProfiling;
        _profiler = null;
        _hasTarget = false;
        _windowed = false;
        _frameSlots = new VulkanFrameSlots(options.FramesInFlight);
        _stagingBuffer = null!;
        _surfaceLease = null;
        _surface = default;
        var deviceContext = renderer.GetDeviceContext(windowed: false);
        _physicalDevice = deviceContext.PhysicalDevice;
        _device = deviceContext.Device;
        _graphicsQueue = deviceContext.GraphicsQueue;
        _presentQueue = deviceContext.PresentQueue;
        _graphicsFamily = deviceContext.GraphicsFamily;
        _presentFamily = deviceContext.PresentFamily;
        _memoryProperties = deviceContext.MemoryProperties;
        _profiler = _profilingEnabled ? new VulkanRenderProfiler(renderer.Api, _device, _physicalDevice, _graphicsFamily) : null;
        _format = Format.R8G8B8A8Unorm;
        _renderPass = default;

        _frameResources = new VulkanFrameSlotResources[_frameSlots.Count];
        try
        {
            for (int index = 0; index < _frameResources.Length; index++)
            {
                var slot = new VulkanFrameSlotResources(this);
                _frameResources[index] = slot;
                var commandResources = CreateCommandResources(renderer.Api, _device, _graphicsFamily);
                slot.Fence = commandResources.Fence;
                slot.CommandPool = commandResources.CommandPool;
                slot.CommandBuffer = commandResources.CommandBuffer;
            }

            ActivateFrameSlot(0);
        }
        catch
        {
            DestroyFrameResources(renderer.Api, _device, _frameResources);
            throw;
        }
    }

    private static (Fence Fence, CommandPool CommandPool, CommandBuffer CommandBuffer) CreateCommandResources(Vk api, Device device, uint graphicsFamily)
    {
        Fence fence = default;
        CommandPool commandPool = default;
        CommandBuffer commandBuffer = default;
        try
        {
            VulkanCall.Ensure(api.CreateFence(device, new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit }, null, out fence), "CreateFence");
            VulkanCall.Ensure(api.CreateCommandPool(device, new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = graphicsFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit }, null, out commandPool), "CreateCommandPool");
            VulkanCall.Ensure(api.AllocateCommandBuffers(device, new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = commandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 }, out commandBuffer), "AllocateCommandBuffer");
            return (fence, commandPool, commandBuffer);
        }
        catch
        {
            DestroyCommandResources(api, device, fence, commandPool, commandBuffer);
            throw;
        }
    }

    private static unsafe void DestroyCommandResources(Vk api, Device device, Fence fence, CommandPool commandPool, CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle != default && commandPool.Handle != default)
        {
            var value = commandBuffer;
            api.FreeCommandBuffers(device, commandPool, 1, &value);
        }

        if (commandPool.Handle != default)
        {
            api.DestroyCommandPool(device, commandPool, null);
        }

        if (fence.Handle != default)
        {
            api.DestroyFence(device, fence, null);
        }
    }

    internal static VulkanRenderSession Create(VulkanRenderer renderer, VulkanSurfaceLease surfaceLease, WindowMetrics metrics, RenderSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(surfaceLease);
        return new VulkanRenderSession(renderer, surfaceLease, metrics, true, options);
    }

    internal static VulkanRenderSession CreateHeadless(VulkanRenderer renderer, PixelExtent extent, RenderSessionOptions options)
    {
        if (extent.IsEmpty)
        {
            throw new ArgumentException("A headless target must have a non-empty extent.", nameof(extent));
        }

        return new VulkanRenderSession(renderer, null, new WindowMetrics(extent.Width, extent.Height, 1), false, options);
    }

    internal static VulkanRenderSession CreateCompute(VulkanRenderer renderer, RenderSessionOptions options) => new(renderer, options);

    public RenderDeviceCapabilities Capabilities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var limits = Api.GetPhysicalDeviceProperties(_physicalDevice).Limits;
            return new RenderDeviceCapabilities(
                limits.MaxStorageBufferRange,
                limits.MinStorageBufferOffsetAlignment,
                limits.MaxPushConstantsSize,
                limits.MaxBoundDescriptorSets,
                limits.MaxComputeWorkGroupInvocations,
                limits.MaxComputeWorkGroupSize[0],
                limits.MaxComputeWorkGroupSize[1],
                limits.MaxComputeWorkGroupSize[2],
                limits.MaxComputeWorkGroupCount[0],
                limits.MaxComputeWorkGroupCount[1],
                limits.MaxComputeWorkGroupCount[2]);
        }
    }

    public RenderTargetHandle Target => _hasTarget && !_disposed ? _target : default;

    public bool ProfilingEnabled => _profilingEnabled;

    public IRenderProfiler? Profiler => _profiler;

    public IRenderGraph CreateRenderGraph()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _graph ??= new VulkanRenderGraph(this);
    }

    public RenderBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!description.IsValid)
        {
            throw new ArgumentException("The buffer description is invalid.", nameof(description));
        }

        var allocation = CreateNativeBuffer(description.SizeInBytes, ToVulkanBufferUsage(description.Usage), MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        ulong value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        uint generation = NextGeneration();
        _resources.AddBuffer(value, new PersistentBuffer(allocation, description, generation));
        return new RenderBufferHandle(value, generation);
    }

    public RenderTextureHandle CreateTexture(in RenderTextureDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!description.IsValid)
        {
            throw new ArgumentException("The texture description is invalid.", nameof(description));
        }

        var texture = CreateNativeTexture(description);
        ulong value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        uint generation = NextGeneration();
        _resources.AddTexture(value, texture with { Generation = generation });
        return new RenderTextureHandle(value, generation);
    }

    public RenderSamplerHandle CreateSampler(in RenderSamplerDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = description.MagFilter == RenderFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MinFilter = description.MinFilter == RenderFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MipmapMode = description.MipmapFilter == RenderFilter.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
            AddressModeU = ToAddressMode(description.AddressU),
            AddressModeV = ToAddressMode(description.AddressV),
            AddressModeW = ToAddressMode(description.AddressW),
            MaxLod = 1
        };
        VulkanCall.Ensure(_renderer.Api.CreateSampler(_device, samplerInfo, null, out var sampler), "CreateSampler");
        ulong value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        uint generation = NextGeneration();
        _resources.AddSampler(value, new PersistentSampler(sampler, generation));
        return new RenderSamplerHandle(value, generation);
    }

    public void Release(RenderBufferHandle buffer)
    {
        if (_resources.TryRemoveBuffer(buffer, out var resource))
        {
            DestroyAllocation(resource.Allocation);
        }
    }

    public void Release(RenderTextureHandle texture)
    {
        if (_resources.TryRemoveTexture(texture, out var resource))
        {
            DestroyTexture(resource);
        }
    }

    public void Release(RenderSamplerHandle sampler)
    {
        if (_resources.TryRemoveSampler(sampler, out var resource))
        {
            DestroySampler(resource);
        }
    }

    public void ResizeTarget(in PixelExtent extent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasTarget)
        {
            throw new InvalidOperationException("The compute-only session has no render target.");
        }

        if (extent.IsEmpty)
        {
            throw new ArgumentException("The target extent must be non-empty.", nameof(extent));
        }

        if (_frameResources.Length != 0)
        {
            WaitForAllFrameSlots();
        }
        else
        {
            WaitForFrame();
        }
        if (_hasDepthStencilAttachment)
        {
            ConfigureDepthStencilAttachment(null, default);
        }

        var newExtent = new Extent2D(extent.Width, extent.Height);
        if (_windowed)
        {
            RecreateSwapchain(newExtent);
        }
        else
        {
            var replacements = new HeadlessTarget[_frameResources.Length];
            try
            {
                for (int index = 0; index < replacements.Length; index++)
                {
                    replacements[index] = CreateHeadlessTarget(newExtent, _renderPass);
                }
            }
            catch
            {
                for (int index = 0; index < replacements.Length; index++)
                {
                    if (replacements[index].Image.Handle != default)
                    {
                        DestroyHeadlessTarget(replacements[index]);
                    }
                }

                throw;
            }

            for (int index = 0; index < _frameResources.Length; index++)
            {
                var slot = _frameResources[index];
                var old = new HeadlessTarget(slot.TargetImage, slot.TargetMemory, slot.TargetView, slot.TargetFramebuffer);
                var replacement = replacements[index];
                slot.TargetImage = replacement.Image;
                slot.TargetMemory = replacement.Memory;
                slot.TargetView = replacement.View;
                slot.TargetFramebuffer = replacement.Framebuffer;
                DestroyHeadlessTarget(old);
            }

            _extent = newExtent;
            ActivateFrameSlot(_frameSlots.CurrentIndex >= 0 ? _frameSlots.CurrentIndex : 0);
        }
    }

    internal Vk Api => _renderer.Api;

    internal VulkanRenderProfiler? ProfilerState => _profiler;
    internal Device Device => _device;
    internal PhysicalDevice PhysicalDevice => _physicalDevice;
    internal PhysicalDeviceMemoryProperties MemoryProperties => _memoryProperties;
    internal Queue GraphicsQueue => _graphicsQueue;
    internal Queue PresentQueue => _presentQueue;
    internal uint GraphicsFamily => _graphicsFamily;
    internal CommandBuffer CommandBuffer => _commandBuffer;
    internal RenderPass GraphRenderPass => _renderPass;
    internal Extent2D GraphExtent => _extent;
    internal Framebuffer GraphFramebuffer => _windowed ? _swapchainFramebuffers[(int)_activeImage] : _targetFramebuffer;
    internal Image GraphImage => _windowed ? default : _targetImage;
    internal bool IsWindowed => _windowed;
    internal bool HasTarget => _hasTarget;
    internal uint MaxBoundDescriptorSets => Capabilities.MaxBoundDescriptorSets;
    internal BufferAllocation StagingBuffer => _stagingBuffer.Current;

    private int CurrentFrameSlot => _frameSlots.CurrentIndex;

    private void ActivateFrameSlot(int index)
    {
        var slot = _frameResources[index];
        _commandPool = slot.CommandPool;
        _commandBuffer = slot.CommandBuffer;
        _frameFence = slot.Fence;
        _imageAvailable = slot.ImageAvailable;
        _renderComplete = slot.RenderComplete;
        _stagingBuffer = slot.StagingBuffer;
        if (_hasTarget)
        {
            _targetImage = slot.TargetImage;
            _targetMemory = slot.TargetMemory;
            _targetView = slot.TargetView;
            _targetFramebuffer = slot.TargetFramebuffer;
        }
    }

    private static void DestroyFrameResources(Vk api, Device device, VulkanFrameSlotResources[] resources, bool skipFirst = false)
    {
        for (int index = resources.Length - 1; index >= 0; index--)
        {
            var slot = resources[index];
            if (skipFirst && index == 0)
            {
                slot.StagingBuffer.Dispose();
                continue;
            }

            slot.StagingBuffer.Dispose();
            if (slot.TargetFramebuffer.Handle != default || slot.TargetView.Handle != default || slot.TargetImage.Handle != default || slot.TargetMemory.Handle != default)
            {
                DestroyHeadlessTarget(api, device, new HeadlessTarget(slot.TargetImage, slot.TargetMemory, slot.TargetView, slot.TargetFramebuffer));
            }

            if (slot.CommandBuffer.Handle != default || slot.CommandPool.Handle != default || slot.Fence.Handle != default)
            {
                DestroyCommandResources(api, device, slot.Fence, slot.CommandPool, slot.CommandBuffer);
            }

            if (slot.ImageAvailable.Handle != default)
            {
                api.DestroySemaphore(device, slot.ImageAvailable, null);
            }

            if (slot.RenderComplete.Handle != default)
            {
                api.DestroySemaphore(device, slot.RenderComplete, null);
            }
        }
    }

    internal void ConfigureDepthStencilAttachment(PersistentTexture? texture, in DepthStencilAttachmentDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasTarget)
        {
            if (texture is not null)
            {
                throw new InvalidOperationException("A compute-only session cannot configure a depth-stencil attachment.");
            }

            return;
        }

        var depthTexture = texture;
        bool hasAttachment = depthTexture is not null;
        if (!hasAttachment && !_hasDepthStencilAttachment)
        {
            return;
        }

        if (hasAttachment)
        {
            if (depthTexture is null || depthTexture.View.Handle == default || depthTexture.Image.Handle == default)
            {
                throw new InvalidOperationException("The depth-stencil attachment native image or view is unavailable.");
            }

            if (depthTexture.Format is not (Format.D32Sfloat or Format.D24UnormS8Uint))
            {
                throw new ArgumentException("The attachment texture must use a depth-capable Vulkan format.", nameof(texture));
            }

            if (!depthTexture.Usage.HasFlag(RenderTextureUsage.DepthStencilAttachment))
            {
                throw new ArgumentException("The attachment texture was not created with DepthStencilAttachment usage.", nameof(texture));
            }

            if (depthTexture.Extent.Width != _extent.Width || depthTexture.Extent.Height != _extent.Height)
            {
                throw new ArgumentException("The depth-stencil texture extent must match the render target.", nameof(texture));
            }

            if (depthTexture.Format == Format.D32Sfloat && (description.StencilLoad != AttachmentLoadOperation.Discard || description.StencilStore != AttachmentStoreOperation.Discard || description.ClearValue.Stencil != 0))
            {
                throw new ArgumentException("D32Float does not provide a stencil aspect.", nameof(description));
            }

            if (_hasDepthStencilAttachment && _depthImage.Handle == depthTexture.Image.Handle && _depthView.Handle == depthTexture.View.Handle && _depthFormat == depthTexture.Format && IsSameDepthDescription(description))
            {
                return;
            }
        }

        WaitForAllFrameSlots();
        var nextDepthFormat = depthTexture?.Format ?? default;
        var nextDepthView = depthTexture?.View ?? default;
        var nextRenderPass = CreateRenderPass(
            Api,
            Device,
            _format,
            _windowed ? ImageLayout.PresentSrcKhr : ImageLayout.ColorAttachmentOptimal,
            hasAttachment,
            hasAttachment ? nextDepthFormat : default,
            hasAttachment ? ToAttachmentLoad(description.DepthLoad) : AttachmentLoadOp.DontCare,
            hasAttachment ? ToAttachmentStore(description.DepthStore) : AttachmentStoreOp.DontCare,
            hasAttachment ? ToAttachmentLoad(description.StencilLoad) : AttachmentLoadOp.DontCare,
            hasAttachment ? ToAttachmentStore(description.StencilStore) : AttachmentStoreOp.DontCare);
        ImageView[] nextViews = [];
        Framebuffer[] nextFramebuffers = [];
        Framebuffer[] nextHeadlessFramebuffers = [];
        Framebuffer nextTargetFramebuffer = default;
        try
        {
            if (_windowed)
            {
                (nextViews, nextFramebuffers) = CreateSwapchainViews(Api, _renderer.GetKhrSwapchain(), Device, _extent, nextRenderPass, _swapchain, _format, nextDepthView, hasAttachment);
            }
            else
            {
                nextHeadlessFramebuffers = new Framebuffer[_frameResources.Length];
                for (int index = 0; index < nextHeadlessFramebuffers.Length; index++)
                {
                    nextHeadlessFramebuffers[index] = CreateFramebuffer(
                        Api,
                        Device,
                        nextRenderPass,
                        _frameResources[index].TargetView,
                        nextDepthView,
                        hasAttachment,
                        _extent,
                        "CreateFramebuffer(target)");
                }
            }
        }
        catch
        {
            DestroySwapchainViews(Api, Device, nextViews, nextFramebuffers);
            if (nextTargetFramebuffer.Handle != default)
            {
                Api.DestroyFramebuffer(Device, nextTargetFramebuffer, null);
            }

            foreach (var framebuffer in nextHeadlessFramebuffers)
            {
                if (framebuffer.Handle != default)
                {
                    Api.DestroyFramebuffer(Device, framebuffer, null);
                }
            }

            Api.DestroyRenderPass(Device, nextRenderPass, null);
            throw;
        }

        DestroyRasterPipelines();
        if (_windowed)
        {
            DestroySwapchainViews(Api, Device, _swapchainViews, _swapchainFramebuffers);
        }
        else
        {
            foreach (var slot in _frameResources)
            {
                if (slot.TargetFramebuffer.Handle != default)
                {
                    Api.DestroyFramebuffer(Device, slot.TargetFramebuffer, null);
                }
            }
        }

        if (_renderPass.Handle != default)
        {
            Api.DestroyRenderPass(Device, _renderPass, null);
        }

        _renderPass = nextRenderPass;
        _swapchainViews = nextViews;
        _swapchainFramebuffers = nextFramebuffers;
        if (_windowed)
        {
            _targetFramebuffer = nextTargetFramebuffer;
        }
        else
        {
            for (int index = 0; index < _frameResources.Length; index++)
            {
                _frameResources[index].TargetFramebuffer = nextHeadlessFramebuffers[index];
            }

            ActivateFrameSlot(_frameSlots.CurrentIndex >= 0 ? _frameSlots.CurrentIndex : 0);
        }
        _depthImage = depthTexture?.Image ?? default;
        _depthView = depthTexture?.View ?? default;
        _depthFormat = nextDepthFormat;
        _hasDepthStencilAttachment = hasAttachment;
        _depthDescription = hasAttachment ? description : default;
    }

    internal VulkanGraphPipeline GetOrCreateRasterPipeline(in RasterPipelineDescription description)
    {
        var copy = description;
        return _rasterPipelines.GetOrCreate(copy.ShaderProgram, () => VulkanGraphPipeline.CreateRaster(this, copy));
    }

    private bool IsSameDepthDescription(in DepthStencilAttachmentDescription description)
        => _depthDescription.DepthLoad == description.DepthLoad &&
           _depthDescription.DepthStore == description.DepthStore &&
           _depthDescription.StencilLoad == description.StencilLoad &&
           _depthDescription.StencilStore == description.StencilStore;

    private void DestroyRasterPipelines()
    {
        foreach (var pipeline in _rasterPipelines.Values)
        {
            pipeline.Dispose(this);
        }

        _rasterPipelines.Clear();
    }

    internal VulkanGraphPipeline GetOrCreateComputePipeline(IShaderArtifact artifact)
        => _computePipelines.GetOrCreate(artifact, () => VulkanGraphPipeline.CreateCompute(this, artifact));

    internal bool TryGetBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
        => _resources.TryGetBuffer(handle, out buffer);

    internal bool TryGetTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
        => _resources.TryGetTexture(handle, out texture);

    internal bool TryGetSampler(RenderSamplerHandle handle, [NotNullWhen(true)] out PersistentSampler? sampler)
        => _resources.TryGetSampler(handle, out sampler);

    internal bool BeginGraphFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recording)
        {
            return false;
        }

        PrepareFrameSlot();
        if (_windowed)
        {
            var swapchainExtension = _renderer.GetKhrSwapchain();
            var result = swapchainExtension.AcquireNextImage(Device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref _activeImage);
            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanOperationException(result, "AcquireNextImage");
            }
            if (result is not Result.Success and not Result.SuboptimalKhr)
            {
                return false;
            }

            if (_activeImage >= (uint)_swapchainFramebuffers.Length)
            {
                throw new InvalidOperationException("Vulkan returned an invalid swapchain image index.");
            }
        }

        VulkanCall.Ensure(Api.ResetCommandBuffer(_commandBuffer, 0), "ResetCommandBuffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (Api.BeginCommandBuffer(_commandBuffer, begin) != Result.Success)
        {
            return false;
        }

        _recording = true;
        return true;
    }

    internal bool EndGraphFrame()
    {
        if (!_recording)
        {
            return false;
        }

        try
        {
            VulkanCall.Ensure(Api.EndCommandBuffer(_commandBuffer), "EndCommandBuffer");
            var commandBuffer = _commandBuffer;
            VulkanCall.Ensure(Api.ResetFences(Device, 1, _frameFence), "ResetFence");
            if (_windowed)
            {
                var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
                var imageAvailable = _imageAvailable;
                var renderComplete = _renderComplete;
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, WaitSemaphoreCount = 1, PWaitSemaphores = &imageAvailable, PWaitDstStageMask = &waitStage, CommandBufferCount = 1, PCommandBuffers = &commandBuffer, SignalSemaphoreCount = 1, PSignalSemaphores = &renderComplete };
                VulkanCall.Ensure(Api.QueueSubmit(_graphicsQueue, 1, &submit, _frameFence), "QueueSubmit(window)");
                _frameResources[CurrentFrameSlot].InFlight = true;
                var swapchain = _swapchain;
                uint imageIndex = _activeImage;
                var present = new PresentInfoKHR { SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &renderComplete, SwapchainCount = 1, PSwapchains = &swapchain, PImageIndices = &imageIndex };
                var swapchainExtension = _renderer.GetKhrSwapchain();
                var result = swapchainExtension.QueuePresent(_presentQueue, present);
                if (result == Result.ErrorDeviceLost)
                {
                    throw new VulkanOperationException(result, "QueuePresent");
                }
                if (result is not Result.Success and not Result.SuboptimalKhr and not Result.ErrorOutOfDateKhr)
                {
                    throw new InvalidOperationException($"QueuePresent failed: {result}.");
                }
            }
            else
            {
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
                VulkanCall.Ensure(Api.QueueSubmit(_graphicsQueue, 1, &submit, _frameFence), "QueueSubmit");
                _frameResources[CurrentFrameSlot].InFlight = true;
            }

            return true;
        }
        finally
        {
            _recording = false;
        }
    }

    internal void AbortGraphFrame()
    {
        if (!_recording)
        {
            return;
        }

        Api.ResetCommandBuffer(_commandBuffer, 0);
        _recording = false;
    }

    private void PrepareFrameSlot()
    {
        ActivateFrameSlot(_frameSlots.Advance());
        WaitForFrame();
        _stagingBuffer.ReclaimCompleted();
        ReclaimDeferredTransientsForSlot(_frameSlots.CurrentIndex);
    }

    private void WaitForFence(Fence fence, string operation)
    {
        var profiler = _profiler;
        long started = profiler is null ? 0L : VulkanRenderProfiler.StartPhase();
        try
        {
            VulkanCall.Ensure(Api.WaitForFences(Device, 1, fence, true, ulong.MaxValue), operation);
        }
        finally
        {
            profiler?.EndFenceWait(started);
        }
    }

    internal void WaitForFrame()
    {
        int slotIndex = CurrentFrameSlot;
        if (slotIndex < 0 || !_frameResources[slotIndex].InFlight)
        {
            return;
        }

        WaitForFence(_frameFence, "WaitForFence");
        _frameResources[slotIndex].InFlight = false;
    }

    private void WaitForAllFrameSlots()
    {
        if (_frameResources.Length == 0)
        {
            WaitForFrame();
            return;
        }

        foreach (var slot in _frameResources)
        {
            if (slot.InFlight && slot.Fence.Handle != default)
            {
                WaitForFence(slot.Fence, "WaitForFrameSlotFence");
                slot.InFlight = false;
            }
        }
    }

    internal void WaitForReadback() => WaitForFrame();

    internal ulong AllocateStaging(ReadOnlySpan<byte> data)
        => _stagingBuffer.Allocate(data);

    internal ulong ReserveStaging(int size)
        => _stagingBuffer.Reserve(size);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _graph?.DisposeGraph();
        _disposed = true;
        if (Device.Handle != default)
        {
            Api.DeviceWaitIdle(Device);
        }
        _profiler?.Dispose();
        DisposePersistentResources();
        DisposeStaging();
        DisposeTargetResources();
        DisposeCommandResources();
        _surfaceLease?.TryRelease(out _);
        return ValueTask.CompletedTask;
    }

    private void DisposePersistentResources()
    {
        ReclaimDeferredTransients();
        _transientTextures.Drain(DestroyTexture);
        _transientBuffers.Drain(DestroyAllocation);
        foreach (var pipeline in _rasterPipelines.Values)
        {
            pipeline.Dispose(this);
        }

        foreach (var pipeline in _computePipelines.Values)
        {
            pipeline.Dispose(this);
        }

        _rasterPipelines.Clear();
        _computePipelines.Clear();
        foreach (var item in _resources.Samplers)
        {
            DestroySampler(item);
        }

        foreach (var item in _resources.Textures)
        {
            DestroyTexture(item);
        }

        foreach (var item in _resources.Buffers)
        {
            DestroyAllocation(item.Allocation);
        }

        _resources.Clear();
    }

    private void DestroySampler(PersistentSampler sampler)
    {
        if (sampler.Sampler.Handle != default)
        {
            Api.DestroySampler(Device, sampler.Sampler, null);
        }
    }

    private void DisposeStaging()
    {
        if (_frameResources.Length == 0)
        {
            _stagingBuffer.Dispose();
            return;
        }

        foreach (var slot in _frameResources)
        {
            slot.StagingBuffer.Dispose();
        }
    }

    private void DisposeTargetResources()
    {
        if (_windowed)
        {
            DestroySwapchainViews(_renderer.Api, Device, _swapchainViews, _swapchainFramebuffers);
            if (_swapchain.Handle != default)
            {
                var swapchainExtension = _renderer.GetKhrSwapchain();
                swapchainExtension.DestroySwapchain(Device, _swapchain, null);
            }
            foreach (var slot in _frameResources)
            {
                DestroySemaphore(slot.ImageAvailable);
                DestroySemaphore(slot.RenderComplete);
            }
        }
        else if (_hasTarget)
        {
            foreach (var slot in _frameResources)
            {
                DestroyHeadlessTarget(new HeadlessTarget(slot.TargetImage, slot.TargetMemory, slot.TargetView, slot.TargetFramebuffer));
            }
        }
    }

    private void DisposeCommandResources()
    {
        if (_frameResources.Length == 0)
        {
            DestroyCommandResources(Api, Device, _frameFence, _commandPool, _commandBuffer);
        }
        else
        {
            foreach (var slot in _frameResources)
            {
                DestroyCommandResources(Api, Device, slot.Fence, slot.CommandPool, slot.CommandBuffer);
            }
        }

        if (_renderPass.Handle != default)
        {
            Api.DestroyRenderPass(Device, _renderPass, null);
        }
    }

    private void DestroySemaphore(Silk.NET.Vulkan.Semaphore semaphore)
    {
        if (semaphore.Handle != default)
        {
            Api.DestroySemaphore(Device, semaphore, null);
        }
    }

    private uint NextGeneration()
    {
        if (_nextGeneration == 0)
        {
            _nextGeneration = 1;
        }

        return _nextGeneration++;
    }

    private readonly record struct HeadlessTarget(Image Image, DeviceMemory Memory, ImageView View, Framebuffer Framebuffer);
    private readonly record struct TransientBufferKey(ulong SizeInBytes, RenderBufferUsage Usage);
    private readonly record struct TransientTextureKey(uint Width, uint Height, RenderTextureFormat Format, uint MipLevels, uint Layers, uint Samples, RenderTextureUsage Usage);
    private readonly record struct DeferredBuffer(BufferAllocation Allocation, TransientBufferKey Key, int FrameSlot);
    private readonly record struct DeferredTexture(PersistentTexture Texture, TransientTextureKey Key, int FrameSlot);
}
