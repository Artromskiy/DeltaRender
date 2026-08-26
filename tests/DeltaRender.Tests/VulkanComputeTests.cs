using System.Runtime.InteropServices;
using DeltaRender;
using DeltaRender.Vulkan;
using DeltaShader.Contract;
using Xunit;

namespace DeltaRender.Tests;

public sealed class VulkanComputeTests
{
    [Fact]
    public async Task Std430ComputeDispatchMatchesCpuOracleForRequiredSizes()
    {
        var artifact = CreateArtifact(await LoadShaderAsync("compute_double.spv"),
            new ShaderResourceBinding(new ShaderBinding(0, 0), ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, ScalarLayout()));

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);

        var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
        Assert.Equal(ComputeDispatchStatus.NoOp, noOp.Status);

        foreach (var size in new[] { 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var buffer = device.CreateStorageBuffer((ulong)size * sizeof(uint));
            var values = Enumerable.Range(0, size).Select(static value => (uint)value).ToArray();
            var input = MemoryMarshal.AsBytes(values.AsSpan());

            Assert.True(device.Upload(buffer, input), $"upload failed for {size}");
            var groups = (uint)((size + 63) / 64);
            var dispatch = device.Dispatch(pipeline, [new ComputeBufferBinding(0, 0, buffer)], groups);
            Assert.True(dispatch.Succeeded, dispatch.Error);

            var output = new byte[input.Length];
            Assert.True(device.Readback(buffer, output), $"readback failed for {size}");
            var actual = MemoryMarshal.Cast<byte, uint>(output);
            for (var index = 0; index < actual.Length; index++)
            {
                Assert.Equal((uint)(index * 2 + 1), actual[index]);
            }
        }
    }

    [Fact]
    public async Task CanonicalShaderArtifactCreatesComputePipelineWithCanonicalAbi()
    {
        var artifact = CreateArtifact(await LoadShaderAsync("compute_double.spv"),
            new ShaderResourceBinding(new ShaderBinding(0, 0), ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, ScalarLayout()));

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);

        Assert.Equal(ShaderStage.Compute, pipeline.Abi.Stage);
        Assert.Equal(64u, pipeline.Abi.WorkgroupSize.X);
        var binding = Assert.Single(pipeline.Abi.Resources);
        Assert.Equal(ShaderResourceAccess.ReadWrite, binding.Access);
        Assert.Equal(0u, binding.Binding.Set);
        Assert.Equal(0u, binding.Binding.Binding);
    }

    [Fact]
    public async Task CanonicalArtifactRejectsDuplicateBindingsAndInvalidStride()
    {
        var shader = await LoadShaderAsync("compute_double.spv");
        var resource = new ShaderResourceBinding(new ShaderBinding(0, 0), ShaderResourceKind.StorageBuffer,
            ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, ScalarLayout());
        var duplicate = CreateArtifact(shader, resource, resource);
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());

        Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(duplicate));

        var invalidLayout = new ShaderAbiLayout(4, 4, arrayStride: 2);
        var invalid = CreateArtifact(shader, new ShaderResourceBinding(new ShaderBinding(0, 0),
            ShaderResourceKind.StorageBuffer, ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, invalidLayout));
        Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(invalid));
    }

    [Fact]
    public async Task CanonicalArtifactRejectsSpirvEntryPointMismatch()
    {
        var words = new uint[]
        {
            0x07230203, 0x00010500, 0, 2, 0,
            (5u << 16) | 15u, 5, 1, 0x706d6f43, 0x00657475
        };
        var artifact = new ShaderArtifact(MemoryMarshal.AsBytes(words.AsSpan()), "main", new ShaderAbi(
            ShaderStage.Compute,
            resources: [new ShaderResourceBinding(new ShaderBinding(0, 0), ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, ScalarLayout())],
            workgroupSize: new ShaderWorkgroupSize(64, 1, 1)));

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        var error = Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(artifact));
        Assert.Contains("main", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiSetZeroBindingAndSetOneBindingDispatchMatchesOracle()
    {
        var artifact = CreateArtifact(await LoadShaderAsync("compute_multi_sets.spv"),
            new ShaderResourceBinding(new ShaderBinding(0, 0), ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.Read, ShaderStageMask.Compute, ScalarLayout()),
            new ShaderResourceBinding(new ShaderBinding(1, 0), ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.ReadWrite, ShaderStageMask.Compute, ScalarLayout()));
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);
        await using var input = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadOnly);
        await using var output = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadWrite);
        var values = Enumerable.Range(0, 64).Select(static value => (uint)value).ToArray();
        Assert.True(device.Upload(input, MemoryMarshal.AsBytes(values.AsSpan())));

        var dispatch = device.Dispatch(pipeline, [
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(1, 0, output)
        ], 1);
        Assert.True(dispatch.Succeeded, dispatch.Error);

        var outputBytes = new byte[values.Length * sizeof(uint)];
        Assert.True(device.Readback(output, outputBytes));
        var actual = MemoryMarshal.Cast<byte, uint>(outputBytes);
        for (var index = 0; index < actual.Length; index++)
        {
            Assert.Equal((uint)(index * 2 + 1), actual[index]);
        }
    }

    [Fact]
    public async Task UploadRangesCoalesceAndReuseStaging()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(32);
        var before = device.UploadStatistics;
        Assert.True(device.UploadRanges(buffer, [
            new ComputeUploadRange(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            new ComputeUploadRange(4, new byte[] { 9, 10, 11, 12 }),
            new ComputeUploadRange(8, new byte[] { 13, 14, 15, 16 })
        ]));
        var afterFirst = device.UploadStatistics;
        Assert.Equal(before.StagingAllocationCount + 1, afterFirst.StagingAllocationCount);
        Assert.True(device.UploadRanges(buffer, [new ComputeUploadRange(16, new byte[] { 1, 2, 3, 4 })]));
        Assert.Equal(afterFirst.StagingAllocationCount, device.UploadStatistics.StagingAllocationCount);
        Assert.False(device.UploadRanges(buffer, [new ComputeUploadRange(31, new byte[] { 1, 2 })]));
        Assert.True(device.UploadRanges(buffer, ReadOnlySpan<ComputeUploadRange>.Empty));
    }

    [Fact]
    public async Task DirtyRecordsBatchDisjointRangesAndGrowsStaging()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(4096);
        var payload = new byte[16];
        var first = device.ApplyDirtyRecords(buffer, [
            RenderRecordChange.Upsert(1, 7, payload),
            RenderRecordChange.Upsert(3, 7, payload)
        ], 16, 256);
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(2, first.UploadRuns);
        var afterFirst = device.UploadStatistics;

        var second = device.ApplyDirtyRecords(buffer, [RenderRecordChange.Upsert(5, 7, payload)], 16, 256);
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(afterFirst.StagingAllocationCount, device.UploadStatistics.StagingAllocationCount);

        var count = checked((int)(device.UploadStatistics.StagingCapacity / 16) + 1);
        var grown = device.ApplyDirtyRecords(buffer,
            Enumerable.Range(0, count).Select(index => RenderRecordChange.Upsert((ulong)index, 7, payload)).ToArray(),
            16,
            256);
        Assert.True(grown.Succeeded, grown.Error);
        Assert.True(device.UploadStatistics.StagingCapacity > afterFirst.StagingCapacity);
    }

    [Fact]
    public async Task EmptyAndInvalidDirtyBatchesDoNotAllocateOrSubmit()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(64);
        Assert.Equal(ComputeDirtyUpdateResult.Empty, device.ApplyDirtyRecords(buffer, ReadOnlySpan<RenderRecordChange>.Empty, 16, 4));
        var result = device.ApplyDirtyRecords(buffer, [RenderRecordChange.Upsert(4, 7, new byte[16])], 16, 4);
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AcceptedRecords);
        Assert.Equal(0, result.UploadRuns);
        Assert.Equal(0, device.UploadStatistics.DirtyBatchSubmitCount);
    }

    private static async Task<byte[]> LoadShaderAsync(string name)
        => await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static ShaderArtifact CreateArtifact(byte[] shader, params ShaderResourceBinding[] resources)
        => new(shader, "main", new ShaderAbi(ShaderStage.Compute, resources, workgroupSize: new ShaderWorkgroupSize(64, 1, 1)));

    private static ShaderAbiLayout ScalarLayout() => new(4, 4, arrayStride: 4);
}
