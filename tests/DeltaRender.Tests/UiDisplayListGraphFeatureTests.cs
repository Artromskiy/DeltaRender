using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.XAML;
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
}
