using System.Reflection;
using System.Linq;
using Delta.Render.Core;
using Xunit;

namespace Delta.Render.Tests;

public class RenderContractTests
{
    [Fact]
    public void IRenderWindow_contract_is_surface_and_metrics_focused()
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
    public void IRenderWindowFrameSession_contract_exposes_lifecycle_and_resize()
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
    public void RenderRecordChange_supports_zero_allocation_path()
    {
        var changed = RenderRecordChange.Upsert(entityId: 42, componentKindId: 11, payloadAddress: 12345, payloadSize: 16);
        var removed = RenderRecordChange.Remove(entityId: 43, componentKindId: 12);

        Assert.Equal(42ul, changed.EntityId);
        Assert.Equal(RenderRecordChangeKind.Upserted, changed.Kind);
        Assert.Equal(11u, changed.ComponentKindId);
        Assert.Equal(12345ul, changed.PayloadAddress);
        Assert.Equal(16u, changed.PayloadSize);

        Assert.Equal(43ul, removed.EntityId);
        Assert.Equal(RenderRecordChangeKind.Removed, removed.Kind);
        Assert.Equal(0u, removed.PayloadSize);
        Assert.Equal(0ul, removed.PayloadAddress);
    }
}
