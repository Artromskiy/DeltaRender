using System.Reflection;
using DeltaRender;
using Xunit;

namespace DeltaRender.Tests;

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
    public void IRenderWindowFrameSessionContractExposesLifecycleAndResize()
    {
        var methods = typeof(IRenderWindowFrameSession)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IRenderWindowFrameSession.BeginFrame), methods);
        Assert.Contains(nameof(IRenderWindowFrameSession.EndFrame), methods);
        Assert.Contains(nameof(IRenderWindowFrameSession.Resize), methods);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IRenderWindowFrameSession)));
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
