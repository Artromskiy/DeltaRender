using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanHeadlessFrameSlotsTests
{
    [Fact]
    public void SlotsRotateAndReuseDeterministically()
    {
        var slots = new VulkanHeadlessFrameSlots(3);

        slots.Advance();
        Assert.Equal(0, slots.CurrentIndex);
        slots.Advance();
        Assert.Equal(1, slots.CurrentIndex);
        slots.Advance();
        Assert.Equal(2, slots.CurrentIndex);
        slots.Advance();
        Assert.Equal(0, slots.CurrentIndex);
        Assert.Equal(2UL, slots.CurrentFrameNumber);
    }

    [Fact]
    public void ResetReturnsSlotsToAnUnborrowedState()
    {
        var slots = new VulkanHeadlessFrameSlots(2);
        slots.Advance();
        slots.Advance();

        slots.Reset();

        Assert.Equal(-1, slots.CurrentIndex);
        Assert.Equal(0UL, slots.CurrentFrameNumber);
        slots.Advance();
        Assert.Equal(0, slots.CurrentIndex);
    }
}
