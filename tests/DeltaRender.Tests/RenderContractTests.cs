using System.Reflection;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public class RenderContractTests
{
    [Fact]
    public void IRenderWindowContractIsSurfaceAndMetricsFocused()
    {
        var members = typeof(IRenderWindow)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(static member => member.Name)
            .ToArray();

        Assert.DoesNotContain("PollEvents", members);
        Assert.DoesNotContain("RenderWindowEvent", members);
        Assert.Contains("Handle", members);
        Assert.Contains("VulkanSurfaceSource", members);
    }

    [Fact]
    public void IRenderFrameSessionContractExposesGraphOwnershipAndResize()
    {
        var methods = typeof(IRenderFrameSession)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IRenderFrameSession.CreateRenderGraph), methods);
        Assert.Contains(nameof(IRenderFrameSession.TryReinitializeAfterDeviceLoss), methods);
        Assert.Contains(nameof(IRenderFrameSession.CreateBuffer), methods);
        Assert.Contains(nameof(IRenderFrameSession.CreateTexture), methods);
        Assert.Contains(nameof(IRenderFrameSession.CreateSampler), methods);
        Assert.Contains(nameof(IRenderFrameSession.Release), methods);
        Assert.Contains(nameof(IRenderFrameSession.ResizeTarget), methods);
        Assert.Contains(nameof(IRenderFrameSession.Target), typeof(IRenderFrameSession).GetProperties().Select(static property => property.Name));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IRenderFrameSession)));
    }

    [Fact]
    public void DeviceLossHasAnExplicitRecoveryStatusAndSessionOperation()
    {
        var method = typeof(IRenderFrameSession).GetMethod(nameof(IRenderFrameSession.TryReinitializeAfterDeviceLoss));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        var exception = new VulkanOperationException(Result.ErrorDeviceLost, "QueueSubmit");
        Assert.Equal(RenderGraphExecutionStatus.DeviceLost, VulkanRenderGraph.ClassifyFailure(exception));
    }

}
