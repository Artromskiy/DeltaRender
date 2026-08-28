using System.Diagnostics.CodeAnalysis;
using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanRenderSession : IRenderFrameSession
{
    private static long _nextTarget;
    private static long _nextResource;

    private readonly VulkanRenderer _renderer;
    private readonly VulkanSurfaceLease? _surfaceLease;
    private readonly SurfaceKHR _surface;
    private readonly bool _windowed;
    private readonly bool _hasTarget;
    private readonly KhrSwapchain? _swapchainExtension;
    private readonly Queue _graphicsQueue;
    private readonly Queue _presentQueue;
    private readonly Device _device;
    private readonly uint _graphicsFamily;
    private readonly uint _presentFamily;
    private readonly PhysicalDeviceMemoryProperties _memoryProperties;
    private readonly RenderPass _renderPass;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _frameFence;
    private readonly VulkanSemaphore _imageAvailable;
    private readonly VulkanSemaphore _renderComplete;
    private readonly Dictionary<ulong, PersistentBuffer> _buffers = new();
    private readonly Dictionary<ulong, PersistentTexture> _textures = new();
    private readonly Dictionary<ulong, PersistentSampler> _samplers = new();

    private SwapchainKHR _swapchain;
    private ImageView[] _swapchainViews = [];
    private Framebuffer[] _swapchainFramebuffers = [];
    private Image _targetImage;
    private DeviceMemory _targetMemory;
    private ImageView _targetView;
    private Framebuffer _targetFramebuffer;
    private Extent2D _extent;
    private Format _format;
    private uint _activeImage;
    private uint _nextGeneration = 1;
    private RenderTargetHandle _target;
    private bool _recording;
    private bool _disposed;
    private BufferAllocation _staging;
    private ulong _stagingCursor;

    private VulkanRenderSession(VulkanRenderer renderer, VulkanSurfaceLease? surfaceLease, WindowMetrics metrics, bool windowed)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _surfaceLease = surfaceLease;
        _windowed = windowed;
        _hasTarget = true;
        _surface = surfaceLease is null ? default : new SurfaceKHR { Handle = surfaceLease.Handle };
        _device = renderer.GetDevice();
        _graphicsQueue = renderer.GetGraphicsQueue();
        _presentQueue = renderer.GetPresentQueue();
        _graphicsFamily = renderer.GetGraphicsFamily();
        _presentFamily = renderer.GetPresentFamily();
        _memoryProperties = renderer.Api.GetPhysicalDeviceMemoryProperties(renderer.GetPhysicalDevice());
        _extent = new Extent2D(Math.Max(1u, metrics.Width), Math.Max(1u, metrics.Height));

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
        try
        {
            if (_windowed)
            {
                _swapchainExtension = renderer.GetKhrSwapchain();
                if (!renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
                {
                    throw new InvalidOperationException("The Vulkan surface has no swapchain support.");
                }

                _format = ChooseSurfaceFormat(formats);
                renderPass = CreateRenderPass(renderer.Api, _device, _format, ImageLayout.PresentSrcKhr);
                swapchain = CreateSwapchain(_surface, _extent, capabilities, formats, modes);
                (views, framebuffers) = CreateSwapchainViews(renderer.Api, _swapchainExtension, _device, _extent, renderPass, swapchain, _format);
                Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out imageAvailable), "CreateSemaphore(image available)");
                Ensure(renderer.Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out renderComplete), "CreateSemaphore(render complete)");
            }
            else
            {
                _format = Format.R8G8B8A8Unorm;
                renderPass = CreateRenderPass(renderer.Api, _device, _format, ImageLayout.ColorAttachmentOptimal);
                headless = CreateHeadlessTarget(_extent, renderPass);
            }

            Ensure(renderer.Api.CreateFence(_device, new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit }, null, out fence), "CreateFence");
            Ensure(renderer.Api.CreateCommandPool(_device, new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _graphicsFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit }, null, out commandPool), "CreateCommandPool");
            Ensure(renderer.Api.AllocateCommandBuffers(_device, new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = commandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 }, out commandBuffer), "AllocateCommandBuffer");

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
            _target = new RenderTargetHandle(unchecked((ulong)Interlocked.Increment(ref _nextTarget)), _nextGeneration++);
            surfaceLease?.TransferToSession();
        }
        catch
        {
            DestroyPartial(renderer.Api, _device, _swapchainExtension, renderPass, swapchain, views, framebuffers, headless, imageAvailable, renderComplete, fence, commandPool, commandBuffer);
            throw;
        }
    }

    private VulkanRenderSession(VulkanRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _hasTarget = false;
        _windowed = false;
        _surfaceLease = null;
        _surface = default;
        _device = renderer.GetDevice();
        _graphicsQueue = renderer.GetGraphicsQueue();
        _presentQueue = _graphicsQueue;
        _graphicsFamily = renderer.GetGraphicsFamily();
        _presentFamily = _graphicsFamily;
        _memoryProperties = renderer.Api.GetPhysicalDeviceMemoryProperties(renderer.GetPhysicalDevice());
        _format = Format.R8G8B8A8Unorm;
        _renderPass = default;
        _swapchainExtension = null;

        Ensure(renderer.Api.CreateFence(_device, new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit }, null, out _frameFence), "CreateFence");
        Ensure(renderer.Api.CreateCommandPool(_device, new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _graphicsFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit }, null, out _commandPool), "CreateCommandPool");
        try
        {
            Ensure(renderer.Api.AllocateCommandBuffers(_device, new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _commandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 }, out _commandBuffer), "AllocateCommandBuffer");
        }
        catch
        {
            renderer.Api.DestroyCommandPool(_device, _commandPool, null);
            throw;
        }
    }

    internal static VulkanRenderSession Create(VulkanRenderer renderer, VulkanSurfaceLease surfaceLease, WindowMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(surfaceLease);
        return new VulkanRenderSession(renderer, surfaceLease, metrics, true);
    }

    internal static VulkanRenderSession CreateHeadless(VulkanRenderer renderer, PixelExtent extent)
    {
        if (extent.IsEmpty)
        {
            throw new ArgumentException("A headless target must have a non-empty extent.", nameof(extent));
        }

        return new VulkanRenderSession(renderer, null, new WindowMetrics(extent.Width, extent.Height, 1), false);
    }

    internal static VulkanRenderSession CreateCompute(VulkanRenderer renderer) => new(renderer);

    public RenderDeviceCapabilities Capabilities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var limits = _renderer.Api.GetPhysicalDeviceProperties(_renderer.GetPhysicalDevice()).Limits;
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

    public IRenderGraph CreateRenderGraph()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VulkanRenderGraph(this);
    }

    public RenderBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!description.IsValid)
        {
            throw new ArgumentException("The buffer description is invalid.", nameof(description));
        }

        var allocation = CreateNativeBuffer(description.SizeInBytes, ToVulkanBufferUsage(description.Usage), MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        var value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        var generation = NextGeneration();
        _buffers.Add(value, new PersistentBuffer(allocation, description, generation));
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
        var value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        var generation = NextGeneration();
        _textures.Add(value, texture with { Generation = generation });
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
        Ensure(_renderer.Api.CreateSampler(_device, samplerInfo, null, out var sampler), "CreateSampler");
        var value = unchecked((ulong)Interlocked.Increment(ref _nextResource));
        var generation = NextGeneration();
        _samplers.Add(value, new PersistentSampler(sampler, generation));
        return new RenderSamplerHandle(value, generation);
    }

    public void Release(RenderBufferHandle buffer)
    {
        if (TryRemove(_buffers, buffer, out var resource))
        {
            DestroyAllocation(resource.Allocation);
        }
    }

    public void Release(RenderTextureHandle texture)
    {
        if (TryRemove(_textures, texture, out var resource))
        {
            DestroyTexture(resource);
        }
    }

    public void Release(RenderSamplerHandle sampler)
    {
        if (TryRemove(_samplers, sampler, out var resource) && resource.Sampler.Handle != default)
        {
            _renderer.Api.DestroySampler(_device, resource.Sampler, null);
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

        WaitForFrame();
        var newExtent = new Extent2D(extent.Width, extent.Height);
        if (_windowed)
        {
            RecreateSwapchain(newExtent);
        }
        else
        {
            var replacement = CreateHeadlessTarget(newExtent, _renderPass);
            var old = new HeadlessTarget(_targetImage, _targetMemory, _targetView, _targetFramebuffer);
            _targetImage = replacement.Image;
            _targetMemory = replacement.Memory;
            _targetView = replacement.View;
            _targetFramebuffer = replacement.Framebuffer;
            _extent = newExtent;
            DestroyHeadlessTarget(old);
        }
    }

    internal Vk Api => _renderer.Api;
    internal Device Device => _device;
    internal PhysicalDevice PhysicalDevice => _renderer.GetPhysicalDevice();
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
    internal BufferAllocation StagingBuffer => _staging;

    internal bool TryGetBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
    {
        buffer = null;
        if (!handle.IsValid || !_buffers.TryGetValue(handle.Value, out var candidate) || candidate.Generation != handle.Generation) return false;
        buffer = candidate;
        return true;
    }

    internal bool TryGetTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
    {
        texture = null;
        if (!handle.IsValid || !_textures.TryGetValue(handle.Value, out var candidate) || candidate.Generation != handle.Generation) return false;
        texture = candidate;
        return true;
    }

    internal bool TryGetSampler(RenderSamplerHandle handle, out PersistentSampler? sampler)
    {
        sampler = null;
        if (!handle.IsValid || !_samplers.TryGetValue(handle.Value, out var candidate) || candidate.Generation != handle.Generation) return false;
        sampler = candidate;
        return true;
    }

    internal BufferAllocation CreateNativeBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = Math.Max(4, size), Usage = usage, SharingMode = SharingMode.Exclusive };
        Ensure(Api.CreateBuffer(Device, info, null, out var buffer), "CreateBuffer");
        try
        {
            var requirements = Api.GetBufferMemoryRequirements(Device, buffer);
            var type = FindMemoryType(requirements.MemoryTypeBits, required, preferred);
            var properties = MemoryProperties.MemoryTypes[(int)type].PropertyFlags;
            Ensure(Api.AllocateMemory(Device, new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = type }, null, out var memory), "AllocateBufferMemory");
            try
            {
                Ensure(Api.BindBufferMemory(Device, buffer, memory, 0), "BindBufferMemory");
                return new BufferAllocation(buffer, memory, requirements.Size, properties);
            }
            catch
            {
                Api.FreeMemory(Device, memory, null);
                throw;
            }
        }
        catch
        {
            Api.DestroyBuffer(Device, buffer, null);
            throw;
        }
    }

    internal PersistentTexture CreateTransientTexture(in RenderTextureDescription description) => CreateNativeTexture(description);

    internal void DestroyAllocation(BufferAllocation allocation)
    {
        if (allocation.Buffer.Handle != default) Api.DestroyBuffer(Device, allocation.Buffer, null);
        if (allocation.Memory.Handle != default) Api.FreeMemory(Device, allocation.Memory, null);
    }

    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint index = 0; index < MemoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) == 0) continue;
            var flags = MemoryProperties.MemoryTypes[(int)index].PropertyFlags;
            if (!flags.HasFlag(required)) continue;
            if (flags.HasFlag(preferred)) return index;
            fallback = index;
        }

        if (fallback != uint.MaxValue) return fallback;
        throw new InvalidOperationException($"No Vulkan memory type satisfies {required}.");
    }

    internal bool BeginGraphFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recording) return false;
        WaitForFrame();
        _stagingCursor = 0;
        if (_windowed)
        {
            var swapchainExtension = _swapchainExtension ?? throw new InvalidOperationException("The windowed session has no swapchain extension.");
            var result = swapchainExtension.AcquireNextImage(Device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref _activeImage);
            if (result is not Result.Success and not Result.SuboptimalKhr) return false;
            if (_activeImage >= (uint)_swapchainFramebuffers.Length) throw new InvalidOperationException("Vulkan returned an invalid swapchain image index.");
        }

        Ensure(Api.ResetCommandBuffer(_commandBuffer, 0), "ResetCommandBuffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (Api.BeginCommandBuffer(_commandBuffer, begin) != Result.Success) return false;
        _recording = true;
        return true;
    }

    internal bool EndGraphFrame()
    {
        if (!_recording) return false;
        try
        {
            Ensure(Api.EndCommandBuffer(_commandBuffer), "EndCommandBuffer");
            var commandBuffer = _commandBuffer;
            Ensure(Api.ResetFences(Device, 1, _frameFence), "ResetFence");
            if (_windowed)
            {
                var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
                var imageAvailable = _imageAvailable;
                var renderComplete = _renderComplete;
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, WaitSemaphoreCount = 1, PWaitSemaphores = &imageAvailable, PWaitDstStageMask = &waitStage, CommandBufferCount = 1, PCommandBuffers = &commandBuffer, SignalSemaphoreCount = 1, PSignalSemaphores = &renderComplete };
                Ensure(Api.QueueSubmit(_graphicsQueue, 1, &submit, _frameFence), "QueueSubmit(window)");
                var swapchain = _swapchain;
                var imageIndex = _activeImage;
                var present = new PresentInfoKHR { SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &renderComplete, SwapchainCount = 1, PSwapchains = &swapchain, PImageIndices = &imageIndex };
                var swapchainExtension = _swapchainExtension ?? throw new InvalidOperationException("The windowed session has no swapchain extension.");
                var result = swapchainExtension.QueuePresent(_presentQueue, present);
                if (result is not Result.Success and not Result.SuboptimalKhr and not Result.ErrorOutOfDateKhr) throw new InvalidOperationException($"QueuePresent failed: {result}.");
            }
            else
            {
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
                Ensure(Api.QueueSubmit(_graphicsQueue, 1, &submit, _frameFence), "QueueSubmit");
            }

            return true;
        }
        finally
        {
            _recording = false;
        }
    }

    internal void AbortGraphFrame() => _recording = false;

    internal void WaitForFrame()
    {
        Ensure(Api.WaitForFences(Device, 1, _frameFence, true, ulong.MaxValue), "WaitForFence");
    }

    internal void WaitForReadback() => WaitForFrame();

    internal ulong AllocateStaging(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return _stagingCursor;
        }

        var offset = Align(_stagingCursor, 4);
        EnsureStaging(checked(offset + (ulong)data.Length));
        void* pointer = null;
        Ensure(Api.MapMemory(Device, _staging.Memory, offset, (ulong)data.Length, 0, &pointer), "MapMemory(staging upload)");
        try
        {
            data.CopyTo(new Span<byte>(pointer, data.Length));
            if (!_staging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                var range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = _staging.Memory,
                    Offset = 0,
                    Size = (nuint)_staging.AllocationSize
                };
                Ensure(Api.FlushMappedMemoryRanges(Device, 1, &range), "FlushMappedMemoryRanges(staging upload)");
            }
        }
        finally
        {
            Api.UnmapMemory(Device, _staging.Memory);
        }

        _stagingCursor = checked(offset + (ulong)data.Length);
        return offset;
    }

    internal ulong ReserveStaging(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var offset = Align(_stagingCursor, 4);
        EnsureStaging(checked(offset + (ulong)size));
        _stagingCursor = checked(offset + (ulong)size);
        return offset;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Api.DeviceWaitIdle(Device);
        foreach (var item in _samplers.Values) if (item.Sampler.Handle != default) Api.DestroySampler(Device, item.Sampler, null);
        foreach (var item in _textures.Values) DestroyTexture(item);
        foreach (var item in _buffers.Values) DestroyAllocation(item.Allocation);
        _samplers.Clear();
        _textures.Clear();
        _buffers.Clear();
        if (VulkanBufferAllocation.IsLive(in _staging))
        {
            DestroyAllocation(_staging);
            _staging = default;
        }

        if (_windowed)
        {
            DestroySwapchainViews(_renderer.Api, Device, _swapchainViews, _swapchainFramebuffers);
            if (_swapchain.Handle != default)
            {
                var swapchainExtension = _swapchainExtension ?? throw new InvalidOperationException("The windowed session has no swapchain extension.");
                swapchainExtension.DestroySwapchain(Device, _swapchain, null);
            }
            if (_imageAvailable.Handle != default) Api.DestroySemaphore(Device, _imageAvailable, null);
            if (_renderComplete.Handle != default) Api.DestroySemaphore(Device, _renderComplete, null);
        }
        else if (_hasTarget)
        {
            DestroyHeadlessTarget(new HeadlessTarget(_targetImage, _targetMemory, _targetView, _targetFramebuffer));
        }

        if (_frameFence.Handle != default) Api.DestroyFence(Device, _frameFence, null);
        if (_commandBuffer.Handle != default) FreeCommandBuffer(_commandPool, _commandBuffer);
        if (_commandPool.Handle != default) Api.DestroyCommandPool(Device, _commandPool, null);
        if (_renderPass.Handle != default) Api.DestroyRenderPass(Device, _renderPass, null);
        _surfaceLease?.TryRelease(out _);
        return ValueTask.CompletedTask;
    }

    private uint NextGeneration() => _nextGeneration++ == 0 ? _nextGeneration++ : _nextGeneration - 1;

    private void EnsureStaging(ulong required)
    {
        if (VulkanBufferAllocation.IsLive(in _staging) && _staging.AllocationSize >= required)
        {
            return;
        }

        if (VulkanBufferAllocation.IsLive(in _staging))
        {
            WaitForReadback();
            DestroyAllocation(_staging);
        }

        var capacity = 4096UL;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        _staging = CreateNativeBuffer(
            capacity,
            BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
    }

    private static ulong Align(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static bool TryRemove<T>(Dictionary<ulong, T> values, RenderBufferHandle handle, [NotNullWhen(true)] out T? value) where T : class
    {
        value = null;
        return handle.IsValid && values.TryGetValue(handle.Value, out var candidate) && candidate is not null && ((PersistentBuffer)(object)candidate).Generation == handle.Generation && (value = candidate) is not null;
    }

    private static bool TryRemove<T>(Dictionary<ulong, T> values, RenderTextureHandle handle, [NotNullWhen(true)] out T? value) where T : class
    {
        value = null;
        return handle.IsValid && values.TryGetValue(handle.Value, out var candidate) && candidate is not null && ((PersistentTexture)(object)candidate).Generation == handle.Generation && (value = candidate) is not null;
    }

    private static bool TryRemove<T>(Dictionary<ulong, T> values, RenderSamplerHandle handle, [NotNullWhen(true)] out T? value) where T : class
    {
        value = null;
        return handle.IsValid && values.TryGetValue(handle.Value, out var candidate) && candidate is not null && ((PersistentSampler)(object)candidate).Generation == handle.Generation && (value = candidate) is not null;
    }

    private static BufferUsageFlags ToVulkanBufferUsage(RenderBufferUsage usage)
    {
        var result = BufferUsageFlags.None;
        if (usage.HasFlag(RenderBufferUsage.Vertex)) result |= BufferUsageFlags.VertexBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Index)) result |= BufferUsageFlags.IndexBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Uniform)) result |= BufferUsageFlags.UniformBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Storage)) result |= BufferUsageFlags.StorageBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Indirect)) result |= BufferUsageFlags.IndirectBufferBit;
        if (usage.HasFlag(RenderBufferUsage.TransferSource)) result |= BufferUsageFlags.TransferSrcBit;
        if (usage.HasFlag(RenderBufferUsage.TransferDestination)) result |= BufferUsageFlags.TransferDstBit;
        return result;
    }

    private PersistentTexture CreateNativeTexture(RenderTextureDescription description)
    {
        var format = ToVulkanFormat(description.Format);
        var usage = ImageUsageFlags.SampledBit;
        if (description.Usage.HasFlag(RenderTextureUsage.Storage)) usage |= ImageUsageFlags.StorageBit;
        if (description.Usage.HasFlag(RenderTextureUsage.ColorAttachment)) usage |= ImageUsageFlags.ColorAttachmentBit;
        if (description.Usage.HasFlag(RenderTextureUsage.DepthStencilAttachment)) usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        if (description.Usage.HasFlag(RenderTextureUsage.TransferSource)) usage |= ImageUsageFlags.TransferSrcBit;
        if (description.Usage.HasFlag(RenderTextureUsage.TransferDestination)) usage |= ImageUsageFlags.TransferDstBit;
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(description.Width, description.Height, 1), MipLevels = description.MipLevels, ArrayLayers = description.Layers, Samples = ToSampleCount(description.Samples), Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined };
        Ensure(Api.CreateImage(Device, info, null, out var image), "CreateImage");
        DeviceMemory memory = default;
        ImageView view = default;
        try
        {
            var requirements = Api.GetImageMemoryRequirements(Device, image);
            var type = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
            Ensure(Api.AllocateMemory(Device, new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = type }, null, out memory), "AllocateImageMemory");
            Ensure(Api.BindImageMemory(Device, image, memory, 0), "BindImageMemory");
            Ensure(Api.CreateImageView(Device, new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange { AspectMask = ToAspectMask(description.Format), BaseMipLevel = 0, LevelCount = description.MipLevels, BaseArrayLayer = 0, LayerCount = description.Layers } }, null, out view), "CreateImageView");
            return new PersistentTexture(image, memory, view, format, new Extent2D(description.Width, description.Height), 0);
        }
        catch
        {
            if (view.Handle != default) Api.DestroyImageView(Device, view, null);
            if (memory.Handle != default) Api.FreeMemory(Device, memory, null);
            Api.DestroyImage(Device, image, null);
            throw;
        }
    }

    internal void DestroyTexture(PersistentTexture texture)
    {
        if (texture.View.Handle != default) Api.DestroyImageView(Device, texture.View, null);
        if (texture.Image.Handle != default) Api.DestroyImage(Device, texture.Image, null);
        if (texture.Memory.Handle != default) Api.FreeMemory(Device, texture.Memory, null);
    }

    private void RecreateSwapchain(Extent2D extent)
    {
        var swapchainExtension = _swapchainExtension ?? throw new InvalidOperationException("The windowed session has no swapchain extension.");
        if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes)) throw new InvalidOperationException("The Vulkan surface no longer has swapchain support.");
        DestroySwapchainViews(Api, Device, _swapchainViews, _swapchainFramebuffers);
        swapchainExtension.DestroySwapchain(Device, _swapchain, null);
        _format = ChooseSurfaceFormat(formats);
        _swapchain = CreateSwapchain(_surface, extent, capabilities, formats, modes);
        (_swapchainViews, _swapchainFramebuffers) = CreateSwapchainViews(Api, swapchainExtension, Device, extent, _renderPass, _swapchain, _format);
        _extent = extent;
    }

    private HeadlessTarget CreateHeadlessTarget(Extent2D extent, RenderPass renderPass)
    {
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = _format, Extent = new Extent3D(extent.Width, extent.Height, 1), MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined };
        Ensure(Api.CreateImage(Device, info, null, out var image), "CreateImage(target)");
        DeviceMemory memory = default;
        ImageView view = default;
        Framebuffer framebuffer = default;
        try
        {
            var requirements = Api.GetImageMemoryRequirements(Device, image);
            var type = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
            Ensure(Api.AllocateMemory(Device, new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = type }, null, out memory), "AllocateMemory(target)");
            Ensure(Api.BindImageMemory(Device, image, memory, 0), "BindImageMemory(target)");
            Ensure(Api.CreateImageView(Device, new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = _format, SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 } }, null, out view), "CreateImageView(target)");
            var attachment = view;
            Ensure(Api.CreateFramebuffer(Device, new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = 1, PAttachments = &attachment, Width = extent.Width, Height = extent.Height, Layers = 1 }, null, out framebuffer), "CreateFramebuffer(target)");
            return new HeadlessTarget(image, memory, view, framebuffer);
        }
        catch
        {
            if (framebuffer.Handle != default) Api.DestroyFramebuffer(Device, framebuffer, null);
            if (view.Handle != default) Api.DestroyImageView(Device, view, null);
            if (memory.Handle != default) Api.FreeMemory(Device, memory, null);
            Api.DestroyImage(Device, image, null);
            throw;
        }
    }

    private void DestroyHeadlessTarget(HeadlessTarget target)
    {
        if (target.Framebuffer.Handle != default) Api.DestroyFramebuffer(Device, target.Framebuffer, null);
        if (target.View.Handle != default) Api.DestroyImageView(Device, target.View, null);
        if (target.Image.Handle != default) Api.DestroyImage(Device, target.Image, null);
        if (target.Memory.Handle != default) Api.FreeMemory(Device, target.Memory, null);
    }

    private static void DestroyPartial(Vk api, Device device, KhrSwapchain? swapchainExtension, RenderPass renderPass, SwapchainKHR swapchain, ImageView[] views, Framebuffer[] framebuffers, HeadlessTarget target, VulkanSemaphore imageAvailable, VulkanSemaphore renderComplete, Fence fence, CommandPool commandPool, CommandBuffer commandBuffer)
    {
        DestroySwapchainViews(api, device, views, framebuffers);
        if (swapchain.Handle != default && swapchainExtension is not null) swapchainExtension.DestroySwapchain(device, swapchain, null);
        if (target.Framebuffer.Handle != default) api.DestroyFramebuffer(device, target.Framebuffer, null);
        if (target.View.Handle != default) api.DestroyImageView(device, target.View, null);
        if (target.Image.Handle != default) api.DestroyImage(device, target.Image, null);
        if (target.Memory.Handle != default) api.FreeMemory(device, target.Memory, null);
        if (imageAvailable.Handle != default) api.DestroySemaphore(device, imageAvailable, null);
        if (renderComplete.Handle != default) api.DestroySemaphore(device, renderComplete, null);
        if (fence.Handle != default) api.DestroyFence(device, fence, null);
        if (commandBuffer.Handle != default && commandPool.Handle != default) { var value = commandBuffer; api.FreeCommandBuffers(device, commandPool, 1, &value); }
        if (commandPool.Handle != default) api.DestroyCommandPool(device, commandPool, null);
        if (renderPass.Handle != default) api.DestroyRenderPass(device, renderPass, null);
    }

    private void FreeCommandBuffer(CommandPool pool, CommandBuffer buffer) { var value = buffer; Api.FreeCommandBuffers(Device, pool, 1, &value); }

    private static void DestroySwapchainViews(Vk api, Device device, ImageView[] views, Framebuffer[] framebuffers)
    {
        for (var i = framebuffers.Length - 1; i >= 0; i--) if (framebuffers[i].Handle != default) api.DestroyFramebuffer(device, framebuffers[i], null);
        for (var i = views.Length - 1; i >= 0; i--) if (views[i].Handle != default) api.DestroyImageView(device, views[i], null);
    }

    private static (ImageView[] Views, Framebuffer[] Framebuffers) CreateSwapchainViews(Vk api, KhrSwapchain extension, Device device, Extent2D extent, RenderPass renderPass, SwapchainKHR swapchain, Format format)
    {
        uint count = 0;
        Ensure(extension.GetSwapchainImages(device, swapchain, &count, null), "GetSwapchainImages(count)");
        var images = new Image[count];
        Ensure(extension.GetSwapchainImages(device, swapchain, &count, images), "GetSwapchainImages");
        var views = new ImageView[images.Length];
        var framebuffers = new Framebuffer[images.Length];
        try
        {
            for (var i = 0; i < images.Length; i++)
            {
                Ensure(api.CreateImageView(device, new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = images[i], ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 } }, null, out views[i]), "CreateSwapchainImageView");
                var attachment = views[i];
                Ensure(api.CreateFramebuffer(device, new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = 1, PAttachments = &attachment, Width = extent.Width, Height = extent.Height, Layers = 1 }, null, out framebuffers[i]), "CreateSwapchainFramebuffer");
            }

            return (views, framebuffers);
        }
        catch
        {
            DestroySwapchainViews(api, device, views, framebuffers);
            throw;
        }
    }

    private static RenderPass CreateRenderPass(Vk api, Device device, Format format, ImageLayout finalLayout)
    {
        var attachment = new AttachmentDescription { Format = format, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store, StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined, FinalLayout = finalLayout };
        var color = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &color };
        var dependency = new SubpassDependency { SrcSubpass = Vk.SubpassExternal, DstSubpass = 0, SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit, DstAccessMask = AccessFlags.ColorAttachmentWriteBit };
        var info = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attachment, SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 1, PDependencies = &dependency };
        Ensure(api.CreateRenderPass(device, info, null, out var renderPass), "CreateRenderPass");
        return renderPass;
    }

    private SwapchainKHR CreateSwapchain(SurfaceKHR surface, Extent2D extent, SurfaceCapabilitiesKHR capabilities, SurfaceFormatKHR[] formats, PresentModeKHR[] modes)
    {
        var imageCount = Math.Max(2u, capabilities.MinImageCount);
        if (capabilities.MaxImageCount != 0) imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        var queueFamilies = new[] { _graphicsFamily, _presentFamily };
        fixed (uint* familyPointer = queueFamilies)
        {
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = surface,
                MinImageCount = imageCount,
                ImageFormat = ChooseSurfaceFormat(formats),
                ImageColorSpace = formats.Length == 0 ? ColorSpaceKHR.SpaceSrgbNonlinearKhr : formats[0].ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = _graphicsFamily == _presentFamily ? SharingMode.Exclusive : SharingMode.Concurrent,
                QueueFamilyIndexCount = _graphicsFamily == _presentFamily ? 0u : 2u,
                PQueueFamilyIndices = _graphicsFamily == _presentFamily ? null : familyPointer,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = ChoosePresentMode(modes),
                Clipped = true
            };
            var swapchainExtension = _swapchainExtension ?? throw new InvalidOperationException("The windowed session has no swapchain extension.");
            Ensure(swapchainExtension.CreateSwapchain(Device, createInfo, null, out var swapchain), "CreateSwapchain");
            return swapchain;
        }
    }

    private static Format ChooseSurfaceFormat(ReadOnlySpan<SurfaceFormatKHR> formats)
    {
        foreach (var item in formats) if (item.Format == Format.B8G8R8A8Unorm && item.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr) return item.Format;
        return formats.Length == 0 ? Format.B8G8R8A8Unorm : formats[0].Format;
    }

    private static PresentModeKHR ChoosePresentMode(ReadOnlySpan<PresentModeKHR> modes) => modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
    private static Format ToVulkanFormat(RenderTextureFormat format) => format switch { RenderTextureFormat.R8Unorm => Format.R8Unorm, RenderTextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm, RenderTextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb, RenderTextureFormat.Bgra8Unorm => Format.B8G8R8A8Unorm, RenderTextureFormat.Bgra8Srgb => Format.B8G8R8A8Srgb, RenderTextureFormat.Rgba16Float => Format.R16G16B16A16Sfloat, RenderTextureFormat.D32Float => Format.D32Sfloat, RenderTextureFormat.D24UnormS8UInt => Format.D24UnormS8Uint, _ => throw new ArgumentException("Unsupported render texture format.", nameof(format)) };
    private static ImageAspectFlags ToAspectMask(RenderTextureFormat format) => format is RenderTextureFormat.D32Float or RenderTextureFormat.D24UnormS8UInt ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
    private static SampleCountFlags ToSampleCount(uint samples) => samples switch { 1 => SampleCountFlags.Count1Bit, 2 => SampleCountFlags.Count2Bit, 4 => SampleCountFlags.Count4Bit, 8 => SampleCountFlags.Count8Bit, _ => throw new ArgumentOutOfRangeException(nameof(samples)) };
    private static SamplerAddressMode ToAddressMode(RenderAddressMode mode) => mode switch { RenderAddressMode.Repeat => SamplerAddressMode.Repeat, RenderAddressMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat, _ => SamplerAddressMode.ClampToEdge };
    private static BufferUsageFlags ToVulkanBufferUsageFlags(RenderBufferUsage usage) => ToVulkanBufferUsage(usage);
    private static void Ensure(Result result, string operation) { if (result != Result.Success) throw new InvalidOperationException($"{operation} failed: {result}"); }

    private readonly record struct HeadlessTarget(Image Image, DeviceMemory Memory, ImageView View, Framebuffer Framebuffer);
}

internal sealed class PersistentBuffer
{
    internal PersistentBuffer(BufferAllocation allocation, RenderBufferDescription description, uint generation) { Allocation = allocation; Description = description; Generation = generation; }
    internal BufferAllocation Allocation { get; }
    internal RenderBufferDescription Description { get; }
    internal uint Generation { get; }
}

internal sealed record PersistentTexture(Image Image, DeviceMemory Memory, ImageView View, Format Format, Extent2D Extent, uint Generation);
internal sealed record PersistentSampler(Sampler Sampler, uint Generation);
