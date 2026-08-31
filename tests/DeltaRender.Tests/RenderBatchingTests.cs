using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Text;
using Delta.Text.Contract;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class RenderBatchingTests
{
    [Fact]
    public void OrderedBatchesMatchLegacyOrderAndReuseWarmResources()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(640, 480), RenderBatchOrderMode.Ordered);
        var pipeline = RegisterPipeline(batcher);
        var materialA = batcher.RegisterMaterial([0xA1]);
        var materialB = batcher.RegisterMaterial([0xB2]);
        var clip = new PixelRect(0, 0, 640, 480);

        Apply(batcher, Item(1, materialA, 0, [1, 2, 3, 4], clip));
        Apply(batcher, Item(2, materialA, 1, [5, 6, 7, 8], clip));
        Apply(batcher, Item(3, materialB, 2, [9, 10, 11, 12], clip));

        var first = new RecordingGraphBuilder(session);
        batcher.AddPasses(first, 1);
        first.RecordAll();

        Assert.Equal(1, first.TransferPassCount);
        Assert.Equal(2, first.UploadBufferCount);
        Assert.Equal(2, first.Draws.Count);
        Assert.Equal(
            new byte[] { 1, 5, 9 },
            first.Draws.SelectMany(static draw => draw.InstancePayloads).Select(static payload => payload[0]).ToArray());
        Assert.Equal(
            new byte[] { 0xA1, 0xA1, 0xB2 },
            first.Draws.SelectMany(static draw => Enumerable.Repeat(draw.PushConstants[0], draw.InstancePayloads.Length)).ToArray());

        var creationCount = session.BufferCreationCount;
        var second = new RecordingGraphBuilder(session);
        batcher.AddPasses(second, 2);
        second.RecordAll();

        Assert.Equal(0, second.TransferPassCount);
        Assert.Equal(0, second.UploadBufferCount);
        Assert.Equal(creationCount, session.BufferCreationCount);
        Assert.Equal(
            new byte[] { 1, 5, 9 },
            second.Draws.SelectMany(static draw => draw.InstancePayloads).Select(static payload => payload[0]).ToArray());

        Apply(batcher, Item(2, materialA, 1, [50, 51, 52, 53], clip, version: 2));
        var update = new RecordingGraphBuilder(session);
        batcher.AddPasses(update, 3);
        update.RecordAll();

        Assert.Equal(1, update.TransferPassCount);
        Assert.Equal(1, update.UploadBufferCount);
        Assert.Equal(4, update.Uploads[0].Data.Length);
        Assert.Equal(new byte[] { 50, 51, 52, 53 }, update.Uploads[0].Data);
        Assert.Equal(
            new byte[] { 1, 50, 9 },
            update.Draws.SelectMany(static draw => draw.InstancePayloads).Select(static payload => payload[0]).ToArray());

        Assert.False(batcher.TryApply(Item(2, materialA, 1, [0, 0, 0, 0], clip, version: 2), out var staleDiagnostic));
        Assert.Contains("not newer", staleDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderedNonAdjacentChangesRemainSeparateWithinOneTransferPass()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Ordered);
        var pipeline = RegisterPipeline(batcher);
        var material = batcher.RegisterMaterial([]);
        var clip = new PixelRect(0, 0, 64, 64);

        Apply(batcher, Item(1, material, 0, [1, 0, 0, 0], clip));
        Apply(batcher, Item(2, material, 1, [2, 0, 0, 0], clip));
        Apply(batcher, Item(3, material, 2, [3, 0, 0, 0], clip));
        var warm = new RecordingGraphBuilder(session);
        batcher.AddPasses(warm, 1);
        warm.RecordAll();

        Apply(batcher, Item(1, material, 0, [11, 0, 0, 0], clip, version: 2));
        Apply(batcher, Item(3, material, 2, [33, 0, 0, 0], clip, version: 2));
        var changed = new RecordingGraphBuilder(session);
        batcher.AddPasses(changed, 2);
        changed.RecordAll();

        Assert.Equal(1, changed.TransferPassCount);
        Assert.Equal(2, changed.UploadBufferCount);
        Assert.Equal(new byte[] { 11, 0, 0, 0 }, changed.Uploads[0].Data);
        Assert.Equal(new byte[] { 33, 0, 0, 0 }, changed.Uploads[1].Data);
        Assert.Equal(new byte[] { 11, 2, 33 }, changed.Draws.Single().InstancePayloads.Select(static payload => payload[0]).ToArray());
    }

    [Fact]
    public void MaterialOnlyChangeUpdatesPushConstantsWithoutInstanceUpload()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Ordered);
        var pipeline = RegisterPipeline(batcher);
        var material = batcher.RegisterMaterial([1]);
        var clip = new PixelRect(0, 0, 64, 64);
        Apply(batcher, Item(1, material, 0, [7, 0, 0, 0], clip));

        var warm = new RecordingGraphBuilder(session);
        batcher.AddPasses(warm, 1);
        warm.RecordAll();

        Assert.True(batcher.TryUpdateMaterial(material, new RenderBatchVersion(2), [9], out var diagnostic), diagnostic);
        var changed = new RecordingGraphBuilder(session);
        batcher.AddPasses(changed, 2);
        changed.RecordAll();

        Assert.Equal(0, changed.TransferPassCount);
        Assert.Equal(0, changed.UploadBufferCount);
        Assert.Equal(new byte[] { 9 }, changed.Draws.Single().PushConstants);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, changed.Draws.Single().InstancePayloads.Single());
    }

    [Fact]
    public void UnorderedRemovalUsesSwapPopAndUploadsOnlyMovedSlot()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Unordered);
        var pipeline = RegisterPipeline(batcher);
        var material = batcher.RegisterMaterial([]);
        var clip = new PixelRect(0, 0, 64, 64);

        Apply(batcher, Item(1, material, 0, [1, 0, 0, 0], clip));
        Apply(batcher, Item(2, material, 1, [2, 0, 0, 0], clip));
        Apply(batcher, Item(3, material, 2, [3, 0, 0, 0], clip));
        var warm = new RecordingGraphBuilder(session);
        batcher.AddPasses(warm, 1);
        warm.RecordAll();

        Assert.True(batcher.TryRemove(new RenderBatchItemId(2, 1), new RenderBatchVersion(2), out var removeDiagnostic), removeDiagnostic);
        var changed = new RecordingGraphBuilder(session);
        batcher.AddPasses(changed, 2);
        changed.RecordAll();

        Assert.Equal(1, changed.TransferPassCount);
        Assert.Equal(1, changed.UploadBufferCount);
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, changed.Uploads[0].Data);
        Assert.Equal(new byte[] { 1, 3 }, changed.Draws.Single().InstancePayloads.Select(static payload => payload[0]).ToArray());
        Assert.False(batcher.TryRemove(new RenderBatchItemId(2, 1), new RenderBatchVersion(3), out var staleDiagnostic));
        Assert.Contains("stale", staleDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderedReorderKeepsTheLegacyCommandSequence()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Ordered);
        var pipeline = RegisterPipeline(batcher);
        var material = batcher.RegisterMaterial([]);
        var clip = new PixelRect(0, 0, 64, 64);

        Apply(batcher, Item(1, material, 0, [1, 0, 0, 0], clip));
        Apply(batcher, Item(2, material, 1, [2, 0, 0, 0], clip));
        Apply(batcher, Item(3, material, 2, [3, 0, 0, 0], clip));
        var warm = new RecordingGraphBuilder(session);
        batcher.AddPasses(warm, 1);
        warm.RecordAll();

        Apply(batcher, Item(1, material, 3, [1, 0, 0, 0], clip, version: 2));
        Apply(batcher, Item(2, material, 2, [2, 0, 0, 0], clip, version: 2));
        Apply(batcher, Item(3, material, 1, [3, 0, 0, 0], clip, version: 2));
        var reordered = new RecordingGraphBuilder(session);
        batcher.AddPasses(reordered, 2);
        reordered.RecordAll();

        Assert.Single(reordered.Draws);
        Assert.Equal(
            new byte[] { 3, 2, 1 },
            reordered.Draws.Single().InstancePayloads.Select(static payload => payload[0]).ToArray());
    }

    [Fact]
    public void BufferGrowthRetainsOldAllocationUntilIdempotentDispose()
    {
        using var session = new FakeSession();
        var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Ordered);
        var pipeline = RegisterPipeline(batcher);
        var material = batcher.RegisterMaterial([]);
        var clip = new PixelRect(0, 0, 64, 64);
        Apply(batcher, Item(1, material, 0, [1, 0, 0, 0], clip));
        var first = new RecordingGraphBuilder(session);
        batcher.AddPasses(first, 1);
        first.RecordAll();

        for (var id = 2UL; id <= 257; id++)
        {
            Apply(batcher, Item(id, material, id - 1, [2, 0, 0, 0], clip));
        }
        var grown = new RecordingGraphBuilder(session);
        batcher.AddPasses(grown, 2);
        grown.RecordAll();

        Assert.Equal(2, session.BufferCreationCount);
        batcher.Dispose();
        Assert.Equal(2, session.ReleasedBufferCount);
        batcher.Dispose();
        Assert.Equal(2, session.ReleasedBufferCount);
    }

    /// <summary>
    /// Mirrors the 5,000-command shape and 715 paint mutations from Xamy's
    /// renderer-neutral workload commit b35a5fa; payloads are supplied as
    /// already-packed bytes at this renderer boundary.
    /// </summary>
    [Fact]
    public void XamlWorkloadKeepsOrderedStreamWhileUploadingOnlyPaintChanges()
    {
        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(8192, 4096), RenderBatchOrderMode.Ordered);
        var pipelines = new[] { RegisterPipeline(batcher, 16), RegisterPipeline(batcher, 16) };
        var materials = new[]
        {
            batcher.RegisterMaterial([0xA0]),
            batcher.RegisterMaterial([0xA1]),
            batcher.RegisterMaterial([0xA2]),
            batcher.RegisterMaterial([0xA3]),
        };
        var displayList = CreateWorkloadDisplayList();
        Assert.Equal(3_500, displayList.Visuals.Length);
        Assert.Equal(1_500, displayList.Text.Length);
        Assert.Equal(256, displayList.Clips.Length);
        Assert.Equal(5_000, displayList.Order.Length);
        var entries = new WorkloadEntry[5_000];
        var expectedFirstBytes = new byte[entries.Length];
        var clip = new PixelRect(0, 0, 8192, 4096);

        for (var index = 0; index < entries.Length; index++)
        {
            var sourceRef = displayList.Order[index];
            Assert.Equal(index % 10 < 7 ? UiDrawKind.Visual : UiDrawKind.Text, sourceRef.Kind);
            var run = index / 125;
            var materialSlot = run % materials.Length;
            entries[index] = new WorkloadEntry(
                new RenderBatchItemId((ulong)index + 1, 1),
                pipelines[run % pipelines.Length],
                materials[materialSlot],
                materialSlot,
                new RenderBatchOrderKey(0, (ulong)index),
                clip);
            expectedFirstBytes[index] = (byte)index;
            ApplyWorkload(batcher, entries[index], expectedFirstBytes[index], 1);
        }

        var cold = new RecordingGraphBuilder(session);
        batcher.AddPasses(cold, 1);
        cold.RecordAll();

        Assert.Equal(40, cold.Draws.Count);
        Assert.Equal(40, cold.BindBufferCount);
        Assert.Equal(5_000, cold.Draws.Sum(static draw => draw.InstancePayloads.Length));
        Assert.Equal(80_000, cold.Uploads.Sum(static upload => upload.Data.Length));
        AssertWorkloadStream(cold, entries, expectedFirstBytes);

        var warm = new RecordingGraphBuilder(session);
        batcher.AddPasses(warm, 2);
        warm.RecordAll();

        Assert.Equal(0, warm.TransferPassCount);
        Assert.Equal(0, warm.UploadBufferCount);
        Assert.Equal(40, warm.Draws.Count);
        Assert.Equal(40, warm.BindBufferCount);
        AssertWorkloadStream(warm, entries, expectedFirstBytes);

        for (var index = 0; index < entries.Length; index++)
        {
            if (index % 7 != 0)
            {
                continue;
            }

            expectedFirstBytes[index] = (byte)(index ^ 0xA5);
            ApplyWorkloadUnchecked(batcher, entries[index], expectedFirstBytes[index], 2);
        }

        var paint = new RecordingGraphBuilder(session);
        batcher.AddPasses(paint, 3);
        paint.RecordAll();

        Assert.Equal(2, paint.TransferPassCount);
        Assert.Equal(715, paint.UploadBufferCount);
        Assert.Equal(11_440, paint.Uploads.Sum(static upload => upload.Data.Length));
        Assert.Equal(40, paint.BindBufferCount);
        Assert.Equal(80_000, entries.Length * 16);
        AssertWorkloadStream(paint, entries, expectedFirstBytes);

        var allocatedBeforeWarmPaint = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < entries.Length; index++)
        {
            if (index % 7 != 0)
            {
                continue;
            }

            expectedFirstBytes[index] = (byte)(index ^ 0x5A);
            ApplyWorkloadUnchecked(batcher, entries[index], expectedFirstBytes[index], 3);
        }

        var warmPaintAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeWarmPaint;
        var warmPaint = new RecordingGraphBuilder(session);
        batcher.AddPasses(warmPaint, 4);
        warmPaint.RecordAll();

        Assert.Equal(0L, warmPaintAllocations);
        Assert.Equal(2, warmPaint.TransferPassCount);
        Assert.Equal(715, warmPaint.UploadBufferCount);
        Assert.Equal(11_440, warmPaint.Uploads.Sum(static upload => upload.Data.Length));
        Assert.Equal(40, warmPaint.BindBufferCount);
        AssertWorkloadStream(warmPaint, entries, expectedFirstBytes);
    }

    [Fact]
    public void CanonicalDisplayListOrderFeedsTheBatcherWithoutReconstruction()
    {
        var visuals = new[]
        {
            new UiVisualDraw(
                UiVisualKind.SolidRectangle,
                default,
                new float4(0, 0, 10, 10),
                new float4(1, 0, 0, 1),
                new UiClipId(0),
                default),
            new UiVisualDraw(
                UiVisualKind.RoundedRectangle,
                default,
                new float4(20, 0, 10, 10),
                new float4(0, 0, 1, 1),
                new UiClipId(0),
                default),
        };
        var clips = new[] { new UiClipRegion(new float4(0, 0, 64, 64), UiClipId.None) };
        var text = new[]
        {
            UiTextDraw.WithPaint(
                new UiTextRunId(1, 1),
                1,
                CreateTestShapedText(),
                new float2(10, 20),
                UiTextPaint.Solid(new float4(1, 1, 1, 1)),
                new UiClipId(0)),
        };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Text, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };
        var displayList = new UiDisplayList(visuals, clips, text, order);

        using var session = new FakeSession();
        using var batcher = new RenderBatcher(session, new PixelExtent(64, 64), RenderBatchOrderMode.Ordered);
        var pipelines = new[] { RegisterPipeline(batcher), RegisterPipeline(batcher) };
        var material = batcher.RegisterMaterial([]);
        var clip = new PixelRect(0, 0, 64, 64);

        Span<byte> payload = stackalloc byte[4];
        for (var index = 0; index < displayList.Order.Length; index++)
        {
            var draw = displayList.Order[index];
            payload[0] = (byte)(10 + index);
            var pipeline = draw.Kind == UiDrawKind.Text ? pipelines[1] : pipelines[0];
            var change = new RenderBatchItemChange(
                new RenderBatchItemId((ulong)index + 1, 1),
                new RenderBatchVersion(1),
                new RenderBatchOrderKey(0, (ulong)index),
                new RenderBatchKey(pipeline, material, clip),
                payload);
            Assert.True(batcher.TryApply(change, out var diagnostic), diagnostic);
        }

        var graph = new RecordingGraphBuilder(session);
        batcher.AddPasses(graph, 1);
        graph.RecordAll();

        Assert.Equal(3, graph.Draws.Count);
        Assert.Equal(
            new byte[] { 10, 11, 12 },
            graph.Draws.SelectMany(static draw => draw.InstancePayloads).Select(static payload => payload[0]).ToArray());
    }

    private static RenderBatchPipelineHandle RegisterPipeline(RenderBatcher batcher, uint instanceStride = 4)
        => batcher.RegisterPipeline(
            new RasterPipelineDescription(new FakeShaderProgram()),
            new ShaderBinding(0, 0),
            instanceStride);

    private static RenderBatchItemChange Item(
        ulong id,
        RenderBatchMaterialHandle material,
        ulong sequence,
        byte[] payload,
        PixelRect clip,
        ulong version = 1)
        => new(
            new RenderBatchItemId(id, 1),
            new RenderBatchVersion(version),
            new RenderBatchOrderKey(0, sequence),
            new RenderBatchKey(
                new RenderBatchPipelineHandle(1, 1),
                material,
                clip),
            payload);

    private static void Apply(RenderBatcher batcher, RenderBatchItemChange change)
        => Assert.True(batcher.TryApply(change, out var diagnostic), diagnostic);

    private static void ApplyWorkload(
        RenderBatcher batcher,
        WorkloadEntry entry,
        byte firstByte,
        ulong version)
    {
        Span<byte> payload = stackalloc byte[16];
        payload[0] = firstByte;
        for (var index = 1; index < payload.Length; index++)
        {
            payload[index] = (byte)(firstByte + index * 17);
        }

        var change = new RenderBatchItemChange(
            entry.Id,
            new RenderBatchVersion(version),
            entry.Order,
            new RenderBatchKey(entry.Pipeline, entry.Material, entry.Clip),
            payload);
        Assert.True(batcher.TryApply(change, out var diagnostic), diagnostic);
    }

    private static void ApplyWorkloadUnchecked(
        RenderBatcher batcher,
        WorkloadEntry entry,
        byte firstByte,
        ulong version)
    {
        Span<byte> payload = stackalloc byte[16];
        payload[0] = firstByte;
        for (var index = 1; index < payload.Length; index++)
        {
            payload[index] = (byte)(firstByte + index * 17);
        }

        var change = new RenderBatchItemChange(
            entry.Id,
            new RenderBatchVersion(version),
            entry.Order,
            new RenderBatchKey(entry.Pipeline, entry.Material, entry.Clip),
            payload);
        if (!batcher.TryApply(change, out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private static void AssertWorkloadStream(
        RecordingGraphBuilder graph,
        WorkloadEntry[] entries,
        byte[] expectedFirstBytes)
    {
        var orderedIndex = 0;
        foreach (var draw in graph.Draws)
        {
            Assert.NotEmpty(draw.PushConstants);
            for (var instance = 0; instance < draw.InstancePayloads.Length; instance++)
            {
                Assert.Equal(expectedFirstBytes[orderedIndex], draw.InstancePayloads[instance][0]);
                Assert.Equal((byte)(0xA0 + entries[orderedIndex].MaterialSlot), draw.PushConstants[0]);
                orderedIndex++;
            }
        }

        Assert.Equal(entries.Length, orderedIndex);
    }

    private static UiDisplayList CreateWorkloadDisplayList()
    {
        var visuals = new UiVisualDraw[3_500];
        var text = new UiTextDraw[1_500];
        var clips = new UiClipRegion[256];
        var order = new UiDrawRef[5_000];
        for (var index = 0; index < clips.Length; index++)
        {
            clips[index] = new UiClipRegion(new float4(0, 0, 8192, 4096), UiClipId.None);
        }

        var visualIndex = 0;
        var textIndex = 0;
        for (var index = 0; index < order.Length; index++)
        {
            var clip = new UiClipId(index % clips.Length);
            if (index % 10 < 7)
            {
                visuals[visualIndex] = new UiVisualDraw(
                    UiVisualKind.SolidRectangle,
                    default,
                    new float4(index % 100 * 8, index / 100 * 6, 64, 24),
                    new float4(1, 1, 1, 1),
                    clip,
                    default);
                order[index] = new UiDrawRef(UiDrawKind.Visual, visualIndex++);
                continue;
            }

            text[textIndex] = UiTextDraw.WithPaint(
                new UiTextRunId((uint)index + 1, 1),
                1,
                CreateTestShapedText(),
                new float2(index % 100 * 8, index / 100 * 6),
                UiTextPaint.Solid(new float4(1, 1, 1, 1)),
                clip);
            order[index] = new UiDrawRef(UiDrawKind.Text, textIndex++);
        }

        return new UiDisplayList(visuals, clips, text, order);
    }

    private sealed class FakeSession : IRenderFrameSession, IDisposable
    {
        private ulong _nextBuffer = 1;
        private readonly Dictionary<RenderBufferHandle, byte[]> _buffers = [];

        public RenderDeviceCapabilities Capabilities => new(0, 4, 128, 4, 64, 64, 1, 1, 65535, 65535, 65535);

        public bool ProfilingEnabled => false;

        public IRenderProfiler? Profiler => null;

        public RenderTargetHandle Target => new(1, 1);

        public int BufferCreationCount { get; private set; }

        public int ReleasedBufferCount { get; private set; }

        public IRenderGraph CreateRenderGraph() => throw new NotSupportedException();

        public bool TryReinitializeAfterDeviceLoss() => false;

        public RenderBufferHandle CreateBuffer(in RenderBufferDescription description)
        {
            var handle = new RenderBufferHandle(_nextBuffer++, 1);
            _buffers.Add(handle, new byte[checked((int)description.SizeInBytes)]);
            BufferCreationCount++;
            return handle;
        }

        public RenderTextureHandle CreateTexture(in RenderTextureDescription description) => throw new NotSupportedException();

        public RenderSamplerHandle CreateSampler(in RenderSamplerDescription description) => throw new NotSupportedException();

        public void Release(RenderBufferHandle buffer)
        {
            if (_buffers.Remove(buffer))
            {
                ReleasedBufferCount++;
            }
        }

        public void Release(RenderTextureHandle texture) { }

        public void Release(RenderSamplerHandle sampler) { }

        public void ResizeTarget(in PixelExtent extent) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() => _buffers.Clear();

        public void Write(RenderBufferHandle buffer, ulong offset, ReadOnlySpan<byte> data)
            => data.CopyTo(_buffers[buffer].AsSpan(checked((int)offset), data.Length));

        public void Read(RenderBufferHandle buffer, ulong offset, Span<byte> destination)
            => _buffers[buffer].AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
    }

    private sealed class RecordingGraphBuilder :
        IRenderGraphBuilder,
        ITransferCommandContext,
        IRasterCommandContext
    {
        private readonly FakeSession _session;
        private uint _nextHandle = 1;
        private readonly Dictionary<RenderGraphBufferHandle, RenderBufferHandle> _buffers = [];
        private readonly List<ITransferPass> _transferPasses = [];
        private readonly List<IRasterPass> _rasterPasses = [];
        private RenderBufferHandle _boundBuffer;
        private ulong _boundOffset;
        private ulong _boundSize;
        private byte[] _pushConstants = [];

        public RecordingGraphBuilder(FakeSession session) => _session = session;

        public int TransferPassCount => _transferPasses.Count;

        public int UploadBufferCount { get; private set; }

        public int BindBufferCount { get; private set; }

        public List<Upload> Uploads { get; } = [];

        public List<Draw> Draws { get; } = [];

        public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target) => new(_nextHandle++);

        public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture) => new(_nextHandle++);

        public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer)
        {
            var handle = new RenderGraphBufferHandle(_nextHandle++);
            _buffers.Add(handle, buffer);
            return handle;
        }

        public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description) => new(_nextHandle++);

        public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description) => new(_nextHandle++);

        public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
        {
            _rasterPasses.Add(pass);
            return new RenderGraphPassHandle(_nextHandle++);
        }

        public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
            => new(_nextHandle++);

        public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
        {
            _transferPasses.Add(pass);
            return new RenderGraphPassHandle(_nextHandle++);
        }

        public void UseColorAttachment(RenderGraphPassHandle pass, uint index, in ColorAttachmentDescription attachment) { }

        public void UseDepthStencilAttachment(RenderGraphPassHandle pass, in DepthStencilAttachmentDescription attachment) { }

        public void UseTexture(RenderGraphPassHandle pass, RenderGraphTextureHandle texture, RenderResourceAccess access, RenderPipelineStages stages) { }

        public void UseBuffer(RenderGraphPassHandle pass, RenderGraphBufferHandle buffer, RenderResourceAccess access, RenderPipelineStages stages) { }

        public RenderGraphReadbackHandle ReadbackBuffer(RenderGraphBufferHandle buffer, in BufferRange range) => new(_nextHandle++);

        public RenderGraphReadbackHandle ReadbackTexture(RenderGraphTextureHandle texture, in PixelRect region) => new(_nextHandle++);

        public void RecordAll()
        {
            for (var index = 0; index < _transferPasses.Count; index++)
            {
                _transferPasses[index].Record(this);
            }

            for (var index = 0; index < _rasterPasses.Count; index++)
            {
                _rasterPasses[index].Record(this);
            }
        }

        public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0)
            => throw new NotSupportedException();

        public void CopyTexture(RenderGraphTextureHandle source, in PixelRect sourceRegion, RenderGraphTextureHandle destination, in PixelRect destinationRegion)
            => throw new NotSupportedException();

        public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
        {
            var buffer = _buffers[destination];
            var copy = data.ToArray();
            _session.Write(buffer, destinationOffset, copy);
            UploadBufferCount++;
            Uploads.Add(new Upload(destinationOffset, copy));
        }

        public void UploadTexture(RenderGraphTextureHandle destination, in PixelRect destinationRegion, ReadOnlySpan<byte> data, uint sourceRowPitch)
            => throw new NotSupportedException();

        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0)
        {
            BindBufferCount++;
            _boundBuffer = _buffers[buffer];
            _boundOffset = offset;
            _boundSize = sizeInBytes;
        }

        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
            => throw new NotSupportedException();

        public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => _pushConstants = data.ToArray();

        public void SetViewport(in RenderViewport viewport) { }

        public void SetScissor(in PixelRect scissor) { }

        public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0)
            => throw new NotSupportedException();

        public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0)
            => throw new NotSupportedException();

        public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
        {
            var stride = checked((int)(_boundSize / instanceCount));
            var payloads = new byte[instanceCount][];
            for (var index = 0; index < instanceCount; index++)
            {
                payloads[index] = new byte[stride];
                _session.Read(_boundBuffer, checked(_boundOffset + (ulong)(index * stride)), payloads[index]);
            }

            Draws.Add(new Draw(_pushConstants, payloads));
        }

        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
            => throw new NotSupportedException();
    }

    private static ShapedText CreateTestShapedText()
    {
        using var textService = new SixLaborsTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        return textService.Shape(new TextShapeRequest("A".AsMemory(), 16, new[] { font }));
    }

    private sealed class FakeShaderProgram : IGraphicsShaderProgram
    {
        public IShaderArtifact Vertex { get; } = new FakeShaderArtifact(ShaderStage.Vertex);

        public IShaderArtifact Fragment { get; } = new FakeShaderArtifact(ShaderStage.Fragment);
    }

    private sealed class FakeShaderArtifact(ShaderStage stage) : IShaderArtifact
    {
        public ShaderStage Stage => stage;

        public string EntryPoint => "main";

        public ReadOnlySpan<byte> Spirv => ReadOnlySpan<byte>.Empty;

        public ShaderAbi Abi => new(stage);
    }

    private readonly record struct WorkloadEntry(
        RenderBatchItemId Id,
        RenderBatchPipelineHandle Pipeline,
        RenderBatchMaterialHandle Material,
        int MaterialSlot,
        RenderBatchOrderKey Order,
        PixelRect Clip);

    private readonly record struct Upload(ulong DestinationOffset, byte[] Data);

    private sealed record Draw(byte[] PushConstants, byte[][] InstancePayloads);
}
