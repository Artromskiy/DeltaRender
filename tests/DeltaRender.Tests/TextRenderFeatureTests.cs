using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Shader.Contract;
using Delta.Text;
using Delta.Text.Contract;
using Xunit;
using Xunit.Abstractions;

namespace Delta.Render.Tests;

public sealed class TextRenderFeatureTests
{
    private readonly ITestOutputHelper _output;

    public TextRenderFeatureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CompatibleTextVariantUsesGeneratedPackerPath()
    {
        using var textService = new DeltaTextService();
        var program = SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var feature = new TextRenderFeature(
            new FakeSession(),
            textService,
            program,
            new PixelExtent(100, 80));

        var variant = new TextShaderVariant(program, GlyphImageMode.Sdf);

        Assert.True(feature.TryResolveTextVariant(variant, out var pipeline));
        Assert.Same(program, pipeline.ShaderProgram);
        Assert.Equal(RenderBlendMode.PremultipliedAlpha, feature.CompositePipeline.BlendMode);
        Assert.Equal(
            RenderBlendState.FromMode(RenderBlendMode.PremultipliedAlpha),
            pipeline.BlendState);
    }

    [Fact]
    public void InvalidTextVariantIsRejectedWithoutFallback()
    {
        using var textService = new DeltaTextService();
        var program = SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var feature = new TextRenderFeature(
            new FakeSession(),
            textService,
            program,
            new PixelExtent(100, 80));

        var variant = new TextShaderVariant(null!, GlyphImageMode.Sdf);

        Assert.False(feature.TryResolveTextVariant(variant, out _));
    }

    [Fact]
    public void FiveThousandAdjacentGlyphsUseOneInstancedDraw()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest(new string('A', 5000).AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(4096, 4096),
            atlasWidth: 2048,
            atlasHeight: 2048);

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 4096, 4096));
        var graph = new RecordingGraphBuilder();
        var stopwatch = Stopwatch.StartNew();
        feature.AddPasses(graph, 1);
        var commands = graph.RecordRaster();
        stopwatch.Stop();

        var glyphCount = 0;
        foreach (var run in shaped.Runs.Span)
        {
            glyphCount += run.Glyphs.Length;
        }

        Assert.Equal(5000, glyphCount);
        Assert.Single(commands.Draws);
        Assert.Equal(6u, commands.Draws[0].VertexCount);
        Assert.Equal((uint)glyphCount, commands.Draws[0].InstanceCount);
        Assert.Equal(0u, commands.Draws[0].FirstInstance);
        _output.WriteLine(
            $"Contiguous text benchmark: glyphs={glyphCount}, naiveDraws={glyphCount}, " +
            $"instancedDraws={commands.Draws.Count}, buildAndRecord={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [Fact]
    public void GeneratedTextParametersAreSubmittedAndResizeUpdatesResolution()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128,
            distanceRange: 6f);

        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 800, 600));
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        var firstCommands = firstGraph.RecordRaster();

        Assert.Equal(1, firstCommands.PushConstantCallCount);
        Assert.Equal(128, firstCommands.LastPushConstants.Length);
        Assert.Equal(800f, ReadFloat(firstCommands.LastPushConstants, 0));
        Assert.Equal(600f, ReadFloat(firstCommands.LastPushConstants, 4));
        Assert.Equal(1f, ReadFloat(firstCommands.LastPushConstants, 16));
        Assert.Equal(1f, ReadFloat(firstCommands.LastPushConstants, 28));
        Assert.Equal(0f, ReadFloat(firstCommands.LastPushConstants, 48));
        Assert.Equal(6f, ReadFloat(firstCommands.LastPushConstants, 52));

        var resourceCreations = session.ResourceCreationCount;
        feature.Clear();
        feature.Resize(new PixelExtent(400, 300));
        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 400, 300));
        var secondGraph = new RecordingGraphBuilder();
        feature.AddPasses(secondGraph, 2);
        var secondCommands = secondGraph.RecordRaster();

        Assert.Equal(resourceCreations, session.ResourceCreationCount);
        Assert.Equal(400f, ReadFloat(secondCommands.LastPushConstants, 0));
        Assert.Equal(300f, ReadFloat(secondCommands.LastPushConstants, 4));
    }

    [Fact]
    public void OuterShadowOffsetUsesOrderedLayersWithoutPromotingDistanceRange()
    {
        const GlyphImageMode mode = GlyphImageMode.Sdf;
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("AB".AsMemory(), 32, new[] { font }));
        var standardProgram = SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var shadowProgram = SdfTextOuterShadowGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            standardProgram,
            new PixelExtent(800, 500),
            mode,
            atlasWidth: 256,
            atlasHeight: 256);
        var effects = new TextEffectValues(
            Vector4.Zero,
            0,
            Vector4.Zero,
            0,
            0,
            new Vector4(0, 0, 0, 0.5f),
            new Vector2(0, 100),
            0,
            1.5f,
            0,
            1);

        feature.QueueCompositeRun(
            shaped,
            100,
            100,
            Vector4.One,
            new PixelRect(0, 0, 800, 500),
            mergeWithPrevious: true,
            effectValues: effects,
            shadowShaderVariant: new TextShaderVariant(shadowProgram, mode, TextShaderPath.OuterShadow));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var layers = graph.RecordRasters();
        var upload = graph.RecordTransfer();

        Assert.Equal(2, layers.Length);
        var shadowDistanceRangeOffset = checked((int)SdfTextOuterShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Layout.Members[1].Offset);
        var baseDistanceRangeOffset = checked((int)SdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Layout.Members[4].Offset);
        Assert.Equal(4f, ReadFloat(layers[0].LastPushConstants, shadowDistanceRangeOffset));
        Assert.Equal(4f, ReadFloat(layers[1].LastPushConstants, baseDistanceRangeOffset));
        Assert.Single(layers[0].Draws);
        Assert.Single(layers[1].Draws);
        Assert.Equal(layers[0].Draws[0], layers[1].Draws[0]);
        Assert.Equal(layers[0].TextureBindings, layers[1].TextureBindings);
        var bytes = upload.LastBufferUpload;
        Assert.Equal(96, bytes.Length);
        var run = shaped.Runs.Span[0];
        var glyph = run.Glyphs.Span[0];
        var defaultImage = textService.GenerateGlyphImage(new GlyphImageRequest(
            run.Font,
            glyph.GlyphId,
            run.PixelsPerEm,
            mode,
            4f,
            null));
        Assert.Equal(100f + glyph.OffsetX + defaultImage.PlaneBounds.Left, ReadFloat(bytes, 0));
        Assert.Equal(100f + glyph.OffsetY + defaultImage.PlaneBounds.Top, ReadFloat(bytes, 4));
        Assert.Equal(100f + glyph.OffsetX + defaultImage.PlaneBounds.Right, ReadFloat(bytes, 8));
        Assert.Equal(100f + glyph.OffsetY + defaultImage.PlaneBounds.Bottom, ReadFloat(bytes, 12));

        var firstUvMaxX = ReadFloat(bytes, 24);
        var secondUvMinX = ReadFloat(bytes, 48 + 16);
        Assert.True((secondUvMinX - firstUvMaxX) * 256f >= 1f);
    }

    [Fact]
    public void AdjacentShadowRunsRecordShadowThenBaseWithoutSharingInstances()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        var standardProgram = SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var shadowVariant = new TextShaderVariant(
            SdfTextOuterShadowGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            GlyphImageMode.Sdf,
            TextShaderPath.OuterShadow);
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            standardProgram,
            new PixelExtent(800, 500),
            atlasWidth: 128,
            atlasHeight: 128);
        var effects = new TextEffectValues(
            Vector4.Zero,
            0,
            Vector4.Zero,
            0,
            0,
            Vector4.One,
            new Vector2(0, 8),
            0,
            1,
            0,
            1);
        var clip = new PixelRect(0, 0, 800, 500);

        feature.QueueCompositeRun(shaped, 20, 20, Vector4.One, clip, true, effectValues: effects, shadowShaderVariant: shadowVariant);
        feature.QueueCompositeRun(shaped, 80, 20, Vector4.One, clip, true, effectValues: effects, shadowShaderVariant: shadowVariant);
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var layers = graph.RecordRasters();

        Assert.Equal(4, layers.Length);
        Assert.Equal(0u, Assert.Single(layers[0].Draws).FirstInstance);
        Assert.Equal(0u, Assert.Single(layers[1].Draws).FirstInstance);
        Assert.Equal(1u, Assert.Single(layers[2].Draws).FirstInstance);
        Assert.Equal(1u, Assert.Single(layers[3].Draws).FirstInstance);
    }

    [Fact]
    public void AnalyticTextEffectWidthBeyondAutomaticRangeFailsDeterministically()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        var standardProgram = SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        var shadowProgram = SdfTextOuterShadowGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv);
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            standardProgram,
            new PixelExtent(800, 500));
        var effects = new TextEffectValues(
            Vector4.Zero,
            0,
            Vector4.Zero,
            0,
            0,
            Vector4.One,
            Vector2.Zero,
            40,
            1,
            0,
            1);
        feature.QueueCompositeRun(
            shaped,
            0,
            0,
            Vector4.One,
            new PixelRect(0, 0, 800, 500),
            mergeWithPrevious: true,
            effectValues: effects,
            shadowShaderVariant: new TextShaderVariant(shadowProgram, GlyphImageMode.Sdf, TextShaderPath.OuterShadow));

        var error = Assert.Throws<InvalidOperationException>(() => feature.AddPasses(new RecordingGraphBuilder(), 1));
        Assert.Contains("maximum automatic distance-field tier 32", error.Message, StringComparison.Ordinal);
        Assert.Contains("CachedMask", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceUploadSkipsUnchangedPayloadAndResizeButKeepsChangesDirty()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);

        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 800, 600));
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        var firstUpload = firstGraph.RecordTransfer();
        Assert.Equal(1, firstUpload.UploadBufferCount);
        Assert.Equal(1, firstUpload.UploadTextureCount);

        feature.Clear();
        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 800, 600));
        var cacheHitGraph = new RecordingGraphBuilder();
        feature.AddPasses(cacheHitGraph, 2);
        var cacheHitUpload = cacheHitGraph.RecordTransfer();
        Assert.Equal(0, cacheHitUpload.UploadBufferCount);
        Assert.Equal(0, cacheHitUpload.UploadTextureCount);

        feature.Clear();
        feature.Resize(new PixelExtent(400, 300));
        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 400, 300));
        var resizeGraph = new RecordingGraphBuilder();
        feature.AddPasses(resizeGraph, 3);
        var resizeUpload = resizeGraph.RecordTransfer();
        Assert.Equal(0, resizeUpload.UploadBufferCount);
        Assert.Equal(0, resizeUpload.UploadTextureCount);

        feature.Clear();
        feature.AddRun(shaped, 11, 20, Vector4.One, new PixelRect(0, 0, 400, 300));
        var changedGraph = new RecordingGraphBuilder();
        feature.AddPasses(changedGraph, 4);
        var changedUpload = changedGraph.RecordTransfer();
        Assert.Equal(1, changedUpload.UploadBufferCount);
        Assert.Equal(0, changedUpload.UploadTextureCount);
    }

    [Fact]
    public void ProducerIdentityAndVersionReuseOnlyMatchingRunPayload()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var firstText = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        var changedText = textService.Shape(new TextShapeRequest("B".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);
        var clip = new PixelRect(0, 0, 800, 600);

        feature.QueueCompositeRun(firstText, 10, 20, Vector4.One, clip, true, 12, 3, 7);
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        Assert.Equal(1, firstGraph.RecordTransfer().UploadBufferCount);

        feature.Clear();
        feature.QueueCompositeRun(firstText, 10, 20, Vector4.One, clip, true, 12, 3, 7);
        var unchangedGraph = new RecordingGraphBuilder();
        feature.AddPasses(unchangedGraph, 2);
        Assert.Equal(0, unchangedGraph.RecordTransfer().UploadBufferCount);

        feature.Clear();
        feature.QueueCompositeRun(changedText, 10, 20, Vector4.One, clip, true, 12, 3, 8);
        var changedVersionGraph = new RecordingGraphBuilder();
        feature.AddPasses(changedVersionGraph, 3);
        Assert.Equal(1, changedVersionGraph.RecordTransfer().UploadBufferCount);
    }

    [Fact]
    public void InstanceBufferGrowthPreservesPreviouslyPackedRuns()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var firstText = textService.Shape(new TextShapeRequest(new string('A', 200).AsMemory(), 32, new[] { font }));
        var secondText = textService.Shape(new TextShapeRequest(new string('B', 100).AsMemory(), 32, new[] { font }));
        var firstRun = firstText.Runs.Span[0];
        var firstGlyph = firstRun.Glyphs.Span[0];
        var firstImage = textService.GenerateGlyphImage(new GlyphImageRequest(
            firstRun.Font,
            firstGlyph.GlyphId,
            firstRun.PixelsPerEm,
            GlyphImageMode.Sdf,
            4f,
            null));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);

        var clip = new PixelRect(0, 0, 800, 600);
        feature.QueueCompositeRun(firstText, 10, 20, Vector4.One, clip, false, 21, 1, 1);
        feature.QueueCompositeRun(secondText, 10, 20, Vector4.One, clip, true, 22, 1, 1);
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var upload = graph.RecordTransfer();

        Assert.Equal(300 * 48, upload.LastBufferUpload.Length);
        Assert.Equal(10f + firstGlyph.OffsetX + firstImage.PlaneBounds.Left, ReadFloat(upload.LastBufferUpload, 0));
        Assert.Equal(20f + firstGlyph.OffsetY + firstImage.PlaneBounds.Top, ReadFloat(upload.LastBufferUpload, 4));
    }

    [Fact]
    public void EffectiveClipIsIntersectedWithViewportBeforeScissor()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(100, 80),
            atlasWidth: 128,
            atlasHeight: 128);

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(-10, -5, 40, 35));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var raster = graph.RecordRaster();

        Assert.Single(raster.Scissors);
        Assert.Equal(new PixelRect(0, 0, 30, 30), raster.Scissors[0]);
    }

    [Fact]
    public void CompatibleRunsBatchButNonAdjacentClipChangesPreserveAbaOrder()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var first = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        var second = textService.Shape(new TextShapeRequest("B".AsMemory(), 32, new[] { font }));
        var third = textService.Shape(new TextShapeRequest("C".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);
        var clipA = new PixelRect(0, 0, 800, 600);
        var clipB = new PixelRect(20, 20, 400, 300);

        feature.AddRun(first, 0, 0, Vector4.One, clipA);
        feature.AddRun(second, 0, 0, Vector4.One, clipA);
        feature.AddRun(third, 0, 0, Vector4.One, clipB);
        feature.AddRun(first, 0, 0, Vector4.One, clipA);
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var raster = graph.RecordRaster();

        Assert.Equal(3, raster.Draws.Count);
        Assert.Equal(6u, raster.Draws[0].VertexCount);
        Assert.Equal(2u, raster.Draws[0].InstanceCount);
        Assert.Equal(0u, raster.Draws[0].FirstInstance);
        Assert.Equal(1u, raster.Draws[1].InstanceCount);
        Assert.Equal(2u, raster.Draws[1].FirstInstance);
        Assert.Equal(1u, raster.Draws[2].InstanceCount);
        Assert.Equal(3u, raster.Draws[2].FirstInstance);
        Assert.Single(raster.TextureBindings);
        Assert.Equal(clipA, raster.Scissors[0]);
        Assert.Equal(clipB, raster.Scissors[1]);
        Assert.Equal(clipA, raster.Scissors[2]);
    }

    [Fact]
    public void ClearEndsBorrowedRunLifetimeBeforeTheNextBuild()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);
        var resourceCreations = session.ResourceCreationCount;

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        feature.Clear();
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Empty(graph.RecordRaster().Draws);
        Assert.Equal(0, graph.RecordTransfer().UploadBufferCount);
        Assert.Equal(resourceCreations, session.ResourceCreationCount);
    }

    [Fact]
    public void WarmFeatureSubmissionPathDoesNotAllocateAfterWarmup()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);
        var graph = new RecordingGraphBuilder();
        var commands = new RecordingRasterCommands();
        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        Assert.True(feature.PrepareComposite(graph));
        graph.RecordTransfer();
        feature.RecordCompositeRuns(commands, 0, 1);
        feature.Clear();
        graph.Reset();
        commands.Reset();

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        Assert.True(feature.PrepareComposite(graph));
        graph.RecordTransfer();
        feature.RecordCompositeRuns(commands, 0, 1);
        feature.Clear();
        graph.Reset();
        commands.Reset();

        var before = GC.GetAllocatedBytesForCurrentThread();
        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        var afterAddRun = GC.GetAllocatedBytesForCurrentThread();
        var prepared = feature.PrepareComposite(graph);
        var afterAddPasses = GC.GetAllocatedBytesForCurrentThread();
        var transferPassCount = graph.TransferPassCount;
        feature.RecordCompositeRuns(commands, 0, 1);
        var afterRecord = GC.GetAllocatedBytesForCurrentThread();
        feature.Clear();
        var afterClear = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(prepared);
        Assert.Equal(0, transferPassCount);
        Assert.True(
            afterClear == before,
            $"AddRun={afterAddRun - before}, AddPasses={afterAddPasses - afterAddRun}, " +
            $"RecordComposite={afterRecord - afterAddPasses}, Clear={afterClear - afterRecord}");
    }

    [Fact]
    public void TextModesUseIsolatedAtlasFormats()
    {
        using var textService = new DeltaTextService();
        using var sdfSession = new FakeSession();
        using var sdfFeature = new TextRenderFeature(
            sdfSession,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(64, 64),
            mode: GlyphImageMode.Sdf,
            atlasWidth: 64,
            atlasHeight: 64);

        using var msdfSession = new FakeSession();
        using var msdfFeature = new TextRenderFeature(
            msdfSession,
            textService,
            MsdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(64, 64),
            mode: GlyphImageMode.Msdf,
            atlasWidth: 64,
            atlasHeight: 64);

        using var colorSession = new FakeSession();
        using var colorFeature = new TextRenderFeature(
            colorSession,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(64, 64),
            mode: GlyphImageMode.Color,
            atlasWidth: 64,
            atlasHeight: 64);

        Assert.Equal(RenderTextureFormat.R8Unorm, sdfSession.TextureFormats[0]);
        Assert.Equal(RenderTextureFormat.Rgba8Unorm, msdfSession.TextureFormats[0]);
        Assert.Equal(RenderTextureFormat.Rgba8Srgb, colorSession.TextureFormats[0]);
    }

    [Fact]
    public void SdfAtlasPaddingSeparatesAdjacentGlyphSlots()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("AB".AsMemory(), 32, new[] { font }));
        var run = shaped.Runs.Span[0];
        using var session = new FakeSession();
        using var atlas = new TextAtlas(
            session,
            textService,
            GlyphImageMode.Sdf,
            GlyphImageEncoding.SdfR8,
            RenderTextureFormat.R8Unorm,
            1,
            128,
            128,
            1,
            4f,
            null);

        var first = atlas.GetOrCreateGlyph(run, run.Glyphs.Span[0], 1);
        var second = atlas.GetOrCreateGlyph(run, run.Glyphs.Span[1], 1);

        Assert.Equal(first.PageIndex, second.PageIndex);
        var firstRight = (int)Maths.Round(first.UvRect.Z * atlas.Width);
        var secondLeft = (int)Maths.Round(second.UvRect.X * atlas.Width);
        Assert.Equal(1, secondLeft - firstRight);
    }

    [Fact]
    public void PackedInstanceUploadPreservesPlaneBoundsAndUvMetrics()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 32, new[] { font }));
        var run = shaped.Runs.Span[0];
        var glyph = run.Glyphs.Span[0];
        var image = textService.GenerateGlyphImage(new GlyphImageRequest(
            run.Font,
            glyph.GlyphId,
            run.PixelsPerEm,
            GlyphImageMode.Sdf,
            4f,
            null));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 128,
            atlasHeight: 128);

        feature.AddRun(shaped, 10, 20, Vector4.One, new PixelRect(0, 0, 800, 600));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var upload = graph.RecordTransfer();
        var bytes = upload.LastBufferUpload;

        Assert.Equal(48, bytes.Length);
        Assert.Equal(10f + glyph.OffsetX + image.PlaneBounds.Left, ReadFloat(bytes, 0));
        Assert.Equal(20f + glyph.OffsetY + image.PlaneBounds.Top, ReadFloat(bytes, 4));
        Assert.Equal(10f + glyph.OffsetX + image.PlaneBounds.Right, ReadFloat(bytes, 8));
        Assert.Equal(20f + glyph.OffsetY + image.PlaneBounds.Bottom, ReadFloat(bytes, 12));
        Assert.Equal(0f, ReadFloat(bytes, 16));
        Assert.Equal(0f, ReadFloat(bytes, 20));
        Assert.Equal(image.Width / 128f, ReadFloat(bytes, 24));
        Assert.Equal(image.Height / 128f, ReadFloat(bytes, 28));
    }

    [Fact]
    public void AtlasPagesUploadAndBindIndependentlyAndDisposeAllPages()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest(
            "ABCDEFGHIJKLMNO".AsMemory(),
            32,
            new[] { font }));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 64,
            atlasHeight: 64);

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var transfer = graph.RecordTransfer();
        var raster = graph.RecordRaster();

        Assert.True(session.TextureFormats.Count > 1);
        Assert.Equal(session.TextureFormats.Count, transfer.UploadTextureCount);
        Assert.Equal(transfer.UploadTextureCount, transfer.TextureUploads.Count);
        Assert.True(raster.TextureBindings.Count > 1);
        Assert.NotEqual(raster.TextureBindings[0], raster.TextureBindings[1]);

        feature.Dispose();
        Assert.Equal(session.TextureFormats.Count, session.ReleasedTextureCount);
    }

    [Fact]
    public void FailedPageAllocationLeavesExistingPagesOwnedAndUnchanged()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        var shaped = textService.Shape(new TextShapeRequest(
            "ABCDEFGHIJKLMNO".AsMemory(),
            32,
            new[] { font }));
        using var session = new FakeSession { FailTextureCreationAt = 2 };
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 64,
            atlasHeight: 64);

        feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
        var graph = new RecordingGraphBuilder();
        Assert.Throws<InvalidOperationException>(() => feature.AddPasses(graph, 1));

        Assert.True(session.TextureCreateAttempts > 1);
        Assert.Single(session.TextureFormats);
        feature.Dispose();
        Assert.Equal(1, session.ReleasedTextureCount);
    }

    [Fact]
    public void AtlasPagesRecycleOldestInactivePagesAcrossFrames()
    {
        using var textService = new DeltaTextService();
        var font = textService.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("6d34a56d-2b0d-4f39-bf55-1f51cf4ee1b7")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));
        using var session = new FakeSession();
        using var feature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(MinimalSpirv, MinimalSpirv),
            new PixelExtent(800, 600),
            atlasWidth: 64,
            atlasHeight: 64);

        var textGroups = new[]
        {
            "ABCDEFGHIJKLMNO",
            "PQRSTUVWXYZabcd",
            "efghijklmnopqrs",
            "tuvwxyz01234567",
            "89!\"#$%&'()*+,-",
            "./:;<=>?@[\\]^_`",
        };

        foreach (var text in textGroups)
        {
            var shaped = textService.Shape(new TextShapeRequest(text.AsMemory(), 32, new[] { font }));
            feature.AddRun(shaped, 0, 0, Vector4.One, new PixelRect(0, 0, 800, 600));
            var graph = new RecordingGraphBuilder();
            feature.AddPasses(graph, 1);
            Assert.True(graph.RecordTransfer().UploadTextureCount > 0);
            feature.Clear();
        }

        Assert.Equal(8, session.TextureFormats.Count);
        Assert.Equal(8, session.TextureCreateAttempts);
        Assert.Equal(0, session.ReleasedTextureCount);
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static readonly byte[] MinimalSpirv =
    [
        0x03, 0x02, 0x23, 0x07,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private sealed class FakeSession : IRenderFrameSession, IDisposable
    {
        private ulong _nextHandle = 1;

        public RenderDeviceCapabilities Capabilities => default;

        public bool ProfilingEnabled => false;

        public IRenderProfiler? Profiler => null;

        public RenderTargetHandle Target => new(1, 1);

        public int ResourceCreationCount { get; private set; }

        public int TextureCreateAttempts { get; private set; }

        public int? FailTextureCreationAt { get; set; }

        public int ReleasedTextureCount { get; private set; }

        public List<RenderTextureFormat> TextureFormats { get; } = [];

        public IRenderGraph CreateRenderGraph() => throw new NotSupportedException();

        public bool TryReinitializeAfterDeviceLoss() => false;

        public RenderBufferHandle CreateBuffer(in RenderBufferDescription description)
        {
            ResourceCreationCount++;
            return new RenderBufferHandle(_nextHandle++, 1);
        }

        public RenderTextureHandle CreateTexture(in RenderTextureDescription description)
        {
            TextureCreateAttempts++;
            if (FailTextureCreationAt == TextureCreateAttempts)
            {
                throw new InvalidOperationException("Injected texture creation failure.");
            }

            ResourceCreationCount++;
            TextureFormats.Add(description.Format);
            return new RenderTextureHandle(_nextHandle++, 1);
        }

        public RenderSamplerHandle CreateSampler(in RenderSamplerDescription description)
        {
            ResourceCreationCount++;
            return new RenderSamplerHandle(_nextHandle++, 1);
        }

        public void Release(RenderBufferHandle buffer)
        {
        }

        public void Release(RenderTextureHandle texture)
        {
            if (texture.IsValid)
            {
                ReleasedTextureCount++;
            }
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
        private IRasterPass? _rasterPass;
        private readonly List<IRasterPass> _rasterPasses = [];
        private ITransferPass? _transferPass;

        public int TransferPassCount { get; private set; }

        public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target) => new(_nextHandle++);

        public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture) => new(_nextHandle++);

        public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer) => new(_nextHandle++);

        public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description) => new(_nextHandle++);

        public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description) => new(_nextHandle++);

        public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
        {
            _rasterPass = pass;
            _rasterPasses.Add(pass);
            return new RenderGraphPassHandle(_nextHandle++);
        }

        public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
            => new(_nextHandle++);

        public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
        {
            _transferPass = pass;
            TransferPassCount++;
            return new RenderGraphPassHandle(_nextHandle++);
        }

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

        public RecordingRasterCommands RecordRaster()
        {
            var commands = new RecordingRasterCommands();
            _rasterPass?.Record(commands);
            return commands;
        }

        public void RecordRaster(RecordingRasterCommands commands) => _rasterPass?.Record(commands);

        public RecordingRasterCommands[] RecordRasters()
        {
            var commands = new RecordingRasterCommands[_rasterPasses.Count];
            for (var index = 0; index < commands.Length; index++)
            {
                commands[index] = new RecordingRasterCommands();
                _rasterPasses[index].Record(commands[index]);
            }

            return commands;
        }

        public RecordingTransferCommands RecordTransfer()
        {
            var commands = new RecordingTransferCommands();
            _transferPass?.Record(commands);
            return commands;
        }

        public void Reset()
        {
            _rasterPass = null;
            _rasterPasses.Clear();
            _transferPass = null;
            TransferPassCount = 0;
        }
    }

    private sealed class RecordingTransferCommands : ITransferCommandContext
    {
        public int UploadBufferCount { get; private set; }

        public int UploadTextureCount { get; private set; }

        public byte[] LastBufferUpload { get; private set; } = [];

        public List<RenderGraphTextureHandle> TextureUploads { get; } = [];

        public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0)
        {
        }

        public void CopyTexture(RenderGraphTextureHandle source, in PixelRect sourceRegion, RenderGraphTextureHandle destination, in PixelRect destinationRegion)
        {
        }

        public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
        {
            UploadBufferCount++;
            LastBufferUpload = data.ToArray();
        }

        public void UploadTexture(RenderGraphTextureHandle destination, in PixelRect destinationRegion, ReadOnlySpan<byte> data, uint sourceRowPitch)
        {
            UploadTextureCount++;
            TextureUploads.Add(destination);
        }
    }

    private sealed class RecordingRasterCommands : IRasterCommandContext
    {
        public List<RenderGraphTextureHandle> TextureBindings { get; } = [];

        public List<PixelRect> Scissors { get; } = [];

        public List<DrawCall> Draws { get; } = [];

        public int PushConstantCallCount { get; private set; }

        private readonly byte[] _lastPushConstants = new byte[128];

        public byte[] LastPushConstants => _lastPushConstants;

        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0)
        {
        }

        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
        {
            TextureBindings.Add(texture);
        }

        public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0)
        {
            PushConstantCallCount++;
            data.CopyTo(_lastPushConstants);
        }

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
            => Draws.Add(new DrawCall(vertexCount, instanceCount, firstVertex, firstInstance));

        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        {
        }

        public void Reset()
        {
            TextureBindings.Clear();
            Scissors.Clear();
            Draws.Clear();
            PushConstantCallCount = 0;
            _lastPushConstants.AsSpan().Clear();
        }
    }

    private readonly record struct DrawCall(uint VertexCount, uint InstanceCount, uint FirstVertex, uint FirstInstance);
}
