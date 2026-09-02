using System.Collections.Generic;
using System.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;
using Xunit;
using Xunit.Abstractions;

namespace Delta.Render.Tests;

public sealed class VulkanReuseTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void DependencyPlannerKeepsTrivialGraphsAllocationFree()
    {
        var planner = new VulkanGraphDependencyPlanner();
        Span<int> order = stackalloc int[1];
        var empty = Array.Empty<VulkanRenderGraph.GraphPass>();

        Assert.Equal(0, planner.Compile(empty, 0, order));
        Assert.Equal(0, planner.PassCapacity);
        Assert.Equal(0, planner.ResourceCapacity);

        var pass = new VulkanRenderGraph.GraphPass("empty", VulkanRenderGraph.PassKind.Transfer, null);
        pass.AddUse(new VulkanRenderGraph.GraphResource(), RenderResourceAccess.Read, RenderPipelineStages.Transfer);
        var passes = new[] { pass };
        Assert.Equal(1, planner.Compile(passes, 1, order));
        Assert.Equal(0, order[0]);
        Assert.Equal(0, planner.PassCapacity);
        Assert.Equal(0, planner.ResourceCapacity);

        var independent = new[]
        {
            new VulkanRenderGraph.GraphPass("raster", VulkanRenderGraph.PassKind.Raster, null),
            new VulkanRenderGraph.GraphPass("transfer", VulkanRenderGraph.PassKind.Transfer, null),
            new VulkanRenderGraph.GraphPass("compute", VulkanRenderGraph.PassKind.Compute, null),
        };
        Span<int> independentOrder = stackalloc int[3];
        Assert.Equal(3, planner.Compile(independent, 0, independentOrder));
        Assert.Equal(1, independentOrder[0]);
        Assert.Equal(0, independentOrder[1]);
        Assert.Equal(2, independentOrder[2]);
        Assert.Equal(0, planner.PassCapacity);
        Assert.Equal(0, planner.ResourceCapacity);
    }

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
    public void GraphPassMergesRepeatedResourceUses()
    {
        var pass = new VulkanRenderGraph.GraphPass("merge", VulkanRenderGraph.PassKind.Compute, null);
        var first = new VulkanRenderGraph.GraphResource();
        var second = new VulkanRenderGraph.GraphResource();

        pass.AddUse(first, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
        pass.AddUse(second, RenderResourceAccess.Write, RenderPipelineStages.Fragment);
        pass.AddUse(first, RenderResourceAccess.Write, RenderPipelineStages.Compute);
        pass.AddUse(first, RenderResourceAccess.Read, RenderPipelineStages.Transfer);

        Assert.Equal(2, pass.Uses.Count);
        Assert.Equal(first, pass.Uses[0].Resource);
        Assert.Equal(RenderResourceAccess.ReadWrite, pass.Uses[0].Access);
        Assert.Equal(RenderPipelineStages.Vertex | RenderPipelineStages.Compute | RenderPipelineStages.Transfer, pass.Uses[0].Stages);
        Assert.Equal(second, pass.Uses[1].Resource);
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
    public void DependencyPlannerHandlesManySharedResourceReaders()
    {
        const int resourceCount = 128;
        const int readerCount = 128;
        var planner = new VulkanGraphDependencyPlanner();
        var resources = new VulkanRenderGraph.GraphResource[resourceCount];
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index] = new VulkanRenderGraph.GraphResource { Index = index };
        }

        var passes = new List<VulkanRenderGraph.GraphPass>(readerCount + 1);
        var writer = new VulkanRenderGraph.GraphPass("writer", VulkanRenderGraph.PassKind.Transfer, null);
        for (int index = 0; index < resources.Length; index++)
        {
            writer.AddUse(resources[index], RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        }

        passes.Add(writer);
        for (int readerIndex = 0; readerIndex < readerCount; readerIndex++)
        {
            var reader = new VulkanRenderGraph.GraphPass($"reader-{readerIndex}", VulkanRenderGraph.PassKind.Compute, null);
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                reader.AddUse(resources[resourceIndex], RenderResourceAccess.Read, RenderPipelineStages.Compute);
            }

            passes.Add(reader);
        }

        int[] order = new int[passes.Count];
        Assert.Equal(passes.Count, planner.Compile(passes, resourceCount, order));
        for (int index = 0; index < order.Length; index++)
        {
            Assert.Equal(index, order[index]);
        }

        long[] samples = new long[5];
        for (int index = 0; index < samples.Length; index++)
        {
            long started = Stopwatch.GetTimestamp();
            Assert.Equal(passes.Count, planner.Compile(passes, resourceCount, order));
            samples[index] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(samples);
        double medianNanoseconds = samples[samples.Length / 2] * 1_000_000_000d / Stopwatch.Frequency;
        _output.WriteLine($"128 shared resources/readers median planner time: {medianNanoseconds:F2} ns");
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
    public void RasterPipelineCacheSeparatesBlendStateForOneShaderProgram()
    {
        IGraphicsShaderProgram program = new CacheShaderProgram();
        var opaque = new RasterPipelineDescription(program);
        var premultiplied = new RasterPipelineDescription(
            program,
            blendMode: RenderBlendMode.PremultipliedAlpha);
        var cache = new VulkanPipelineCache<RasterPipelineDescription, int>();
        var createCalls = 0;

        cache.GetOrCreate(opaque, () => ++createCalls);
        cache.GetOrCreate(premultiplied, () => ++createCalls);

        Assert.Equal(2, createCalls);
        Assert.Equal(2, cache.CreateCount);
        Assert.Equal(2, cache.Count);
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

    private sealed class CacheShaderProgram : IGraphicsShaderProgram
    {
        public IShaderArtifact Vertex { get; } = new CacheShaderArtifact(ShaderStage.Vertex);

        public IShaderArtifact Fragment { get; } = new CacheShaderArtifact(ShaderStage.Fragment);
    }

    private sealed class CacheShaderArtifact(ShaderStage stage) : IShaderArtifact
    {
        public ShaderStage Stage => stage;

        public string EntryPoint => "main";

        public ReadOnlySpan<byte> Spirv => ReadOnlySpan<byte>.Empty;

        public ShaderAbi Abi => new(stage);
    }
}
