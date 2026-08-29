using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
using Delta.Text;
using Delta.Text.Contract;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiDisplayListGraphFeatureTests
{
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
        var texts = new[] { new UiTextDraw(shaped, new float2(4, 5), new float4(0.2f, 0.3f, 0.4f, 1), new UiClipId(0)) };
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
        texts[0] = new UiTextDraw(shaped, default, default, UiClipId.None);
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
}
