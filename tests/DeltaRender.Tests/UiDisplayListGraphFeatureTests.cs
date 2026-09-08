using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Delta;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Render.UIShaders;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Text;
using Delta.Text.Contract;
using Delta.XAML.Contract;
using Xunit;
using Xunit.Abstractions;
using XamlEffectParameters = Delta.XAML.Contract.UiEffectParameters;

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
            feature.Consume(UiDisplayListTestFactory.Create(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
            string.Join(" | ", feature.Diagnostics));
        Assert.Equal(order, feature.BorrowOrder().ToArray());
    }

    [Fact]
    public void DuplicatePayloadReferenceIsRejected()
    {
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
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

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
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
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("A".AsMemory(), 24, new[] { font }));
        var visuals = new[] { Solid(1, new UiClipId(0)) };
        var clips = new[] { new UiClipRegion(new float4(10, 12, 50, 30), UiClipId.None) };
        var texts = new[]
        {
            UiTextDraw.WithPaint(
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

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(visuals, clips, texts, order)), string.Join(" | ", feature.Diagnostics));
        var expectedVisual = visuals[0];
        var expectedClip = clips[0];
        var expectedText = texts[0];
        var expectedOrder = order[0];
        visuals[0] = Solid(9);
        clips[0] = new UiClipRegion(new float4(0, 0, 1, 1), UiClipId.None);
        texts[0] = UiTextDraw.WithPaint(
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
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var visuals = new[] { Solid(1), Solid(2) };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
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
    public void UnchangedWarmVisualPassSkipsInstanceUpload()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var displayList = UiDisplayListTestFactory.Create(
            new[] { Solid(1) },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) });

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));

        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        Assert.Equal(1, firstGraph.RecordTransfer().UploadBufferCount);

        var warmGraph = new RecordingGraphBuilder();
        feature.AddPasses(warmGraph, 2);
        Assert.Equal(0, warmGraph.RecordTransfer().UploadBufferCount);
        Assert.Single(warmGraph.RasterPasses);
    }

    [Fact]
    public void ChangedVisualUploadsOnlyItsExistingInstanceRange()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var firstVisual = Solid(1);
        var changedVisual = firstVisual with
        {
            Paint = UiVisualPaint.Solid(new float4(0.2f, 0.3f, 0.4f, 1))
        };
        var order = new[]
        {
            new UiDrawRef(UiDrawKind.Visual, 0),
            new UiDrawRef(UiDrawKind.Visual, 1),
        };

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { firstVisual, firstVisual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            order)), string.Join(" | ", feature.Diagnostics));
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        firstGraph.RecordTransfer();

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { firstVisual, changedVisual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            order)), string.Join(" | ", feature.Diagnostics));
        var changedGraph = new RecordingGraphBuilder();
        feature.AddPasses(changedGraph, 2);
        var upload = changedGraph.RecordTransfer();

        Assert.Equal(1, upload.UploadBufferCount);
        Assert.Equal(32, upload.UploadedByteCount);
        Assert.Single(upload.UploadOffsets);
        Assert.Equal(32UL, upload.UploadOffsets[0]);
    }

    [Fact]
    public void ZeroRadiusRoundedVisualUsesSolidProgramWhenSupplied()
    {
        var roundedProgram = RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var solidProgram = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            roundedProgram,
            new PixelExtent(800, 500),
            solidVisualProgram: solidProgram);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(100, 100, 600, 300),
            new UiVisualPaint(new float4(0.2f, 0.5f, 0.9f, 1), default, UiEffectSet.None),
            UiClipId.None,
            default);
        var order = new[] { new UiDrawRef(UiDrawKind.Visual, 0) };

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { visual },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                order)),
            string.Join(" | ", feature.Diagnostics));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 0);

        Assert.Single(graph.RasterPasses);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);
        Assert.Equal(1u, commands.InstanceCounts[0]);
    }

    [Fact]
    public void VisualSegmentUsesPremultipliedAlphaForCoverageOutput()
    {
        var program = RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(10, 10, 40, 30),
            new UiVisualPaint(
                new float4(0.2f, 0.5f, 0.9f, 1),
                new float4(8, 8, 8, 8),
                UiEffectSet.None)
            with
            { BlendMode = UiBlendMode.PremultipliedAlpha },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { visual },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                new[] { new UiDrawRef(UiDrawKind.Visual, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Single(graph.RasterDescriptions);
        Assert.Equal(
            RenderBlendState.FromMode(RenderBlendMode.PremultipliedAlpha),
            graph.RasterDescriptions[0].Pipeline.BlendState);
    }

    [Fact]
    public void VisualBlendModeMapsToRendererBlendState()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(10, 10, 40, 30),
            new UiVisualPaint(
                new float4(0.2f, 0.5f, 0.9f, 1),
                default,
                UiEffectSet.None)
            with
            { BlendMode = UiBlendMode.Multiply },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { visual },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                new[] { new UiDrawRef(UiDrawKind.Visual, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Single(graph.RasterDescriptions);
        Assert.Equal(
            RenderBlendState.FromMode(RenderBlendMode.Multiply),
            graph.RasterDescriptions[0].Pipeline.BlendState);
    }

    [Fact]
    public void ContiguousVisualsWithDifferentBlendModesUseSeparateSegments()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(100, 80));
        var alpha = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(0, 0, 40, 30),
            new UiVisualPaint(new float4(1, 0, 0, 1), default, UiEffectSet.None)
            with
            { BlendMode = UiBlendMode.Alpha },
            UiClipId.None,
            UiResourceId.Empty);
        var multiply = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(40, 0, 40, 30),
            new UiVisualPaint(new float4(0, 1, 0, 1), default, UiEffectSet.None)
            with
            { BlendMode = UiBlendMode.Multiply },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { alpha, multiply },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                new[]
                {
                    new UiDrawRef(UiDrawKind.Visual, 0),
                    new UiDrawRef(UiDrawKind.Visual, 1),
                })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Equal(2, graph.RasterDescriptions.Count);
        Assert.Equal(
            RenderBlendState.FromMode(RenderBlendMode.Alpha),
            graph.RasterDescriptions[0].Pipeline.BlendState);
        Assert.Equal(
            RenderBlendState.FromMode(RenderBlendMode.Multiply),
            graph.RasterDescriptions[1].Pipeline.BlendState);
    }

    [Fact]
    public void CompatibleVisualsWithDifferentClipsUseOnePassAndPreserveScissorCommands()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
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

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(visuals, clips, Array.Empty<UiTextDraw>(), order)), string.Join(" | ", feature.Diagnostics));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(graph.RasterPasses);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(feature.GetEffectiveClip(0), commands.Scissors[0]);
        Assert.Equal(feature.GetEffectiveClip(1), commands.Scissors[1]);
        Assert.Equal(2, commands.InstanceCounts.Count);
        Assert.Equal(1u, commands.InstanceCounts[0]);
        Assert.Equal(1u, commands.InstanceCounts[1]);
    }

    [Fact]
    public void VisualsWithDifferentClipsUseSeparateScissorDraws()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
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

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(visuals, clips, Array.Empty<UiTextDraw>(), order)), string.Join(" | ", feature.Diagnostics));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(graph.RasterPasses);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(2, commands.Scissors.Count);
        Assert.Equal(new PixelRect(0, 0, 40, 40), commands.Scissors[0]);
        Assert.Equal(new PixelRect(40, 0, 40, 40), commands.Scissors[1]);
        Assert.Equal(2, commands.InstanceCounts.Count);
        Assert.Equal(1u, commands.InstanceCounts[0]);
        Assert.Equal(1u, commands.InstanceCounts[1]);
    }

    [Fact]
    public void TextOnlyDisplayListAddsTextRasterPassWithoutVisualInstances()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);

        var text = UiTextDraw.WithPaint(
                shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)),
            UiClipId.None);
        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                Array.Empty<UiVisualDraw>(),
                Array.Empty<UiClipRegion>(),
                new[] { text },
                new[] { new UiDrawRef(UiDrawKind.Text, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.NotEmpty(graph.RasterPasses);
        Assert.Equal(1, graph.TransferPassCount);
        Assert.Equal(1, feature.BorrowOrder().Length);
        Assert.Equal(new UiDrawRef(UiDrawKind.Text, 0), feature.BorrowOrder()[0]);
    }

    [Fact]
    public void RegisteredCompatibleTextEffectSetIsAccepted()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        var textProgram = SdfTextStrokeGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var glowProgram = SdfTextOuterGlowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var standardTextProgram = SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            standardTextProgram,
            new PixelExtent(100, 80));
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Text,
            UiEffectCapabilities.Stroke | UiEffectCapabilities.OuterGlow,
            UiEffectQuality.Analytic,
            default);
        var effectResource = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                default,
                default,
                new UiEffectLayer(new float4(0.2f, 0.4f, 1, 1), default, 2, 3, 0, 0.8f),
                default,
                default));
        var registry = new UiDisplayListResourceRegistry();
        registry.RegisterTextEffectResourceGlowLayers(
            effectResource,
            new TextShaderVariant(glowProgram, GlyphImageMode.Sdf, TextShaderPath.OuterGlowOnly),
            new TextShaderVariant(textProgram, GlyphImageMode.Sdf, TextShaderPath.Stroke));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            registry: registry,
            textFeature: textFeature);
        var text = UiTextDraw.WithPaint(
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)) with { EffectSet = effectSet },
            UiClipId.None);

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[] { text },
            new[] { new UiDrawRef(UiDrawKind.Text, 0) })), string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Empty(feature.Diagnostics);
        Assert.NotEmpty(graph.RasterPasses);
    }

    [Fact]
    public void TextOuterGlowRecordsGlowBeforeBase()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Glow".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Text,
            UiEffectCapabilities.OuterGlow,
            UiEffectQuality.Analytic,
            default);
        var effectResource = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                default,
                default,
                default,
                new UiEffectLayer(new float4(0.2f, 0.4f, 1, 1), default, 0, 3, 0, 0.8f),
                default,
                default));
        var registry = new UiDisplayListResourceRegistry();
        registry.RegisterTextEffectResourceGlowLayers(
            effectResource,
            new TextShaderVariant(
                SdfTextOuterGlowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                GlyphImageMode.Sdf,
                TextShaderPath.OuterGlowOnly),
            new TextShaderVariant(
                SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                GlyphImageMode.Sdf,
                TextShaderPath.Standard));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            registry: registry,
            textFeature: textFeature);
        var text = UiTextDraw.WithPaint(
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)) with { EffectSet = effectSet },
            UiClipId.None);

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[] { text },
            new[] { new UiDrawRef(UiDrawKind.Text, 0) })), string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);

        Assert.Empty(feature.Diagnostics);
        Assert.Equal(2, graph.RasterDescriptions.Count);
        Assert.Contains("DeltaRender.XAML.Text.Glow", graph.RasterDescriptions[0].Name, StringComparison.Ordinal);
        Assert.Contains("DeltaRender.XAML.Text.Base", graph.RasterDescriptions[1].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTextEffectVariantIsRejectedDeterministically()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);
        var text = UiTextDraw.WithPaint(
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)) with
            {
                EffectSet = new UiEffectSet(
                    new UiResourceId(Guid.NewGuid()),
                    UiEffectTarget.Text,
                    UiEffectCapabilities.Stroke,
                    UiEffectQuality.Analytic,
                    default)
            },
            UiClipId.None);

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[] { text },
            new[] { new UiDrawRef(UiDrawKind.Text, 0) })));
        Assert.Contains(
            "references an unregistered text effect-set resource.",
            feature.Diagnostics.Single());
    }

    [Fact]
    public void TextCachedMaskIsRejectedWithStableDiagnostic()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);
        var text = UiTextDraw.WithPaint(
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)) with
            {
                EffectSet = new UiEffectSet(
                    new UiResourceId(Guid.NewGuid()),
                    UiEffectTarget.Text,
                    UiEffectCapabilities.Stroke,
                    UiEffectQuality.CachedMask,
                    default)
            },
            UiClipId.None);

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[] { text },
            new[] { new UiDrawRef(UiDrawKind.Text, 0) })));
        Assert.Equal(
            "Text at Order[0] requests unsupported CachedMask text rendering; use the generated stroke/outer-glow text artifact.",
            feature.Diagnostics.Single());
    }

    [Fact]
    public void MixedVisualAndTextUseOneTransferPass()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);
        var text = UiTextDraw.WithPaint(
            shaped,
            new float2(10, 20),
            UiTextPaint.Solid(new float4(1, 1, 1, 1)),
            UiClipId.None);
        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { Solid(1) },
            Array.Empty<UiClipRegion>(),
            new[] { text },
            new[]
            {
                new UiDrawRef(UiDrawKind.Visual, 0),
                new UiDrawRef(UiDrawKind.Text, 0),
            })), string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = graph.RecordTransfer();

        Assert.Equal(1, graph.TransferPassCount);
        Assert.Equal(2, commands.UploadBufferCount);
        Assert.True(commands.UploadTextureCount > 0);
    }

    [Fact]
    public void RepeatedTextOnlyConsumeDoesNotAccumulatePreviousFrameRuns()
    {
        using var textService = new DeltaTextService();
        var font = OpenTestFont(textService);
        var shaped = textService.Shape(new TextShapeRequest("Rewards".AsMemory(), 24, new[] { font }));
        using var session = new RecordingSession();
        using var textFeature = new TextRenderFeature(
            session,
            textService,
            SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80));
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            textFeature: textFeature);
        var displayList = UiDisplayListTestFactory.Create(
            Array.Empty<UiVisualDraw>(),
            Array.Empty<UiClipRegion>(),
            new[]
            {
                UiTextDraw.WithPaint(
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
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
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
            feature.Consume(UiDisplayListTestFactory.Create(visuals, Array.Empty<UiClipRegion>(), Array.Empty<UiTextDraw>(), order)),
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
    public void AdjacentVisualClipRunsUseSequentialInstancesAndOneBufferRange()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(4096, 4096));
        var visuals = new UiVisualDraw[29];
        var clips = new UiClipRegion[visuals.Length];
        var order = new UiDrawRef[visuals.Length];
        for (var index = 0; index < visuals.Length; index++)
        {
            visuals[index] = Solid(index, new UiClipId(index));
            clips[index] = new UiClipRegion(new float4(index, 0, 1, 1), UiClipId.None);
            order[index] = new UiDrawRef(UiDrawKind.Visual, index);
        }

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(visuals, clips, Array.Empty<UiTextDraw>(), order)),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(graph.RasterPasses);
        Assert.Equal(visuals.Length, commands.Draws.Count);
        Assert.Equal(visuals.Length, commands.Scissors.Count);
        Assert.Single(commands.BufferBindings);
        Assert.Equal(0UL, commands.BufferBindings[0].Offset);
        Assert.Equal(
            checked((ulong)visuals.Length * program.Vertex.Abi.Resources[0].Layout.ArrayStride),
            commands.BufferBindings[0].SizeInBytes);
        for (var index = 0; index < visuals.Length; index++)
        {
            Assert.Equal((uint)index, commands.Draws[index].FirstInstance);
        }
    }

    [Fact]
    public void PaintOnlyConsumeReusesRetainedStorageAndUpdatesValue()
    {
        var firstVisual = Solid(1);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(100, 80));
        var firstList = UiDisplayListTestFactory.Create(
            new[] { firstVisual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) });

        Assert.True(feature.Consume(firstList), string.Join(" | ", feature.Diagnostics));
        ref var retained = ref MemoryMarshal.GetReference(feature.BorrowVisuals());
        var updatedVisual = Solid(2);
        var secondList = UiDisplayListTestFactory.Create(
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
        var displayList = UiDisplayListTestFactory.Create(visuals, clips, texts, order);

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

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(visuals, clips, Array.Empty<UiTextDraw>(), order)));
        Assert.Equal(new PixelRect(20, 20, 70, 50), feature.GetEffectiveClip(0));
    }

    [Fact]
    public void UnsupportedRoundedPaintIsDiagnosedWithoutFallback()
    {
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(0, 0, 10, 10),
            new UiVisualPaint(new float4(1, 1, 1, 1), new float4(2, 2, 2, 2), UiEffectSet.None),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { visual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })));
        Assert.Contains(feature.Diagnostics, static message => message.Contains("rounded geometry", StringComparison.Ordinal));
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
                new float4(1, 2, 3, 4),
                UiEffectSet.None),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
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
                new float4(6, 6, 1, 1),
                UiEffectSet.None),
            UiClipId.None,
            UiResourceId.Empty);
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
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

        Assert.False(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { Solid(1, new UiClipId(0)) },
            clips,
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })));
        Assert.Contains(feature.Diagnostics, static message => message == "Clip 0 contains a parent cycle.");
    }

    [Fact]
    public void EmptyClipIsWarningAndOmittedFromRenderWork()
    {
        var clips = new[] { new UiClipRegion(new float4(10, 12, 0, 20), UiClipId.None) };
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(32, 32));

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { Solid(1, new UiClipId(0)) },
            clips,
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })), string.Join(" | ", feature.Diagnostics));
        Assert.Empty(feature.Diagnostics);
        Assert.Contains(feature.Warnings, static message =>
            message == "Clip 0 has empty bounds and was omitted from render work.");
        Assert.True(feature.GetEffectiveClip(0).IsEmpty);
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

    [Fact]
    public void ResourceRegistrySeparatesPreparedVisualAndTextEffectSets()
    {
        var registry = new UiDisplayListResourceRegistry();
        var visualEffect = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.Stroke,
            UiEffectQuality.Analytic,
            default);
        var textEffect = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Text,
            UiEffectCapabilities.Stroke,
            UiEffectQuality.Analytic,
            default);
        var visualProgram = RoundedStrokeGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var textProgram = SdfTextStrokeGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var outerShadowProgram = SdfTextOuterShadowGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);

        var visualVariant = new UiVisualShaderVariant(visualProgram, UiVisualKind.RoundedRectangle, UiVisualShaderPath.RoundedStrokeEffect);
        var visualResource = new UiEffectResource(
            visualEffect,
            new XamlEffectParameters(
                new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                default,
                default,
                default,
                default,
                default));
        registry.RegisterVisualEffectResource(visualResource, visualVariant);
        var standardError = Assert.Throws<ArgumentException>(() =>
            registry.RegisterVisualEffectResource(
                visualResource,
                new UiVisualShaderVariant(
                    RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                    UiVisualKind.RoundedRectangle)));
        Assert.Contains("generated effect UI artifact", standardError.Message, StringComparison.Ordinal);
        var textVariant = new TextShaderVariant(textProgram, GlyphImageMode.Sdf, TextShaderPath.Stroke);
        var textResource = new UiEffectResource(
            textEffect,
            new XamlEffectParameters(
                new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                default,
                default,
                default,
                default,
                default));
        registry.RegisterTextEffectResource(textResource, textVariant);
        var textInnerGlowSet = textEffect with
        {
            Resource = new UiResourceId(Guid.NewGuid()),
            Capabilities = UiEffectCapabilities.InnerGlow,
        };
        var textInnerGlowError = Assert.Throws<ArgumentException>(() =>
            registry.RegisterTextEffectResource(
                new UiEffectResource(
                    textInnerGlowSet,
                    new XamlEffectParameters(
                        default,
                        default,
                        default,
                        default,
                        new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 2, 0, 1),
                        default)),
                textVariant));
        Assert.Contains("InnerGlow", textInnerGlowError.Message, StringComparison.Ordinal);
        var outerTextSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Text,
            UiEffectCapabilities.OuterShadow,
            UiEffectQuality.Analytic,
            default);
        var outerTextResource = new UiEffectResource(
            outerTextSet,
            new XamlEffectParameters(
                default,
                new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 2, 0, 1),
                default,
                default,
                default,
                default));
        var outerShadowVariant = new TextShaderVariant(outerShadowProgram, GlyphImageMode.Sdf, TextShaderPath.OuterShadow);
        registry.RegisterTextEffectResource(outerTextResource, outerShadowVariant);
        Assert.True(registry.TryResolveTextEffectSet(outerTextSet, out var outerTextVariant, out var resolvedOuterTextResource));
        Assert.Equal(outerShadowVariant, outerTextVariant);
        Assert.Equal(outerTextResource, resolvedOuterTextResource);

        Assert.True(registry.TryResolveVisualEffectSet(visualEffect, out var resolvedVisual, out var resolvedVisualResource));
        Assert.Equal(visualVariant, resolvedVisual);
        Assert.Equal(visualResource, resolvedVisualResource);
        Assert.False(registry.TryResolveVisualEffectSet(
            visualEffect with { Quality = UiEffectQuality.CachedMask }, out _, out _));
        Assert.False(registry.TryResolveVisualEffectSet(textEffect, out _, out _));
        Assert.True(registry.TryResolveTextEffectSet(textEffect, out var resolvedText, out var resolvedTextResource));
        Assert.Equal(textVariant, resolvedText);
        Assert.Equal(textResource, resolvedTextResource);
        Assert.False(registry.TryResolveTextEffectSet(visualEffect, out _, out _));
        Assert.Throws<ArgumentException>(() => registry.RegisterVisualEffectResource(
            visualResource,
            new UiVisualShaderVariant(
                SdfTextGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.RoundedRectangle,
                UiVisualShaderPath.RoundedStrokeEffect)));
        Assert.True(registry.UnregisterVisualEffectSet(visualEffect.Resource));
        Assert.False(registry.UnregisterVisualEffectSet(visualEffect.Resource));
        Assert.True(registry.UnregisterTextEffectSet(textEffect.Resource));
        Assert.False(registry.UnregisterTextEffectSet(textEffect.Resource));
    }

    [Fact]
    public void UpdatingVisualEffectResourceRetainsPreparedVariant()
    {
        var registry = new UiDisplayListResourceRegistry();
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.Stroke,
            UiEffectQuality.Analytic,
            default);
        var variant = new UiVisualShaderVariant(
            RoundedStrokeGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            UiVisualKind.RoundedRectangle,
            UiVisualShaderPath.RoundedStrokeEffect);
        registry.RegisterVisualEffectResource(
            new UiEffectResource(
                effectSet,
                new XamlEffectParameters(
                    new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                    default,
                    default,
                    default,
                    default,
                    default)),
            variant);
        var updated = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                new UiEffectLayer(new float4(0, 1, 0, 1), default, 2, 0, 0, 1),
                default,
                default,
                default,
                default,
                default));

        registry.UpdateVisualEffectResource(updated);

        Assert.True(registry.TryResolveVisualEffectSet(effectSet, out var resolvedVariant, out var resolvedResource));
        Assert.Equal(variant, resolvedVariant);
        Assert.Equal(updated, resolvedResource);
        Assert.Throws<ArgumentException>(() => registry.UpdateVisualEffectResource(
            new UiEffectResource(
                effectSet with { Capabilities = UiEffectCapabilities.OuterGlow },
                updated.Parameters)));
    }

    [Fact]
    public void UpdatingVisualEffectResourceRepacksOnlyReferencingVisuals()
    {
        var registry = new UiDisplayListResourceRegistry();
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.OuterGlow,
            UiEffectQuality.Analytic,
            default);
        var effectProgram = RoundedOuterGlowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        var baseProgram = RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        registry.RegisterVisualEffectResourceGlowLayers(
            new UiEffectResource(
                effectSet,
                new XamlEffectParameters(
                    default,
                    default,
                    default,
                    new UiEffectLayer(new float4(0, 0, 1, 1), default, 0, 4, 0, 1),
                    default,
                    default)),
            new UiVisualShaderVariant(effectProgram, UiVisualKind.RoundedRectangle, UiVisualShaderPath.OuterGlowOnlyEffect),
            new UiVisualShaderVariant(baseProgram, UiVisualKind.RoundedRectangle, UiVisualShaderPath.Standard));

        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            registry: registry);
        var displayList = UiDisplayListTestFactory.Create(
            new[]
            {
                UiVisualDraw.WithPaint(
                    UiVisualKind.RoundedRectangle,
                    default,
                    new float4(5, 5, 20, 20),
                    UiVisualPaint.Solid(new float4(1, 1, 1, 1)) with { EffectSet = effectSet },
                    UiClipId.None,
                    UiResourceId.Empty),
                Solid(2),
            },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[]
            {
                new UiDrawRef(UiDrawKind.Visual, 0),
                new UiDrawRef(UiDrawKind.Visual, 1),
            });

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        var firstGraph = new RecordingGraphBuilder();
        feature.AddPasses(firstGraph, 1);
        var firstUpload = firstGraph.RecordTransfer();
        Assert.Equal(1, firstUpload.UploadBufferCount);

        registry.UpdateVisualEffectResource(
            new UiEffectResource(
                effectSet,
                new XamlEffectParameters(
                    default,
                    default,
                    default,
                    new UiEffectLayer(new float4(0, 1, 0, 1), default, 0, 8, 0, 2),
                    default,
                    default)));

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        var updatedGraph = new RecordingGraphBuilder();
        feature.AddPasses(updatedGraph, 2);
        var updatedUpload = updatedGraph.RecordTransfer();

        Assert.Equal(1, updatedUpload.UploadBufferCount);
        Assert.True(updatedUpload.UploadedByteCount < firstUpload.UploadedByteCount);
    }

    [Fact]
    public void VisualEffectRegistryAcceptsCachedMaskOnlyWithRegisteredMask()
    {
        var registry = new UiDisplayListResourceRegistry();
        var cachedSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.Stroke,
            UiEffectQuality.CachedMask,
            default);
        var maskResource = new UiResourceId(Guid.NewGuid());
        var cachedResource = new UiEffectResource(
            cachedSet,
            new XamlEffectParameters(
                new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                default,
                default,
                default,
                default,
                maskResource));
        var cachedError = Assert.Throws<ArgumentException>(() =>
            registry.RegisterVisualEffectResource(
                cachedResource,
                new UiVisualShaderVariant(
                    CachedMaskRoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                    UiVisualKind.RoundedRectangle,
                    UiVisualShaderPath.CachedMask)));
        Assert.Contains("registered mask resource", cachedError.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => registry.RegisterMask(
            maskResource,
            new RenderTextureHandle(20, 2),
            new RenderSamplerHandle(21, 2),
            new float4(0.75f, 0, 0.5f, 1)));
        registry.RegisterMask(
            maskResource,
            new RenderTextureHandle(20, 2),
            new RenderSamplerHandle(21, 2),
            new float4(0, 0, 1, 1));
        var cachedVariant = new UiVisualShaderVariant(
            CachedMaskRoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            UiVisualKind.RoundedRectangle,
            UiVisualShaderPath.CachedMask);
        registry.RegisterVisualEffectResource(cachedResource, cachedVariant);
        Assert.True(registry.TryResolveVisualEffectSet(cachedSet, out var resolvedCachedVariant, out var resolvedCachedResource));
        Assert.Equal(cachedVariant, resolvedCachedVariant);
        Assert.Equal(cachedResource, resolvedCachedResource);
        Assert.True(registry.UnregisterMask(maskResource));
    }

    [Fact]
    public void AdjacentCachedMaskVisualsShareOnePassAndMaskBinding()
    {
        var registry = new UiDisplayListResourceRegistry();
        var maskResource = new UiResourceId(Guid.NewGuid());
        var maskTexture = new RenderTextureHandle(20, 2);
        var maskSampler = new RenderSamplerHandle(21, 2);
        registry.RegisterMask(maskResource, maskTexture, maskSampler, new float4(0, 0, 1, 1));
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.Stroke,
            UiEffectQuality.CachedMask,
            default);
        registry.RegisterVisualEffectResource(
            new UiEffectResource(
                effectSet,
                new XamlEffectParameters(
                    new UiEffectLayer(new float4(1, 1, 1, 1), default, 1, 0, 0, 1),
                    default,
                    default,
                    default,
                    default,
                    maskResource)),
            new UiVisualShaderVariant(
                CachedMaskRoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.RoundedRectangle,
                UiVisualShaderPath.CachedMask));

        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            registry: registry);
        var paint = UiVisualPaint.Solid(new float4(0.2f, 0.4f, 0.8f, 1)) with { EffectSet = effectSet };
        var visuals = new[]
        {
            UiVisualDraw.WithPaint(UiVisualKind.RoundedRectangle, default, new float4(5, 5, 20, 20), paint, UiClipId.None, UiResourceId.Empty),
            UiVisualDraw.WithPaint(UiVisualKind.RoundedRectangle, default, new float4(30, 5, 20, 20), paint, UiClipId.None, UiResourceId.Empty),
        };

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            visuals,
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[]
            {
                new UiDrawRef(UiDrawKind.Visual, 0),
                new UiDrawRef(UiDrawKind.Visual, 1),
            })), string.Join(" | ", feature.Diagnostics));
        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(graph.RasterPasses);
        Assert.Single(commands.TextureBindings);
        Assert.Equal(UiVisualShaderContract.CachedMaskTextureBinding, commands.TextureBindings[0].Binding);
        Assert.True(commands.TextureBindings[0].Texture.IsValid);
        Assert.Equal(maskSampler, commands.TextureBindings[0].Sampler);
    }

    [Fact]
    public void VisualOuterShadowRecordsShadowBeforeBaseAndUsesSeparatePackedRanges()
    {
        var registry = new UiDisplayListResourceRegistry();
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.OuterShadow,
            UiEffectQuality.Analytic,
            default);
        var effectResource = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                default,
                new UiEffectLayer(
                    new float4(0, 0, 0, 0.5f),
                    new float2(100, 100),
                    0,
                    1.5f,
                    0,
                    1),
                default,
                default,
                default,
                default));
        registry.RegisterVisualEffectResourceLayers(
            effectResource,
            new UiVisualShaderVariant(
                RoundedOuterShadowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.RoundedRectangle,
                UiVisualShaderPath.OuterShadowOnlyEffect),
            new UiVisualShaderVariant(
                RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.RoundedRectangle,
                UiVisualShaderPath.Standard));

        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            RoundedRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(800, 500),
            registry: registry);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(100, 100, 600, 300),
            UiVisualPaint.Solid(new float4(0.2f, 0.5f, 0.9f, 1)) with
            {
                CornerRadii = new float4(48, 20, 72, 12),
                EffectSet = effectSet,
            },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { visual },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                new[] { new UiDrawRef(UiDrawKind.Visual, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Equal(2, graph.RasterDescriptions.Count);
        Assert.Contains(".Shadow[0:1)", graph.RasterDescriptions[0].Name, StringComparison.Ordinal);
        Assert.Contains(".Base[0:1)", graph.RasterDescriptions[1].Name, StringComparison.Ordinal);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(1u, commands.Draws[0].InstanceCount);
        Assert.Equal(1u, commands.Draws[1].InstanceCount);
        Assert.Equal(80UL, commands.BufferBindings[0].SizeInBytes);
        Assert.Equal(48UL, commands.BufferBindings[1].SizeInBytes);
        Assert.Equal(80UL, commands.BufferBindings[1].Offset);
    }

    [Fact]
    public void SolidVisualOuterGlowRecordsGlowBeforeBase()
    {
        var registry = new UiDisplayListResourceRegistry();
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.OuterGlow,
            UiEffectQuality.Analytic,
            default);
        var effectResource = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                default,
                default,
                default,
                new UiEffectLayer(new float4(0.2f, 0.4f, 1, 1), default, 0, 3, 0, 0.8f),
                default,
                default));
        registry.RegisterVisualEffectResourceGlowLayers(
            effectResource,
            new UiVisualShaderVariant(
                SolidOuterGlowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.SolidRectangle,
                UiVisualShaderPath.OuterGlowOnlyEffect),
            new UiVisualShaderVariant(
                SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.SolidRectangle,
                UiVisualShaderPath.Standard));

        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(100, 80),
            registry: registry);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(10, 10, 20, 20),
            UiVisualPaint.Solid(new float4(1, 1, 1, 1)) with { EffectSet = effectSet },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create(
            new[] { visual },
            Array.Empty<UiClipRegion>(),
            Array.Empty<UiTextDraw>(),
            new[] { new UiDrawRef(UiDrawKind.Visual, 0) })), string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Empty(feature.Diagnostics);
        Assert.Equal(2, graph.RasterDescriptions.Count);
        Assert.Contains(".Glow[0:1)", graph.RasterDescriptions[0].Name, StringComparison.Ordinal);
        Assert.Contains(".Base[0:1)", graph.RasterDescriptions[1].Name, StringComparison.Ordinal);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(1u, commands.InstanceCounts[0]);
        Assert.Equal(1u, commands.InstanceCounts[1]);
    }

    [Fact]
    public void SolidVisualOuterShadowUsesSolidShadowLayerAndBaseLayer()
    {
        var registry = new UiDisplayListResourceRegistry();
        var effectSet = new UiEffectSet(
            new UiResourceId(Guid.NewGuid()),
            UiEffectTarget.Visual,
            UiEffectCapabilities.OuterShadow,
            UiEffectQuality.Analytic,
            default);
        var effectResource = new UiEffectResource(
            effectSet,
            new XamlEffectParameters(
                default,
                new UiEffectLayer(
                    new float4(0, 0, 0, 0.5f),
                    new float2(12, 18),
                    0,
                    8,
                    4,
                    1),
                default,
                default,
                default,
                default));
        registry.RegisterVisualEffectResourceLayers(
            effectResource,
            new UiVisualShaderVariant(
                SolidOuterShadowOnlyGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.SolidRectangle,
                UiVisualShaderPath.OuterShadowOnlyEffect),
            new UiVisualShaderVariant(
                SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
                UiVisualKind.SolidRectangle,
                UiVisualShaderPath.Standard));

        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(
            session,
            SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv),
            new PixelExtent(800, 500),
            registry: registry);
        var visual = UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(100, 100, 600, 300),
            UiVisualPaint.Solid(new float4(0.2f, 0.5f, 0.9f, 1)) with { EffectSet = effectSet },
            UiClipId.None,
            UiResourceId.Empty);

        Assert.True(
            feature.Consume(UiDisplayListTestFactory.Create(
                new[] { visual },
                Array.Empty<UiClipRegion>(),
                Array.Empty<UiTextDraw>(),
                new[] { new UiDrawRef(UiDrawKind.Visual, 0) })),
            string.Join(" | ", feature.Diagnostics));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Equal(2, graph.RasterDescriptions.Count);
        Assert.Contains(".Shadow[0:1)", graph.RasterDescriptions[0].Name, StringComparison.Ordinal);
        Assert.Contains(".Base[0:1)", graph.RasterDescriptions[1].Name, StringComparison.Ordinal);
        Assert.Equal(2, commands.DrawCount);
        Assert.Equal(64UL, commands.BufferBindings[0].SizeInBytes);
        Assert.Equal(32UL, commands.BufferBindings[1].SizeInBytes);
        Assert.Equal(64UL, commands.BufferBindings[1].Offset);
    }

    [Fact]
    public void DpiScaleConvertsLogicalClipOnceAtRasterBoundary()
    {
        var program = SolidRectangleGraphicsShaderProgram.CreateProgram(_minimalSpirv, _minimalSpirv);
        using var session = new RecordingSession();
        using var feature = new UiDisplayListGraphFeature(session, program, new PixelExtent(200, 160));
        var clips = new[] { new UiClipRegion(new float4(12, 20, 30, 10), UiClipId.None) };
        var visuals = new[] { Solid(1, new UiClipId(0)) };
        var order = new[] { new UiDrawRef(UiDrawKind.Visual, 0) };
        var identities = new[] { new UiElementIdentity(1, 1, 1) };
        var displayList = new UiDisplayList(visuals, clips, Array.Empty<UiTextDraw>(), order, identities, 2f);

        Assert.True(feature.Consume(displayList), string.Join(" | ", feature.Diagnostics));
        Assert.Equal(new PixelRect(12, 20, 30, 10), feature.GetEffectiveClip(0));

        var graph = new RecordingGraphBuilder();
        feature.AddPasses(graph, 1);
        var commands = new RecordingRasterCommands();
        graph.RecordRaster(commands);

        Assert.Single(commands.Scissors);
        Assert.Equal(new PixelRect(24, 40, 60, 20), commands.Scissors[0]);
    }

    private static UiVisualDraw Solid(int seed, UiClipId? clip = null)
        => new(
            UiVisualKind.SolidRectangle,
            default,
            new float4(seed, seed, 10, 10),
            new float4(1, 1, 1, 1),
            clip ?? UiClipId.None,
            UiResourceId.Empty);

    private static FontInstanceId OpenTestFont(DeltaTextService service)
        => service.OpenFont(new FontOpenRequest(
            new FontSourceId(Guid.Parse("d7f3e9ab-6fb6-4d0f-9d8a-4d4fc2c4d6f6")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSans-Regular.ttf")),
            0));

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static readonly byte[] _minimalSpirv =
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
        private readonly List<ITransferPass> _transferPasses = [];

        public List<IRasterPass> RasterPasses { get; } = [];

        public List<RasterPassDescription> RasterDescriptions { get; } = [];

        public int TransferPassCount => _transferPasses.Count;

        public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target) => new(_nextHandle++);

        public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture) => new(_nextHandle++);

        public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer) => new(_nextHandle++);

        public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description) => new(_nextHandle++);

        public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description) => new(_nextHandle++);

        public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
        {
            RasterDescriptions.Add(description);
            RasterPasses.Add(pass);
            return new RenderGraphPassHandle(_nextHandle++);
        }

        public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
            => new(_nextHandle++);

        public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
        {
            _transferPasses.Add(pass);
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

        public void RecordRaster(RecordingRasterCommands commands)
        {
            foreach (var pass in RasterPasses)
            {
                pass.Record(commands);
            }
        }

        public RecordingTransferCommands RecordTransfer()
        {
            var commands = new RecordingTransferCommands();
            foreach (var pass in _transferPasses)
            {
                pass.Record(commands);
            }

            return commands;
        }
    }

    private sealed class RecordingTransferCommands : ITransferCommandContext
    {
        public int UploadBufferCount { get; private set; }

        public int UploadTextureCount { get; private set; }

        public int UploadedByteCount { get; private set; }

        public List<ulong> UploadOffsets { get; } = [];

        public void CopyBuffer(
            RenderGraphBufferHandle source,
            RenderGraphBufferHandle destination,
            ulong sizeInBytes,
            ulong sourceOffset = 0,
            ulong destinationOffset = 0)
        {
        }

        public void CopyTexture(
            RenderGraphTextureHandle source,
            in PixelRect sourceRegion,
            RenderGraphTextureHandle destination,
            in PixelRect destinationRegion)
        {
        }

        public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
        {
            UploadBufferCount++;
            UploadedByteCount += data.Length;
            UploadOffsets.Add(destinationOffset);
        }

        public void UploadTexture(
            RenderGraphTextureHandle destination,
            in PixelRect destinationRegion,
            ReadOnlySpan<byte> data,
            uint sourceRowPitch)
        {
            UploadTextureCount++;
        }
    }

    private sealed class RecordingRasterCommands : IRasterCommandContext
    {
        public readonly record struct BufferBinding(ulong Offset, ulong SizeInBytes);

        public readonly record struct DrawCall(uint InstanceCount, uint FirstInstance);

        public readonly record struct TextureBinding(
            ShaderBinding Binding,
            RenderGraphTextureHandle Texture,
            RenderSamplerHandle Sampler);

        public List<PixelRect> Scissors { get; } = [];

        public List<byte[]> PushedConstants { get; } = [];

        public int DrawCount { get; private set; }

        public List<uint> InstanceCounts { get; } = [];

        public List<BufferBinding> BufferBindings { get; } = [];

        public List<DrawCall> Draws { get; } = [];

        public List<TextureBinding> TextureBindings { get; } = [];

        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0)
        {
            BufferBindings.Add(new BufferBinding(offset, sizeInBytes));
        }

        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
        {
            TextureBindings.Add(new TextureBinding(binding, texture, sampler));
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
            Draws.Add(new DrawCall(instanceCount, firstInstance));
        }

        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        {
        }
    }
}
