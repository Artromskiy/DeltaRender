using Delta.Maths;
using Delta.Render;
using Delta.Render.XAML;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class XamlDisplayListAdapterTests
{
    [Fact]
    public void NestedClipsBecomeRendererClipHierarchyAndEffectiveRectangleClip()
    {
        var clips = new[]
        {
            new UiClip(new float4(0, 0, 100, 100), UiClipId.None),
            new UiClip(new float4(10, 20, 40, 50), new UiClipId(0))
        };
        var visuals = new[]
        {
            new UiVisualCommand(
                UiVisualKind.SolidRectangle,
                default,
                new float4(12, 24, 20, 20),
                new float4(1, 0.5f, 0.25f, 1),
                new UiClipId(1),
                UiResourceId.Empty)
        };
        var displayList = new UiDisplayList(visuals, clips, ReadOnlySpan<UiTextDraw>.Empty);

        using var adapter = new UiDisplayListBatchAdapter();
        Assert.True(adapter.TryReplace(
            in displayList,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            default,
            out var token));

        var batch = adapter.Borrow(in token);
        Assert.Equal(1, batch.Rectangles.Length);
        Assert.Equal(new UiClipRect(10, 20, 40, 50), batch.Rectangles[0].Clip);
        Assert.Equal(new UiRenderClipId(1), batch.Clips[0].Id);
        Assert.Equal(new UiRenderClipId(2), batch.Clips[1].Id);
        Assert.Equal(new UiRenderClipId(1), batch.Clips[1].Parent);
    }

    [Fact]
    public void FailedReplacementLeavesPreviousBatchUntilSuccessfulReplacement()
    {
        var visuals = new[]
        {
            new UiVisualCommand(
                UiVisualKind.SolidRectangle,
                default,
                new float4(1, 2, 3, 4),
                new float4(1, 1, 1, 1),
                UiClipId.None,
                UiResourceId.Empty)
        };
        var displayList = new UiDisplayList(visuals, ReadOnlySpan<UiClip>.Empty, ReadOnlySpan<UiTextDraw>.Empty);
        using var adapter = new UiDisplayListBatchAdapter();
        Assert.True(adapter.TryReplace(
            in displayList,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            default,
            out var token));

        var unsupported = new[]
        {
            new UiVisualCommand(
                UiVisualKind.Image,
                default,
                new float4(1, 2, 3, 4),
                new float4(1, 1, 1, 1),
                UiClipId.None,
                new UiResourceId(Guid.NewGuid()))
        };
        var unsupportedList = new UiDisplayList(unsupported, ReadOnlySpan<UiClip>.Empty, ReadOnlySpan<UiTextDraw>.Empty);
        Assert.False(adapter.TryReplace(
            in unsupportedList,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            default,
            out _));
        Assert.Equal(1, adapter.Borrow(in token).Rectangles.Length);

        var replacement = new UiDisplayList(ReadOnlySpan<UiVisualCommand>.Empty, ReadOnlySpan<UiClip>.Empty, ReadOnlySpan<UiTextDraw>.Empty);
        Assert.True(adapter.TryReplace(
            in replacement,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            default,
            out _));
        Assert.Throws<InvalidOperationException>(() =>
        {
            var stale = adapter.Borrow(in token);
            _ = stale.IsEmpty;
        });
    }
}
