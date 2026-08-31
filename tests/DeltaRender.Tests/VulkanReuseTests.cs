using System.Collections.Generic;
using System.Diagnostics;
using Delta.Render.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Delta.Render.Tests;

public sealed class VulkanReuseTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

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
        int initialPassCapacity = planner.PassCapacity;
        int initialResourceCapacity = planner.ResourceCapacity;

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
    public void FrameSlotsWrapInStableRingOrder()
    {
        var slots = new VulkanFrameSlots(3);

        Assert.Equal(0, slots.Advance());
        Assert.Equal(1, slots.Advance());
        Assert.Equal(2, slots.Advance());
        Assert.Equal(0, slots.Advance());
        Assert.Equal(1, slots.Advance());
    }

    [Fact]
    public void DependencyPlannerKeepsTransferPriorityWithoutReorderingEachQueue()
    {
        var planner = new VulkanGraphDependencyPlanner();
        var passes = new List<VulkanRenderGraph.GraphPass>
        {
            new("raster", VulkanRenderGraph.PassKind.Raster, null),
            new("compute", VulkanRenderGraph.PassKind.Compute, null),
            new("transfer", VulkanRenderGraph.PassKind.Transfer, null),
        };
        Span<int> order = stackalloc int[3];

        Assert.Equal(3, planner.Compile(passes, 0, order));
        Assert.Equal(2, order[0]);
        Assert.Equal(0, order[1]);
        Assert.Equal(1, order[2]);
    }

    [Fact]
    public void DependencyPlannerHandlesLargeIndependentGraph()
    {
        const int passCount = 4096;
        var planner = new VulkanGraphDependencyPlanner();
        var passes = new List<VulkanRenderGraph.GraphPass>(passCount);
        for (int index = 0; index < passCount; index++)
        {
            var kind = index % 8 == 0
                ? VulkanRenderGraph.PassKind.Transfer
                : VulkanRenderGraph.PassKind.Raster;
            passes.Add(new VulkanRenderGraph.GraphPass($"pass-{index}", kind, null));
        }

        int[] order = new int[passCount];
        Assert.Equal(passCount, planner.Compile(passes, 0, order));

        long[] samples = new long[5];
        for (int index = 0; index < samples.Length; index++)
        {
            long started = Stopwatch.GetTimestamp();
            Assert.Equal(passCount, planner.Compile(passes, 0, order));
            samples[index] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(samples);
        double medianNanoseconds = samples[samples.Length / 2] * 1_000_000_000d / Stopwatch.Frequency;
        _output.WriteLine($"4096 independent passes median planner time: {medianNanoseconds:F2} ns");
    }

    [Fact]
    public void PipelineCacheCreatesOnceAndReportsWarmHit()
    {
        var cache = new VulkanPipelineCache<object, int>(ReferenceEqualityComparer.Instance);
        object key = new();
        int createCalls = 0;

        int first = cache.GetOrCreate(key, () => ++createCalls);
        int second = cache.GetOrCreate(key, () => ++createCalls);

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
        int createCalls = 0;

        int first = pool.Acquire("color", () => ++createCalls);
        pool.Return("color", first);
        int second = pool.Acquire("color", () => ++createCalls);

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

    [Fact]
    public void TransientPoolSupportsDirectTakeAndCreateAccounting()
    {
        var pool = new VulkanTransientResourcePool<string, int>();

        Assert.False(pool.TryTake("color", out _));

        pool.RecordCreated();
        pool.Return("color", 42);

        Assert.True(pool.TryTake("color", out int reused));
        Assert.Equal(42, reused);
        Assert.Equal(1, pool.CreateCount);
        Assert.Equal(1, pool.ReuseCount);
        Assert.Equal(0, pool.FreeCount);
    }

    [Fact]
    public void TransientPoolDirectTakePathHasNoWarmAllocations()
    {
        var pool = new VulkanTransientResourcePool<string, int>();

        for (int index = 0; index < 32; index++)
        {
            int value = index;
            pool.RecordCreated();
            pool.Return("color", value);
            Assert.True(pool.TryTake("color", out int reused));
            Assert.Equal(value, reused);
            pool.Return("color", reused);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            Assert.True(pool.TryTake("color", out int reused));
            pool.Return("color", reused);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
