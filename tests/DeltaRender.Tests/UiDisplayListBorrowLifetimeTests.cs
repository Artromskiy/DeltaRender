using Delta.Render.XAML;
using Delta.Render.RenderGraph;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiDisplayListBorrowLifetimeTests
{
    [Fact]
    public void BorrowedFrameViewIsRejectedAfterFeatureDispose()
    {
        using var feature = new UiDisplayListGraphFeature(new PixelExtent(16, 16));

        Assert.True(feature.Consume(UiDisplayListTestFactory.Create([], [], [], [])), string.Join(" | ", feature.Diagnostics));
        Assert.Empty(feature.BorrowOrder().ToArray());

        feature.Dispose();

        Assert.Throws<ObjectDisposedException>(() => feature.BorrowOrder());
    }
}
