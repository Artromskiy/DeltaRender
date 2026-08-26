using Delta.Render.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace Delta.Render.Tests;

public sealed class VulkanOwnershipContractTests
{
    [Fact]
    public void BufferLeaseReleasesExactlyOnceAndSupportsRetry()
    {
        var allocation = new BufferAllocation(
            new VulkanBuffer { Handle = 1 },
            new DeviceMemory { Handle = 2 },
            64,
            MemoryPropertyFlags.HostVisibleBit);
        var lease = new VulkanBufferLease();

        lease.Assign(allocation);
        Assert.True(lease.IsLive);
        Assert.Equal(allocation, lease.Release());
        Assert.False(lease.IsLive);
        Assert.Equal(default, lease.Release());

        lease.Assign(allocation);
        Assert.Equal(allocation, lease.Release());
        Assert.False(lease.IsLive);
    }

    [Fact]
    public void BufferLeaseRejectsReplacementWithoutReleasingCurrentAllocation()
    {
        var first = new BufferAllocation(new VulkanBuffer { Handle = 1 }, new DeviceMemory { Handle = 2 }, 64, 0);
        var second = new BufferAllocation(new VulkanBuffer { Handle = 3 }, new DeviceMemory { Handle = 4 }, 128, 0);
        var lease = new VulkanBufferLease();
        lease.Assign(first);

        Assert.Throws<InvalidOperationException>(() => lease.Assign(second));
        Assert.Equal(first, lease.Value);
        Assert.Equal(first, lease.Release());
        Assert.False(lease.IsLive);
    }
}
