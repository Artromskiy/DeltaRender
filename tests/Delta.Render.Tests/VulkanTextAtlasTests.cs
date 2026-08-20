using Delta.Render.Core;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanTextAtlasTests
{
    [Fact]
    public async Task Atlas_upload_reuses_and_grows_staging_and_rejects_invalid_lifetime_handles()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        var description = new TextAtlasPageDescription(new TextAtlasPageId(1), 16, 16, TextAtlasFormat.R8Unorm);
        await using var page = device.CreateAtlasPage(description);
        var pixels = new byte[256];

        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, 4, 4, 4, new byte[16])
        }));
        var first = device.AtlasUploadStatistics;
        Assert.Equal(1, first.StagingAllocationCount);
        Assert.Equal(1, first.SubmissionCount);
        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(1, 1, 2, 2, 2, new byte[] { 1, 2, 3, 4 }),
            new TextAtlasDirtyRange(4, 4, 2, 2, 2, new byte[] { 5, 6, 7, 8 })
        }));
        var reused = device.AtlasUploadStatistics;
        Assert.Equal(first.StagingAllocationCount, reused.StagingAllocationCount);
        Assert.Equal(2, reused.SubmissionCount);

        Assert.True(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, 16, 16, 16, pixels)
        }));
        Assert.True(device.AtlasUploadStatistics.StagingAllocationCount > reused.StagingAllocationCount);

        await page.DisposeAsync();
        Assert.True(page.IsDisposed);
        Assert.False(device.UploadAtlasPage(page, pixels, 16));
        Assert.False(device.UploadAtlasDirtyRanges(page, new[]
        {
            new TextAtlasDirtyRange(0, 0, 1, 1, 1, new byte[] { 1 })
        }));
    }

    [Fact]
    public async Task Atlas_upload_validates_foreign_page_ranges_and_noop_batches()
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
}
