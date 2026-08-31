using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Shader.Text;
using Delta.Shader.UI;
using Delta.Text;
using Delta.Text.Contract;
using Delta.XAML.Contract;
using Xunit;
using Xunit.Abstractions;

namespace Delta.Render.Tests;

public sealed class UiDisplayListGraphFeatureTests
{
    private readonly ITestOutputHelper _output;

    public UiDisplayListGraphFeatureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConsumePreservesCanonicalDrawOrder()
    {
        var visuals = new[]
        {
            Solid(1),
            Solid(2),
            Solid(3),
        };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
            new UiDrawRef(UiDrawKind.Visual, 2),
        };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.True(
            feature.Consume(new UiDisplayList(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
            string.Join(" | ", feature.Diagnostics));
        Assert.Equal(order, feature.BorrowOrder().ToArray());
    }

    [Fact]
    public void DuplicatePayloadReferenceIsRejected()
    {
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.False(feature.Consume(new UiDisplayList(
            new[] { Solid(1), Solid(2) },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[]
            {
                new UiDrawRef(UiDrawKind.Visual, 0),
                new UiDrawRef(UiDrawKind.Visual, 1),
                new UiDrawRef(UiDrawKind.Visual, 0),
            })));
        Assert.Contains(feature.Diagnostics, static message => message == "Order[2] references visual 0 more than once.");
    }

    [Fact]
    public void ConsumeCopiesBorrowedOrderBeforeProducerMutation()
    {
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.True(feature.Consume(new UiDisplayList(
            new[] { Solid(1), Solid(2) },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            order)));

        order[0] = new UiDrawRef(UiDrawKind.Visual, 1);

        Assert.Equal(new UiDrawRef(UiDrawKind.Visual, 0), feature.BorrowOrder()[0]);
    }

    [Fact]
    public void ConsumeCopiesVisualClipTextAndOrderBeforeProducerMutation()
    {
        using var textService = new SixLaborsTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 24, new[] { font }));
        var visuals = new[] { Solid(1, new UiClipId(0)) };
        var clips = new[] { new UiClipRegion(new float4(10, 12, 50, 30), UiClipId.None) };
        var texts = new[]
        {
            UiTextDraw.WithPaint(
                new UiTextRunId(1, 1),
                1,
                shaped,
                new float2(4, 5),
                UiTextPaint.Solid(new float4(0.2f, 0.3f, 0.4f, 1)),
                new UiClipId(0)),
        };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Text, 0),
        };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.True(feature.Consume(new UiDisplayList(visuals, clips, texts, order)), string.Join(" | ", feature.Diagnostics));
        var expectedVisual = visuals[0];
        var expectedClip = clips[0];
        var expectedText = texts[0];
        var expectedOrder = order[0];
        visuals[0] = Solid(9);
        clips[0] = new UiClipRegion(new float4(0, 0, 1, 1), UiClipId.None);
        texts[0] = UiTextDraw.WithPaint(
            new UiTextRunId(1, 1),
            2,
            shaped,
            default,
            UiTextPaint.Solid(default),
            UiClipId.None);
        order[0] = new UiDrawRef(UiDrawKind.Text, 0);

        Assert.Equal(expectedVisual, feature.BorrowVisuals()[0]);
        Assert.Equal(expectedClip, feature.BorrowClips()[0]);
        Assert.Equal(expectedText, feature.BorrowTexts()[0]);
        Assert.Equal(shaped, feature.BorrowTexts()[0].Text);
        Assert.Equal(expectedOrder, feature.BorrowOrder()[0]);
        Assert.Equal(new PixelRect(10, 12, 50, 30), feature.GetEffectiveClip(0));
        Assert.Equal(new PixelRect(10, 12, 50, 30), feature.GetEffectiveClip(1));
    }

    [Fact]
    public void AdjacentVisualsUseOneRasterSegmentWithoutReorderingDraws()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var visuals = new[] { Solid(1), Solid(2) };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };

        Assert.True(
            feature.Consume(new UiDisplayList(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.True(graph.RasterPasses.Count > 0, string.Join(" | ", feature.Diagnostics));
        Assert.Single(graph.RasterPasses);
        Assert.Single(commands.InstanceCounts);
        Assert.Equal(2u, commands.InstanceCounts[0]);
        Assert.Single(commands.PushedConstants);
        Assert.Single(commands.Scissors);
        Assert.Equal(100f, ReadFloat(commands.PushedConstants[0], 0));
        Assert.Equal(80f, ReadFloat(commands.PushedConstants[0], 4));
    }

    [Fact]
    public void CompatibleVisualsWithDifferentClipsUseOnePassAndPreserveScissorCommands()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var clips = new[]
        {
            new UiClipRegion(new float4(0, 0, 40, 40), UiClipId.None),
            new UiClipRegion(new float4(40, 0, 40, 40), UiClipId.None),
        };
        var visuals = new[]
        {
            Solid(1, new UiClipId(0)),
            Solid(2, new UiClipId(1)),
        };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };

        Assert.True(feature.Consume(new UiDisplayList(visuals, clips, Array.Empty<UiTextDraw>(), order)), string.Join(" | ", feature.Diagnostics));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(graph.RasterPasses);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(feature.GetEffectiveClip(0), commands.Scissors[0]);
        Assert.Equal(feature.GetEffectiveClip(1), commands.Scissors[1]);
        Assert.Equal(new[] { 1u, 1u }, commands.InstanceCounts);
    }

    [Fact]
    public void TextOnlyDisplayListAddsTextRasterPassWithoutVisualInstances()
    {
        using var textService = new SixLaborsTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);

        var text = UiTextDraw.WithPaint(
            new UiTextRunId(1, 1),
            1,
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)),
            UiClipId.None);
        Assert.True(
            feature.Consume(new UiDisplayList(
                Array.Empty<UiVisualDraw>(),
                Array.Empty<UiClipRegion>(),
                new[] { text },
                new[] { new UiDrawRef(UiDrawKind.Text, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.NotEmpty(graph.RasterPasses);
        Assert.Equal(1, feature.BorrowOrder().Length);
        Assert.Equal(new UiDrawRef(UiDrawKind.Text, 0), feature.BorrowOrder()[0]);
    }

    [Fact]
    public void RepeatedTextOnlyConsumeDoesNotAccumulatePreviousFrameRuns()
    {
        using var textService = new SixLaborsTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);
        var displayList = new UiDisplayList(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[]
            {
                UiTextDraw.WithPaint(
                    new UiTextRunId(1, 1),
                    1,
                    shaped,
                    new float2(10, 20),
                    UiTextPaint.Solid(new float4(1, 1, 1, 1)),
                    UiClipId.None),
            },
            new[] { new UiDrawRef(UiDrawKind.Text, 0) });

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        var firstCommands = new RecordingRasterCommands();
        firstGraph.RecordRaster(firstCommands);

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        var secondGraph = new RecordingGraphBuilder();
        feature.AddPasses(secondGraph, 2);
        var secondCommands = new RecordingRasterCommands();
        secondGraph.RecordRaster(secondCommands);

        Assert.Equal(firstCommands.InstanceCounts, secondCommands.InstanceCounts);
        Assert.Equal(firstCommands.DrawCount, secondCommands.DrawCount);
    }

    [Fact]
    public void FiveThousandAdjacentVisualsUseOneInstancedDraw()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(4096, 4096));
        var visuals = new UiVisualDraw[5000];
        var order = new UiDrawRef[visuals.Length];
        for (var index = 0; index < visuals.Length; index++)
        {
            visuals[index] = Solid(index % 100);
            order[index] = new UiDrawRef(UiDrawKind.Visual, index);
        }

        Assert.True(
            feature.Consume(new UiDisplayList(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        var stopwatch = Stopwatch.StartNew();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);
        stopwatch.Stop();

        Assert.Single(graph.RasterPasses);
        Assert.Single(commands.InstanceCounts);
        Assert.Equal(5000u, commands.InstanceCounts[0]);
        _output.WriteLine(
            $"Contiguous visual benchmark: visuals={visuals.Length}, naiveDraws={visuals.Length}, " +
            $"instancedDraws={commands.InstanceCounts.Count}, buildAndRecord={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [Fact]
    public void PaintOnlyConsumeReusesRetainedStorageAndUpdatesValue()
    {
        var firstVisual = Solid(1);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));
        var firstList = new UiDisplayList(
            new[] { firstVisual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) });

        Assert.True(feature.Consume(firstList), string.Join(" | ", feature.Diagnostics));
        ref var retained = ref MemoryMarshal.GetReference(feature.BorrowVisuals());
        var updatedVisual = Solid(2);
        var secondList = new UiDisplayList(
            new[] { updatedVisual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) });

        Assert.True(feature.Consume(secondList), string.Join(" | ", feature.Diagnostics));
        ref var current = ref MemoryMarshal.GetReference(feature.BorrowVisuals());

        Assert.True(Unsafe.AreSame(ref retained, ref current));
        Assert.Equal(updatedVisual, current);
    }

    [Fact]
    public void ResourceRegistryCacheHitReturnsSameHandlesWithoutAllocation()
    {
        var registry = new UiDisplayListResourceRegistry();
        var resource = new UiResourceId(Guid.NewGuid());
        var texture = new RenderTextureHandle(12, 3);
        var sampler = new RenderSamplerHandle(13, 4);
        registry.RegisterImage(resource, texture, sampler);

        for (var index = 0; index < 4; index++)
        {
            Assert.True(registry.TryResolveImage(resource, out _, out _, out _));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(registry.TryResolveImage(resource, out var resolvedTexture, out var resolvedSampler, out var resolvedBinding));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(texture, resolvedTexture);
        Assert.Equal(sampler, resolvedSampler);
        Assert.Null(resolvedBinding);
    }

    [Fact]
    public void UnchangedWarmConsumeDoesNotAllocate()
    {
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));
        var visuals = new[] { Solid(1) };
        var clips = Array.Empty<UiClipRegion>();
        var texts = Array.Empty<UiTextDraw>();
        var order = new[] { new UiDrawRef(UiDrawKind.Visual, 0) };
        var displayList = new UiDisplayList(visuals, clips, texts, order);

        for (var index = 0; index < 4; index++)
        {
            Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
        {
            Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ConsumeResolvesNestedRectangularClipsAndViewportIntersection()
    {
        var clips = new[]
        {
            new UiClipRegion(new float4(10, 10, 80, 60), UiClipId.None),
            new UiClipRegion(new float4(20, 20, 80, 70), new UiClipId(0)),
        };
        var visuals = new[] { Solid(1, new UiClipId(1)) };
        var order = new[] { new UiDrawRef(UiDrawKind.Visual, 0) };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.True(feature.Consume(new UiDisplayList(visuals, clips, Array.Empty<UiTextDraw>(), order)));
        Assert.Equal(new PixelRect(20, 20, 70, 50), feature.GetEffectiveClip(0));
    }

    [Fact]
    public void UnsupportedRoundedPaintIsDiagnosedWithoutFallback()
    {
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(0, 0, 10, 10),
            new UiVisualPaint(new float4(1, 1, 1, 1), default, 0, new float4(2, 2, 2, 2)),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.False(feature.Consume(new UiDisplayList(
            new[] { visual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })));
        Assert.Contains(feature.Diagnostics, static message => message.Contains("stroke or rounded", StringComparison.Ordinal));
    }

    [Fact]
    public void NonUniformRoundedPaintIsAccepted()
    {
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(0, 0, 20, 10),
            new UiVisualPaint(
                new float4(1, 1, 1, 1),
                new float4(0, 0, 0, 1),
                1,
                new float4(1, 2, 3, 4)),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.True(feature.Consume(new UiDisplayList(
            new[] { visual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })), string.Join(" | ", feature.Diagnostics));
    }

    [Fact]
    public void RoundedPaintWithAdjacentRadiiOutsideBoundsIsRejected()
    {
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(0, 0, 10, 10),
            new UiVisualPaint(
                new float4(1, 1, 1, 1),
                default,
                0,
                new float4(6, 6, 1, 1)),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.False(feature.Consume(new UiDisplayList(
            new[] { visual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })));
        Assert.Contains(feature.Diagnostics, static message => message == "Visual at Order[0] has adjacent corner radii exceeding its bounds.");
    }

    [Fact]
    public void ClipParentCycleIsRejectedWithDeterministicDiagnostic()
    {
        var clips = new[]
        {
            new UiClipRegion(new float4(0, 0, 20, 20), new UiClipId(1)),
            new UiClipRegion(new float4(0, 0, 20, 20), new UiClipId(0)),
        };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.False(feature.Consume(new UiDisplayList(
            new[] { Solid(1, new UiClipId(0)) },
            clips,
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })));
        Assert.Contains(feature.Diagnostics, static message => message == "Clip 0 contains a parent cycle.");
    }

    [Fact]
    public void ResourceRegistryRetainsSemanticIdentityAndSessionHandles()
    {
        var registry = new UiDisplayListResourceRegistry();
        var resource = new UiResourceId(Guid.NewGuid());
        var texture = new RenderTextureHandle(12, 3);
        var sampler = new RenderSamplerHandle(13, 4);

        registry.RegisterImage(resource, texture, sampler);

        Assert.True(registry.UnregisterImage(resource));
        Assert.False(registry.UnregisterImage(resource));
    }

    private static UiVisualDraw Solid(int seed, UiClipId? clip = null)
        => new(
            UiVisualKind.SolidRectangle,
            default,
            new float4(seed, seed, 10, 10),
            new float4(1, 1, 1, 1),
            clip ?? UiClipId.None,
            UiResourceId.Empty);

    private static FontInstanceId OpenTestFont(SixLaborsTextService service)
        => service.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("d7f3e9ab-6fb6-4d0f-9d8a-4d4fc2c4d6f6")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static readonly byte[] MinimalSpirv =
    [
        0x03, 0x02, 0x23, 0x07,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private sealed class RecordingSession : IRenderFrameSession, IDisposable
    {
        public RenderDeviceCapabilities Capabilities => default;

        public bool ProfilingEnabled => false;

        public IRenderProfiler? Profiler => null;

        public RenderTargetHandle Target => new(1, 1);

        public IRenderGraph CreateRenderGraph() => throw new NotSupportedException();

        public bool TryReinitializeAfterDeviceLoss() => false;

        public RenderBufferHandle CreateBuffer(in RenderBufferDescription description) => new(1, 1);

        public RenderTextureHandle CreateTexture(in RenderTextureDescription description) => new(1, 1);

        public RenderSamplerHandle CreateSampler(in RenderSamplerDescription description) => new(1, 1);

        public void Release(RenderBufferHandle buffer)
        {
        }

        public void Release(RenderTextureHandle texture)
        {
        }

        public void Release(RenderSamplerHandle sampler)
        {
        }

        public void ResizeTarget(in PixelExtent extent)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingGraphBuilder : IRenderGraphBuilder
    {
        private uint _nextHandle = 1;

        public List<IRasterPass> RasterPasses { get; } = [];

        public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target) => new(_nextHandle++);

        public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture) => new(_nextHandle++);

        public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer) => new(_nextHandle++);

        public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description) => new(_nextHandle++);

        public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description) => new(_nextHandle++);

        public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
        {
            RasterPasses.Add(pass);
            return new RenderGraphPassHandle(_nextHandle++);
        }

        public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
            => new(_nextHandle++);

        public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
            => new(_nextHandle++);

        public void UseColorAttachment(RenderGraphPassHandle pass, uint index, in ColorAttachmentDescription attachment)
        {
        }

        public void UseDepthStencilAttachment(RenderGraphPassHandle pass, in DepthStencilAttachmentDescription attachment)
        {
        }

        public void UseTexture(RenderGraphPassHandle pass, RenderGraphTextureHandle texture, RenderResourceAccess access, RenderPipelineStages stages)
        {
        }

        public void UseBuffer(RenderGraphPassHandle pass, RenderGraphBufferHandle buffer, RenderResourceAccess access, RenderPipelineStages stages)
        {
        }

        public RenderGraphReadbackHandle ReadbackBuffer(RenderGraphBufferHandle buffer, in BufferRange range)
            => new(_nextHandle++);

        public RenderGraphReadbackHandle ReadbackTexture(RenderGraphTextureHandle texture, in PixelRect region)
            => new(_nextHandle++);

        public void RecordRaster(RecordingRasterCommands commands)
        {
            foreach (var pass in RasterPasses)
            {
                pass.Record(commands);
            }
        }
    }

    private sealed class RecordingRasterCommands : IRasterCommandContext
    {
        public List<PixelRect> Scissors { get; } = [];

        public List<byte[]> PushedConstants { get; } = [];

        public int DrawCount { get; private set; }

        public List<uint> InstanceCounts { get; } = [];

        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0)
        {
        }

        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
        {
        }

        public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => PushedConstants.Add(data.ToArray());

        public void SetViewport(in RenderViewport viewport)
        {
        }

        public void SetScissor(in PixelRect scissor) => Scissors.Add(scissor);

        public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0)
        {
        }

        public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0)
        {
        }

        public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
        {
            DrawCount++;
            InstanceCounts.Add(instanceCount);
        }

        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        {
        }
    }
}
