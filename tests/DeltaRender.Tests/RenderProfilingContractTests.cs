using Delta.Diagnostics;
using Delta.Render;
using Xunit;

namespace Delta.Render.Tests;

public sealed class RenderProfilingContractTests
{
    [Fact]
    public void ProfileDurationPreservesPicosecondsAndSupportsNanosecondView()
    {
        var value = new ProfileDuration(1_234_567_891_250);

        Assert.Equal(1_234_567_891_250ul, value.Picoseconds);
        Assert.Equal(1_234_567_891.25d, value.Nanoseconds);
        Assert.Equal(new ProfileDuration(1_234_567_891_252), value + new ProfileDuration(2));
        Assert.Equal(new ProfileDuration(1_234_567_891_248), value - new ProfileDuration(2));
        Assert.Equal(TimeSpan.FromTicks(12_345_678), value.ToTimeSpan());
    }

    [Fact]
    public void SessionOptionsExposeExplicitProfilingOptIn()
    {
        var disabled = new RenderSessionOptions();
        var enabled = new RenderSessionOptions(EnableProfiling: true);

        Assert.False(disabled.EnableProfiling);
        Assert.True(enabled.EnableProfiling);
        Assert.Contains(nameof(IRenderFrameSession.ProfilingEnabled), typeof(IRenderFrameSession).GetProperties().Select(static property => property.Name));
        Assert.Contains(nameof(IRenderFrameSession.Profiler), typeof(IRenderFrameSession).GetProperties().Select(static property => property.Name));
    }

    [Fact]
    public void SessionOptionsExposeBoundedFrameSlots()
    {
        var defaultOptions = new RenderSessionOptions();
        var multiBuffered = new RenderSessionOptions(FramesInFlight: 3);

        Assert.Equal(1, defaultOptions.FramesInFlight);
        Assert.Equal(3, multiBuffered.FramesInFlight);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderSessionOptions(FramesInFlight: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderSessionOptions(FramesInFlight: 9));
    }

    [Fact]
    public void ProfilingCapabilitiesDistinguishCpuAndGpuTiming()
    {
        var capabilities = new RenderProfilingCapabilities(true, false, 0, 0);

        Assert.True(capabilities.CpuTimings);
        Assert.False(capabilities.GpuTimestamps);
        Assert.Equal(0, capabilities.TimestampPeriodNanoseconds);
    }
}
