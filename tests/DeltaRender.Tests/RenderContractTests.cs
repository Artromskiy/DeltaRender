using System.Reflection;
using Delta.Render;
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
        Assert.Contains(nameof(IRenderFrameSession.CreateGraphicsPipeline), methods);
        Assert.Contains(nameof(IRenderFrameSession.SurfaceHandle), methods);
        Assert.Contains(nameof(IRenderFrameSession.Resize), methods);
        Assert.DoesNotContain("BeginFrame", methods);
        Assert.DoesNotContain("EndFrame", methods);
        Assert.DoesNotContain("SubmitFrame", methods);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IRenderFrameSession)));
    }

    [Fact]
    public void RenderRecordChangeSupportsZeroAllocationPath()
    {
        var payload = new byte[16];
        var changed = RenderRecordChange.Upsert(entityId: 42, componentKindId: 11, payload);
        var removed = RenderRecordChange.Remove(entityId: 43, componentKindId: 12);

        Assert.Equal(42ul, changed.EntityId);
        Assert.Equal(RenderRecordChangeKind.Upserted, changed.Kind);
        Assert.Equal(11u, changed.ComponentKindId);
        Assert.Equal(payload, changed.Payload.ToArray());
        Assert.Equal(16u, changed.PayloadSize);

        Assert.Equal(43ul, removed.EntityId);
        Assert.Equal(RenderRecordChangeKind.Removed, removed.Kind);
        Assert.Equal(0u, removed.PayloadSize);
        Assert.Empty(removed.Payload.ToArray());
    }
}
