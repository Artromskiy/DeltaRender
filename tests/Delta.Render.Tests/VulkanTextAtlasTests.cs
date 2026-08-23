using Delta.Render.Core;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanTextAtlasTests
{
    [Fact]
    public async Task AtlasUploadReusesAndGrowsStagingAndRejectsInvalidLifetimeHandles()
    {
        var fixture = AtlasFixture.Load();
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var page = device.CreateAtlasPage(fixture.Description);
        var pixels = fixture.Pixels;

        Assert.True(device.UploadAtlasPage(page, pixels, fixture.Description.Width));
        var first = device.AtlasUploadStatistics;
        Assert.Equal(1, first.StagingAllocationCount);
        Assert.Equal(1, first.SubmissionCount);
        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(1, 1, 8, 8, fixture.Description.Width, pixels),
            new TextAtlasDirtyRange(32, 32, 8, 8, fixture.Description.Width, pixels)
        }));
        var reused = device.AtlasUploadStatistics;
        Assert.Equal(first.StagingAllocationCount, reused.StagingAllocationCount);
        Assert.Equal(2, reused.SubmissionCount);

        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, fixture.Description.Width, fixture.Description.Height, fixture.Description.Width, pixels)
        }));
        var bigger = new TextAtlasPageDescription(new TextAtlasPageId(9), 512, 512, TextAtlasFormat.R8Unorm);
        await using var biggerPage = device.CreateAtlasPage(bigger);
        var biggerPixels = new byte[checked((int)bigger.RequiredBytes)];
        Assert.True(device.UploadAtlasPage(biggerPage, biggerPixels, bigger.Width));
        Assert.True(device.AtlasUploadStatistics.StagingAllocationCount > reused.StagingAllocationCount);

        await page.DisposeAsync();
        Assert.True(page.IsDisposed);
        Assert.False(device.UploadAtlasPage(page, pixels, fixture.Description.Width));
        Assert.False(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, 1, 1, 1, new byte[] { 1 })
        }));
    }

    [Fact]
    public async Task AtlasUploadValidatesForeignPageRangesAndNoopBatches()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var otherDevice = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var page = device.CreateAtlasPage(new TextAtlasPageDescription(new TextAtlasPageId(2), 4, 4, TextAtlasFormat.Rgba8Unorm));
        await using var foreign = otherDevice.CreateAtlasPage(new TextAtlasPageDescription(new TextAtlasPageId(3), 4, 4, TextAtlasFormat.Rgba8Unorm));

        Assert.True(device.UploadAtlasDirtyRanges(page, ReadOnlySpan<TextAtlasDirtyRange>.Empty));
        Assert.False(device.UploadAtlasPage(foreign, new byte[64], 16));
        Assert.False(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(3, 3, 2, 2, 8, new byte[32])
        }));
        Assert.False(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, 2, 2, 4, new byte[15])
        }));
    }

    [Fact]
    public async Task AtlasFixturePageIsUsableForDirtyUploadAndViewerContract()
    {
        var fixture = AtlasFixture.Load();
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var page = device.CreateAtlasPage(fixture.Description);

        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, fixture.Description.Width, fixture.Description.Height, fixture.Description.Width, fixture.Pixels)
        }));
        Assert.True(page.Description.IsValid);
        Assert.Equal(fixture.Description, page.Description);
        Assert.False(page.IsDisposed);
    }
}
