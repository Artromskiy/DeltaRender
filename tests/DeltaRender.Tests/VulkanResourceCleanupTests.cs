using DeltaRender.Vulkan;
using Xunit;

namespace DeltaRender.Tests;

public sealed class VulkanResourceCleanupTests
{
    private static readonly int[] Resources = [1, 2, 3];
    private static readonly int[] ExpectedOrder = [3, 2, 1];

    [Fact]
    public void CleanupRunsEveryResourceInReverseOrderWhenOneDeleterThrows()
    {
        var order = new List<int>();

        var aggregate = Assert.Throws<AggregateException>(() =>
            VulkanResourceCleanup.CleanupInReverse(Resources, resource =>
            {
                order.Add(resource);
                if (resource == 2)
                {
                    throw new InvalidOperationException("injected cleanup failure");
                }
            }));

        Assert.Equal(ExpectedOrder, order);
        Assert.Contains("injected cleanup failure", aggregate.ToString());
    }
}
