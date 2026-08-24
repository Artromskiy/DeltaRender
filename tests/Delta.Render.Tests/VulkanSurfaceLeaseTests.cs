using Delta.Render.Core;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanSurfaceLeaseTests
{
    [Fact]
    public void TransferAndRepeatedReleaseUseTheSameDeleterExactlyOnce()
    {
        var source = new FakeSurfaceSource();
        var lease = new VulkanSurfaceLease(source, 1, 2);

        lease.TransferToSession();
        Assert.True(lease.IsTransferred);
        Assert.True(lease.TryRelease(out var firstDiagnostics));
        Assert.True(lease.TryRelease(out var secondDiagnostics));

        Assert.Equal(1, source.DestroyCount);
        Assert.Equal("No diagnostics.", firstDiagnostics.ToString());
        Assert.Equal("No diagnostics.", secondDiagnostics.ToString());
        Assert.True(lease.IsReleased);
    }

    [Fact]
    public void FailureBeforeTransferCanReleaseOnceAndCannotBeDoubleDestroyed()
    {
        var source = new FakeSurfaceSource();
        var lease = new VulkanSurfaceLease(source, 3, 4);

        Assert.False(lease.IsTransferred);
        Assert.True(lease.TryRelease(out _));
        Assert.True(lease.TryRelease(out _));

        Assert.Equal(1, source.DestroyCount);
        Assert.True(lease.IsReleased);
    }

    [Fact]
    public void DeleterFailureIsReportedAndReleaseIsStillExactOnce()
    {
        var source = new FakeSurfaceSource { DestroyResult = false };
        var lease = new VulkanSurfaceLease(source, 5, 6);

        Assert.False(lease.TryRelease(out var diagnostics));
        Assert.Contains("surface cleanup failed", diagnostics.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(lease.TryRelease(out _));
        Assert.Equal(1, source.DestroyCount);
    }

    private sealed class FakeSurfaceSource : IVulkanWindowSurfaceSource
    {
        public int DestroyCount { get; private set; }
        public bool DestroyResult { get; init; } = true;
        public bool SupportsPortabilityEnumeration => false;
        public string PlatformName => "test";

        public bool TryGetRequiredInstanceExtensions(out string[] extensionNames, out RenderDiagnosticBag diagnostics)
        {
            extensionNames = [];
            diagnostics = new RenderDiagnosticBag();
            return true;
        }

        public bool TryCreateSurface(ulong vkInstance, ulong allocatorAddress, out ulong surface, out RenderDiagnosticBag diagnostics)
        {
            surface = 0;
            diagnostics = new RenderDiagnosticBag();
            return false;
        }

        public bool TryDestroySurface(ulong vkInstance, ulong surface, out RenderDiagnosticBag diagnostics)
        {
            DestroyCount++;
            diagnostics = new RenderDiagnosticBag();
            if (!DestroyResult)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "TEST-SURFACE", "surface cleanup failed");
            }

            return DestroyResult;
        }
    }
}
