using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderSession
{
    private void RecreateSwapchain(Extent2D extent)
    {
        var swapchainExtension = _renderer.GetKhrSwapchain();
        if (!_renderer.QuerySwapchainSupport(_surface, out var capabilities, out var formats, out var modes))
        {
            throw new InvalidOperationException("The Vulkan surface no longer has swapchain support.");
        }

        DestroySwapchainViews(Api, Device, _swapchainViews, _swapchainFramebuffers);
        swapchainExtension.DestroySwapchain(Device, _swapchain, null);
        _format = ChooseSurfaceFormat(formats);
        _swapchain = CreateSwapchain(_surface, extent, capabilities, formats, modes);
        (_swapchainViews, _swapchainFramebuffers) = CreateSwapchainViews(Api, swapchainExtension, Device, extent, _renderPass, _swapchain, _format, _depthView, _hasDepthStencilAttachment);
        _extent = extent;
    }

    private HeadlessTarget CreateHeadlessTarget(Extent2D extent, RenderPass renderPass)
    {
        Image image;
        fixed (uint* queueFamilyPointer = _queueFamilies)
        {
            var shared = _queueFamilies.Length > 1;
            var info = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = _format,
                Extent = new Extent3D(extent.Width, extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = shared ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = shared ? (uint)_queueFamilies.Length : 0u,
                PQueueFamilyIndices = shared ? queueFamilyPointer : null,
                InitialLayout = ImageLayout.Undefined,
            };
            VulkanCall.Ensure(Api.CreateImage(Device, info, null, out image), "CreateImage(target)");
        }
        DeviceMemory memory = default;
        ImageView view = default;
        Framebuffer framebuffer = default;
        try
        {
            memory = AllocateAndBindImageMemory(image);
            view = CreateImageView(image, _format, ImageAspectFlags.ColorBit, 1, 1, "CreateImageView(target)");
            framebuffer = CreateFramebuffer(Api, Device, renderPass, view, default, false, extent, "CreateFramebuffer(target)");
            return new HeadlessTarget(image, memory, view, framebuffer);
        }
        catch
        {
            DestroyHeadlessTarget(Api, Device, new HeadlessTarget(image, memory, view, framebuffer));
            throw;
        }
    }

    private void DestroyHeadlessTarget(HeadlessTarget target)
    {
        DestroyHeadlessTarget(Api, Device, target);
    }

    private static void DestroyHeadlessTarget(Vk api, Device device, HeadlessTarget target)
    {
        if (target.Framebuffer.Handle != default)
        {
            api.DestroyFramebuffer(device, target.Framebuffer, null);
        }

        if (target.View.Handle != default)
        {
            api.DestroyImageView(device, target.View, null);
        }

        if (target.Image.Handle != default)
        {
            api.DestroyImage(device, target.Image, null);
        }

        if (target.Memory.Handle != default)
        {
            api.FreeMemory(device, target.Memory, null);
        }
    }

    private static void DestroyPartial(Vk api, Device device, KhrSwapchain? swapchainExtension, RenderPass renderPass, SwapchainKHR swapchain, ImageView[] views, Framebuffer[] framebuffers, HeadlessTarget target, VulkanSemaphore imageAvailable, VulkanSemaphore renderComplete, Fence fence, CommandPool commandPool, CommandBuffer commandBuffer)
    {
        DestroySwapchainViews(api, device, views, framebuffers);
        if (swapchain.Handle != default && swapchainExtension is not null)
        {
            swapchainExtension.DestroySwapchain(device, swapchain, null);
        }

        DestroyHeadlessTarget(api, device, target);
        if (imageAvailable.Handle != default)
        {
            api.DestroySemaphore(device, imageAvailable, null);
        }

        if (renderComplete.Handle != default)
        {
            api.DestroySemaphore(device, renderComplete, null);
        }

        DestroyCommandResources(api, device, fence, commandPool, commandBuffer);

        if (renderPass.Handle != default)
        {
            api.DestroyRenderPass(device, renderPass, null);
        }
    }

    private static void DestroySwapchainViews(Vk api, Device device, ImageView[] views, Framebuffer[] framebuffers)
    {
        for (var i = framebuffers.Length - 1; i >= 0; i--)
        {
            if (framebuffers[i].Handle != default)
            {
                api.DestroyFramebuffer(device, framebuffers[i], null);
            }
        }

        for (var i = views.Length - 1; i >= 0; i--)
        {
            if (views[i].Handle != default)
            {
                api.DestroyImageView(device, views[i], null);
            }
        }
    }

    private static (ImageView[] Views, Framebuffer[] Framebuffers) CreateSwapchainViews(Vk api, KhrSwapchain extension, Device device, Extent2D extent, RenderPass renderPass, SwapchainKHR swapchain, Format format, ImageView depthView = default, bool hasDepthStencil = false)
    {
        uint count = 0;
        VulkanCall.Ensure(extension.GetSwapchainImages(device, swapchain, &count, null), "GetSwapchainImages(count)");
        var images = new Image[count];
        VulkanCall.Ensure(extension.GetSwapchainImages(device, swapchain, &count, images), "GetSwapchainImages");
        var views = new ImageView[images.Length];
        var framebuffers = new Framebuffer[images.Length];
        try
        {
            for (var i = 0; i < images.Length; i++)
            {
                VulkanCall.Ensure(api.CreateImageView(device, new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = images[i], ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 } }, null, out views[i]), "CreateSwapchainImageView");
                framebuffers[i] = CreateFramebuffer(api, device, renderPass, views[i], depthView, hasDepthStencil, extent, "CreateSwapchainFramebuffer");
            }

            return (views, framebuffers);
        }
        catch
        {
            DestroySwapchainViews(api, device, views, framebuffers);
            throw;
        }
    }

    private static RenderPass CreateRenderPass(
        Vk api,
        Device device,
        Format format,
        ImageLayout finalLayout,
        bool hasDepthStencil = false,
        Format depthFormat = default,
        AttachmentLoadOp depthLoadOp = AttachmentLoadOp.DontCare,
        AttachmentStoreOp depthStoreOp = AttachmentStoreOp.DontCare,
        AttachmentLoadOp stencilLoadOp = AttachmentLoadOp.DontCare,
        AttachmentStoreOp stencilStoreOp = AttachmentStoreOp.DontCare)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription { Format = format, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store, StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined, FinalLayout = finalLayout };
        if (hasDepthStencil)
        {
            attachments[1] = new AttachmentDescription { Format = depthFormat, Samples = SampleCountFlags.Count1Bit, LoadOp = depthLoadOp, StoreOp = depthStoreOp, StencilLoadOp = stencilLoadOp, StencilStoreOp = stencilStoreOp, InitialLayout = depthLoadOp == AttachmentLoadOp.Load || stencilLoadOp == AttachmentLoadOp.Load ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.Undefined, FinalLayout = ImageLayout.DepthStencilAttachmentOptimal };
        }

        var color = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var depth = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var depthStages = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &color };
        if (hasDepthStencil)
        {
            subpass.PDepthStencilAttachment = &depth;
        }

        var dependency = new SubpassDependency { SrcSubpass = Vk.SubpassExternal, DstSubpass = 0, SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | (hasDepthStencil ? depthStages : PipelineStageFlags.None), DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | (hasDepthStencil ? depthStages : PipelineStageFlags.None), DstAccessMask = AccessFlags.ColorAttachmentWriteBit | (hasDepthStencil ? AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit : AccessFlags.None) };
        var info = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = hasDepthStencil ? 2u : 1u, PAttachments = attachments, SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 1, PDependencies = &dependency };
        VulkanCall.Ensure(api.CreateRenderPass(device, info, null, out var renderPass), "CreateRenderPass");
        return renderPass;
    }

    private static Framebuffer CreateFramebuffer(Vk api, Device device, RenderPass renderPass, ImageView colorView, ImageView depthView, bool hasDepthStencil, Extent2D extent, string operation)
    {
        var attachments = stackalloc ImageView[2];
        attachments[0] = colorView;
        if (hasDepthStencil)
        {
            attachments[1] = depthView;
        }

        var info = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = hasDepthStencil ? 2u : 1u, PAttachments = attachments, Width = extent.Width, Height = extent.Height, Layers = 1 };
        VulkanCall.Ensure(api.CreateFramebuffer(device, info, null, out var framebuffer), operation);
        return framebuffer;
    }

    private SwapchainKHR CreateSwapchain(SurfaceKHR surface, Extent2D extent, SurfaceCapabilitiesKHR capabilities, SurfaceFormatKHR[] formats, PresentModeKHR[] modes)
    {
        var imageCount = DeltaMaths.Max(2u, capabilities.MinImageCount);
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = DeltaMaths.Min(imageCount, capabilities.MaxImageCount);
        }

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
            var swapchainExtension = _renderer.GetKhrSwapchain();
            VulkanCall.Ensure(swapchainExtension.CreateSwapchain(Device, createInfo, null, out var swapchain), "CreateSwapchain");
            return swapchain;
        }
    }

    private static Format ChooseSurfaceFormat(ReadOnlySpan<SurfaceFormatKHR> formats)
    {
        foreach (var item in formats)
        {
            if (item.Format == Format.B8G8R8A8Unorm && item.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return item.Format;
            }
        }

        return formats.Length == 0 ? Format.B8G8R8A8Unorm : formats[0].Format;
    }

    private static PresentModeKHR ChoosePresentMode(ReadOnlySpan<PresentModeKHR> modes) => modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
    private static Format ToVulkanFormat(RenderTextureFormat format) => format switch { RenderTextureFormat.R8Unorm => Format.R8Unorm, RenderTextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm, RenderTextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb, RenderTextureFormat.Bgra8Unorm => Format.B8G8R8A8Unorm, RenderTextureFormat.Bgra8Srgb => Format.B8G8R8A8Srgb, RenderTextureFormat.Rgba16Float => Format.R16G16B16A16Sfloat, RenderTextureFormat.D32Float => Format.D32Sfloat, RenderTextureFormat.D24UnormS8UInt => Format.D24UnormS8Uint, _ => throw new ArgumentException("Unsupported render texture format.", nameof(format)) };
    private static AttachmentLoadOp ToAttachmentLoad(AttachmentLoadOperation operation) => operation switch { AttachmentLoadOperation.Load => AttachmentLoadOp.Load, AttachmentLoadOperation.Clear => AttachmentLoadOp.Clear, AttachmentLoadOperation.Discard => AttachmentLoadOp.DontCare, _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
    private static AttachmentStoreOp ToAttachmentStore(AttachmentStoreOperation operation) => operation switch { AttachmentStoreOperation.Store => AttachmentStoreOp.Store, AttachmentStoreOperation.Discard => AttachmentStoreOp.DontCare, _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
    private static ImageAspectFlags ToAspectMask(RenderTextureFormat format) => format switch { RenderTextureFormat.D32Float => ImageAspectFlags.DepthBit, RenderTextureFormat.D24UnormS8UInt => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, _ => ImageAspectFlags.ColorBit };
    private static SampleCountFlags ToSampleCount(uint samples) => samples switch { 1 => SampleCountFlags.Count1Bit, 2 => SampleCountFlags.Count2Bit, 4 => SampleCountFlags.Count4Bit, 8 => SampleCountFlags.Count8Bit, _ => throw new ArgumentOutOfRangeException(nameof(samples)) };
    private static SamplerAddressMode ToAddressMode(RenderAddressMode mode) => mode switch { RenderAddressMode.Repeat => SamplerAddressMode.Repeat, RenderAddressMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat, _ => SamplerAddressMode.ClampToEdge };
}
