using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderSession
{
    public bool TryReinitializeAfterDeviceLoss()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recording)
        {
            _renderer.Diagnostics.Add(RenderDiagnosticSeverity.Warning, "VK-RECOVERY", "Device recovery cannot start while a graph frame is recording.");
            return false;
        }

        _graph?.DisposeGraph();
        _graph = null;
        InvalidateDeviceLocalState();

        var diagnostics = new RenderDiagnosticBag();
        if (!_renderer.TryReinitializeDevice(_windowed, _surface, diagnostics))
        {
            _renderer.Diagnostics.Merge(diagnostics);
            return false;
        }

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
        KhrSwapchain? swapchainExtension = _windowed ? _renderer.GetKhrSwapchain() : null;
        try
        {
            var deviceContext = _renderer.GetDeviceContext(_windowed);
            _physicalDevice = deviceContext.PhysicalDevice;
            _device = deviceContext.Device;
            _graphicsQueue = deviceContext.GraphicsQueue;
            _presentQueue = deviceContext.PresentQueue;
            _graphicsFamily = deviceContext.GraphicsFamily;
            _presentFamily = deviceContext.PresentFamily;
            _memoryProperties = deviceContext.MemoryProperties;

            if (_windowed)
            {
                _swapchainExtension = swapchainExtension;
                if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
                {
                    throw new InvalidOperationException("The Vulkan surface has no swapchain support after device recovery.");
                }

                _format = ChooseSurfaceFormat(formats);
                renderPass = CreateRenderPass(Api, _device, _format, ImageLayout.PresentSrcKhr);
                swapchain = CreateSwapchain(_surface, _extent, capabilities, formats, modes);
                (views, framebuffers) = CreateSwapchainViews(Api, swapchainExtension ?? throw new InvalidOperationException("The recovered window session has no swapchain extension."), _device, _extent, renderPass, swapchain, _format);
                VulkanCall.Ensure(Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out imageAvailable), "CreateSemaphore(image available recovery)");
                VulkanCall.Ensure(Api.CreateSemaphore(_device, new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo }, null, out renderComplete), "CreateSemaphore(render complete recovery)");
            }
            else if (_hasTarget)
            {
                _format = Format.R8G8B8A8Unorm;
                renderPass = CreateRenderPass(Api, _device, _format, ImageLayout.ColorAttachmentOptimal);
                headless = CreateHeadlessTarget(_extent, renderPass);
            }

            var commandResources = CreateCommandResources(Api, _device, _graphicsFamily);
            fence = commandResources.Fence;
            commandPool = commandResources.CommandPool;
            commandBuffer = commandResources.CommandBuffer;

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
            _activeImage = 0;
            _target = _hasTarget ? new RenderTargetHandle(unchecked((ulong)Interlocked.Increment(ref _nextTarget)), NextGeneration()) : default;
            _renderer.Diagnostics.Merge(diagnostics);
            return true;
        }
        catch (Exception exception)
        {
            DestroyPartial(Api, _device, swapchainExtension, renderPass, swapchain, views, framebuffers, headless, imageAvailable, renderComplete, fence, commandPool, commandBuffer);
            diagnostics.Add(RenderDiagnosticSeverity.Error, "VK-RECOVERY", exception.Message);
            _renderer.Diagnostics.Merge(diagnostics);
            return false;
        }
    }

    private void InvalidateDeviceLocalState()
    {
        _rasterPipelines.Clear();
        _computePipelines.Clear();
        _transientBuffers.Clear();
        _transientTextures.Clear();
        _deferredBuffers.Clear();
        _deferredTextures.Clear();
        _resources.Clear();
        _staging = default;
        _stagingCursor = 0;
        _renderPass = default;
        _swapchain = default;
        _swapchainViews = [];
        _swapchainFramebuffers = [];
        _targetImage = default;
        _targetMemory = default;
        _targetView = default;
        _targetFramebuffer = default;
        _depthImage = default;
        _depthView = default;
        _depthFormat = default;
        _hasDepthStencilAttachment = false;
        _depthDescription = default;
        _target = default;
        _swapchainExtension = null;
        _commandPool = default;
        _commandBuffer = default;
        _frameFence = default;
        _imageAvailable = default;
        _renderComplete = default;
        _device = default;
        _physicalDevice = default;
        _graphicsQueue = default;
        _presentQueue = default;
        _graphicsFamily = uint.MaxValue;
        _presentFamily = uint.MaxValue;
        _memoryProperties = default;
        _recording = false;
        _ = NextGeneration();
    }
}
