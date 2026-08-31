using System.Collections.Generic;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanReuseTests
{
    [Fact]
    public void DependencyPlannerReusesCapacityWhenGraphShrinksAndGrows()
    {
        var planner = new VulkanGraphDependencyPlanner();
        var passes = new List<VulkanRenderGraph.GraphPass>
        {
            new("first", VulkanRenderGraph.PassKind.Transfer, null),
            new("second", VulkanRenderGraph.PassKind.Transfer, null),
        };
        Span<int> order = stackalloc int[16];

        Assert.Equal(2, planner.Compile(passes, 1, order));
        var initialPassCapacity = planner.PassCapacity;
        var initialResourceCapacity = planner.ResourceCapacity;

        passes.RemoveAt(1);
        Assert.Equal(1, planner.Compile(passes, 1, order));
        Assert.Equal(initialPassCapacity, planner.PassCapacity);
        Assert.Equal(initialResourceCapacity, planner.ResourceCapacity);

        passes.Add(new VulkanRenderGraph.GraphPass("second", VulkanRenderGraph.PassKind.Transfer, null));
        Assert.Equal(2, planner.Compile(passes, 1, order));
        Assert.Equal(initialPassCapacity, planner.PassCapacity);
        Assert.Equal(initialResourceCapacity, planner.ResourceCapacity);
    }

    [Fact]
    public void PipelineCacheCreatesOnceAndReportsWarmHit()
    {
        var cache = new VulkanPipelineCache<object, int>(ReferenceEqualityComparer.Instance);
        var key = new object();
        var createCalls = 0;

        var first = cache.GetOrCreate(key, () => ++createCalls);
        var second = cache.GetOrCreate(key, () => ++createCalls);

        Assert.Equal(first, second);
        Assert.Equal(1, createCalls);
        Assert.Equal(1, cache.CreateCount);
        Assert.Equal(1, cache.HitCount);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void PipelineCacheDoesNotPublishFailedCreation()
    {
        var cache = new VulkanPipelineCache<string, int>();

        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("broken", static () => throw new InvalidOperationException("injected")));

        Assert.Equal(0, cache.CreateCount);
        Assert.Equal(0, cache.HitCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TransientPoolReusesReturnedResourceAndRejectsDoubleReturn()
    {
        var pool = new VulkanTransientResourcePool<string, int>();
        var createCalls = 0;

        var first = pool.Acquire("color", () => ++createCalls);
        pool.Return("color", first);
        var second = pool.Acquire("color", () => ++createCalls);

        Assert.Equal(first, second);
        Assert.Equal(1, createCalls);
        Assert.Equal(1, pool.CreateCount);
        Assert.Equal(1, pool.ReuseCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.FreeCount);

        pool.Return("color", second);
        Assert.Throws<InvalidOperationException>(() => pool.Return("color", second));
    }

    [Fact]
    public void TransientPoolKeepsDifferentDescriptionsIsolatedAndDrainsOnce()
    {
        var pool = new VulkanTransientResourcePool<string, int>();
        pool.Return("rgba8", 10);
        pool.Return("depth", 11);
        var released = new List<int>();

        pool.Drain(released.Add);
        pool.Drain(released.Add);

        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(2, released.Count);
        Assert.Contains(10, released);
        Assert.Contains(11, released);
    }

    [Fact]
    public void TransientPoolDoesNotPublishFailedFactoryResult()
    {
        var pool = new VulkanTransientResourcePool<string, int>();

        Assert.Throws<InvalidOperationException>(() => pool.Acquire("color", static () => throw new InvalidOperationException("injected")));

        Assert.Equal(0, pool.CreateCount);
        Assert.Equal(0, pool.ReuseCount);
        Assert.Equal(0, pool.FreeCount);
    }
}
