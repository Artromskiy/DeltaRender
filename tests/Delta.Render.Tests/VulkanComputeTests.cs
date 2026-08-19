using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Delta.Render.Core;
using Delta.Render.Vulkan;
using DeltaShaderAccess = Delta.Shader.Abstractions.ShaderResourceAccess;
using DeltaShaderArtifact = Delta.Shader.Abstractions.ShaderArtifact;
using DeltaShaderManifest = Delta.Shader.Abstractions.ShaderAbiManifest;
using DeltaShaderResource = Delta.Shader.Abstractions.ShaderAbiResource;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanComputeTests
{
    [Fact]
    public async Task Std430_compute_dispatch_matches_cpu_oracle_for_required_sizes()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_double.spv"));
        var metadata = new ComputeShaderMetadata(
            ComputeAbiLayout.Std430,
            64,
            1,
            1,
            new[] { new ComputeDescriptorBinding(0, 0, ComputeDescriptorKind.StorageBuffer, ComputeBufferAccess.ReadWrite) });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(shader, in metadata);

        await using var zeroBuffer = device.CreateStorageBuffer(0);
        var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
        Assert.Equal(ComputeDispatchStatus.NoOp, noOp.Status);

        foreach (var size in new[] { 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var buffer = device.CreateStorageBuffer((ulong)size * sizeof(uint));
            var values = new uint[size];
            for (var i = 0; i < values.Length; i++) values[i] = (uint)i;
            var input = MemoryMarshal.AsBytes(values.AsSpan());

            Assert.True(device.Upload(buffer, input), $"upload failed for {size}");
            var groups = (uint)((size + 63) / 64);
            var dispatch = device.Dispatch(pipeline, new[] { new ComputeBufferBinding(0, 0, buffer) }, groups);
            Assert.True(dispatch.Succeeded, dispatch.Error);
            Assert.Equal(ComputeDispatchStatus.Executed, dispatch.Status);

            var output = new byte[input.Length];
            Assert.True(device.Readback(buffer, output), $"readback failed for {size}");
            var actual = MemoryMarshal.Cast<byte, uint>(output);
            for (var i = 0; i < actual.Length; i++)
            {
                Assert.Equal((uint)(i * 2 + 1), actual[i]);
            }
        }
    }

    [Fact]
    public async Task Shader_artifact_manifest_creates_compute_pipeline_without_raw_metadata()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_double.spv"));
        var artifact = new DeltaShaderArtifact(shader, new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[]
            {
                new DeltaShaderResource
                {
                    Name = "values",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadWrite,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                }
            }
        });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);

        Assert.Equal(ComputeAbiLayout.Std430, pipeline.Metadata.AbiLayout);
        Assert.Equal(64u, pipeline.Metadata.LocalSizeX);
        var binding = Assert.Single(pipeline.Metadata.Bindings.Span.ToArray());
        Assert.Equal(ComputeBufferAccess.ReadWrite, binding.Access);
        Assert.Equal(0u, binding.Set);
        Assert.Equal(0u, binding.Binding);
    }

    [Fact]
    public async Task Shader_artifact_manifest_rejects_duplicate_bindings_and_invalid_stride()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_double.spv"));
        var resource = new DeltaShaderResource
        {
            Name = "values",
            Category = "storage-buffer",
            Set = 0,
            Binding = 0,
            Access = DeltaShaderAccess.ReadWrite,
            Layout = "std430",
            Alignment = 4,
            Size = 4,
            ArrayStride = 4
        };
        var artifact = new DeltaShaderArtifact(shader, new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[] { resource, resource }
        });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(artifact));

        var invalidStrideArtifact = new DeltaShaderArtifact(shader, new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[]
            {
                new DeltaShaderResource
                {
                    Name = "values",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadWrite,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 2
                }
            }
        });

        Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(invalidStrideArtifact));
    }

    [Fact]
    public async Task Shader_artifact_rejects_manifest_entry_point_mismatch()
    {
        var words = new uint[]
        {
            0x07230203, 0x00010500, 0, 2, 0,
            (5u << 16) | 15u, 5, 1, 0x706d6f43, 0x00657475
        };
        var artifact = new DeltaShaderArtifact(MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[]
            {
                new DeltaShaderResource
                {
                    Name = "values",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadWrite,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                }
            }
        });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        var error = Assert.Throws<ArgumentException>(() => device.CreateComputePipeline(artifact));
        Assert.Contains("main", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_ssbo_artifact_dispatch_matches_oracle_and_repeated_dispatch()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_multi.spv"));
        var artifact = CreateMultiBufferArtifact(shader);

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);
        Assert.Equal(2, pipeline.Metadata.Bindings.Length);

        await using var input = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadOnly);
        await using var output = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadWrite);
        var values = Enumerable.Range(0, 64).Select(static value => (uint)value).ToArray();
        var inputBytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        Assert.True(device.Upload(input, inputBytes));

        var bindings = new[]
        {
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(0, 1, output)
        };
        var first = device.Dispatch(pipeline, bindings, 1);
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(ComputeDispatchStatus.Executed, first.Status);

        var outputBytes = new byte[inputBytes.Length];
        Assert.True(device.Readback(output, outputBytes));
        Assert.Equal(Enumerable.Range(0, 64).Select(static value => (uint)(value * 2 + 1)), MemoryMarshal.Cast<byte, uint>(outputBytes).ToArray());

        var replacement = new uint[] { 100, 101, 102, 103 };
        var replacementBytes = MemoryMarshal.AsBytes(replacement.AsSpan()).ToArray();
        Assert.True(device.UploadRanges(input, new[] { new ComputeUploadRange(0, replacementBytes) }));
        var repeated = device.Dispatch(pipeline, bindings, 1);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.True(device.Readback(output, outputBytes));
        var repeatedValues = MemoryMarshal.Cast<byte, uint>(outputBytes);
        Assert.Equal(201u, repeatedValues[0]);
        Assert.Equal(207u, repeatedValues[3]);
        Assert.Equal(9u, repeatedValues[4]);
        var repeatedReadback = new byte[outputBytes.Length];
        Assert.True(device.Readback(output, repeatedReadback));
        Assert.Equal(outputBytes, repeatedReadback);
    }

    [Fact]
    public async Task Multi_set_zero_binding_and_set_one_binding_dispatch_matches_oracle()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_multi_sets.spv"));
        var artifact = new DeltaShaderArtifact(shader, new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[]
            {
                new DeltaShaderResource
                {
                    Name = "inputBuffer",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadOnly,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                },
                new DeltaShaderResource
                {
                    Name = "outputBuffer",
                    Category = "storage-buffer",
                    Set = 1,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadWrite,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                }
            }
        });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(artifact);
        await using var input = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadOnly);
        await using var output = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadWrite);
        var values = Enumerable.Range(0, 64).Select(static value => (uint)value).ToArray();
        Assert.True(device.Upload(input, MemoryMarshal.AsBytes(values.AsSpan())));

        var dispatch = device.Dispatch(pipeline, new[]
        {
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(1, 0, output)
        }, 1);
        Assert.True(dispatch.Succeeded, dispatch.Error);

        var outputBytes = new byte[values.Length * sizeof(uint)];
        Assert.True(device.Readback(output, outputBytes));
        var actual = MemoryMarshal.Cast<byte, uint>(outputBytes);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal((uint)(i * 2 + 1), actual[i]);
        }
    }

    [Fact]
    public async Task Multi_ssbo_dispatch_rejects_missing_extra_duplicate_and_read_only_bindings()
    {
        var shader = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_multi.spv"));
        var metadata = new ComputeShaderMetadata(
            ComputeAbiLayout.Std430,
            64,
            1,
            1,
            new[]
            {
                new ComputeDescriptorBinding(0, 0, ComputeDescriptorKind.StorageBuffer, ComputeBufferAccess.ReadOnly),
                new ComputeDescriptorBinding(0, 1, ComputeDescriptorKind.StorageBuffer, ComputeBufferAccess.ReadWrite)
            });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var pipeline = device.CreateComputePipeline(shader, in metadata);
        await using var input = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadOnly);
        await using var output = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadWrite);
        await using var readOnlyOutput = device.CreateStorageBuffer(64 * sizeof(uint), ComputeBufferAccess.ReadOnly);

        Assert.Equal(ComputeDispatchStatus.Invalid, device.Dispatch(pipeline, new[] { new ComputeBufferBinding(0, 0, input) }, 1).Status);
        Assert.Equal(ComputeDispatchStatus.Invalid, device.Dispatch(pipeline, new[]
        {
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(0, 1, output),
            new ComputeBufferBinding(0, 2, output)
        }, 1).Status);
        Assert.Equal(ComputeDispatchStatus.Invalid, device.Dispatch(pipeline, new[]
        {
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(0, 0, output)
        }, 1).Status);
        Assert.Equal(ComputeDispatchStatus.Invalid, device.Dispatch(pipeline, new[]
        {
            new ComputeBufferBinding(0, 0, input),
            new ComputeBufferBinding(0, 1, readOnlyOutput)
        }, 1).Status);
    }

    [Fact]
    public async Task Upload_ranges_coalesce_adjacent_and_overlapping_ranges_and_reuse_staging()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(32);
        var first = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var second = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };
        var overlap = new byte[] { 100, 101, 102, 103 };

        var before = device.UploadStatistics;
        Assert.True(device.UploadRanges(buffer, new[]
        {
            new ComputeUploadRange(0, first),
            new ComputeUploadRange(8, second),
            new ComputeUploadRange(4, overlap)
        }));
        var afterFirst = device.UploadStatistics;
        Assert.Equal(before.StagingAllocationCount + 1, afterFirst.StagingAllocationCount);

        var output = new byte[32];
        Assert.True(device.Readback(buffer, output));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 100, 101, 102, 103, 9, 10, 11, 12, 13, 14, 15, 16 }, output.AsSpan(0, 16).ToArray());

        Assert.True(device.UploadRanges(buffer, new[] { new ComputeUploadRange(16, new byte[] { 21, 22, 23, 24 }) }));
        var afterReuse = device.UploadStatistics;
        Assert.Equal(afterFirst.StagingAllocationCount, afterReuse.StagingAllocationCount);

        Assert.False(device.UploadRanges(buffer, new[] { new ComputeUploadRange(2, new byte[] { 1, 2, 3, 4 }) }));
        Assert.True(device.UploadRanges(buffer, ReadOnlySpan<ComputeUploadRange>.Empty));
    }

    private static DeltaShaderArtifact CreateMultiBufferArtifact(byte[] shader)
        => new(shader, new DeltaShaderManifest
        {
            SourceEntryPointName = "Compute",
            EntryPointName = "main",
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            Resources = new[]
            {
                new DeltaShaderResource
                {
                    Name = "inputBuffer",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 0,
                    Access = DeltaShaderAccess.ReadOnly,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                },
                new DeltaShaderResource
                {
                    Name = "outputBuffer",
                    Category = "storage-buffer",
                    Set = 0,
                    Binding = 1,
                    Access = DeltaShaderAccess.ReadWrite,
                    Layout = "std430",
                    Alignment = 4,
                    Size = 4,
                    ArrayStride = 4
                }
            }
        });

    [Fact]
    public async Task Dirty_records_validate_ranges_and_coalesce_adjacent_updates()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(64);
        var payload = Marshal.AllocHGlobal(16);
        try
        {
            var bytes = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
            Marshal.Copy(bytes, 0, payload, bytes.Length);
            var changes = new[]
            {
                RenderRecordChange.Upsert(1, 7, (ulong)payload, 16),
                RenderRecordChange.Upsert(2, 7, (ulong)payload, 16),
                RenderRecordChange.Remove(3, 7)
            };

            var result = device.ApplyDirtyRecords(buffer, changes, 16, 4);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(3, result.AcceptedRecords);
            Assert.Equal(1, result.UploadRuns);

            var output = new byte[64];
            Assert.True(device.Readback(buffer, output));
            Assert.Equal(bytes, output.AsSpan(16, 16).ToArray());
            Assert.Equal(bytes, output.AsSpan(32, 16).ToArray());
            Assert.Equal(new byte[16], output.AsSpan(48, 16).ToArray());
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }
    }

    [Fact]
    public async Task Dirty_records_batch_disjoint_ranges_reuses_and_grows_staging()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(4096);
        var payload = Marshal.AllocHGlobal(16 * 32);
        try
        {
            var bytes = Enumerable.Range(0, 16 * 32).Select(static value => (byte)value).ToArray();
            Marshal.Copy(bytes, 0, payload, bytes.Length);

            var before = device.UploadStatistics;
            var first = device.ApplyDirtyRecords(
                buffer,
                new[]
                {
                    RenderRecordChange.Upsert(1, 7, (ulong)payload, 16),
                    RenderRecordChange.Upsert(3, 7, (ulong)(payload + 16), 16)
                },
                16,
                256);

            Assert.True(first.Succeeded, first.Error);
            Assert.Equal(2, first.UploadRuns);
            var afterFirst = device.UploadStatistics;
            Assert.Equal(before.StagingAllocationCount + 1, afterFirst.StagingAllocationCount);
            Assert.Equal(before.DirtyBatchSubmitCount + 1, afterFirst.DirtyBatchSubmitCount);

            var second = device.ApplyDirtyRecords(
                buffer,
                new[] { RenderRecordChange.Upsert(5, 7, (ulong)(payload + 32), 16) },
                16,
                256);

            Assert.True(second.Succeeded, second.Error);
            var afterReuse = device.UploadStatistics;
            Assert.Equal(afterFirst.StagingAllocationCount, afterReuse.StagingAllocationCount);
            Assert.Equal(afterFirst.DirtyBatchSubmitCount + 1, afterReuse.DirtyBatchSubmitCount);

            var recordsForGrowth = checked((int)(afterReuse.StagingCapacity / 16) + 1);
            var growthChanges = Enumerable.Range(0, recordsForGrowth)
                .Select(index => RenderRecordChange.Upsert((uint)index, 7, (ulong)payload, 16))
                .ToArray();
            var grown = device.ApplyDirtyRecords(buffer, growthChanges, 16, 256);

            Assert.True(grown.Succeeded, grown.Error);
            Assert.Equal(1, grown.UploadRuns);
            var afterGrow = device.UploadStatistics;
            Assert.Equal(afterReuse.StagingAllocationCount + 1, afterGrow.StagingAllocationCount);
            Assert.Equal(afterReuse.DirtyBatchSubmitCount + 1, afterGrow.DirtyBatchSubmitCount);
            Assert.True(afterGrow.StagingCapacity > afterReuse.StagingCapacity);
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }
    }

    [Fact]
    public async Task Empty_dirty_batch_is_no_op_without_staging_or_submit()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(64);

        var result = device.ApplyDirtyRecords(buffer, ReadOnlySpan<RenderRecordChange>.Empty, 16, 4);

        Assert.Equal(ComputeDirtyUpdateResult.Empty, result);
        Assert.Equal(0, device.UploadStatistics.StagingAllocationCount);
        Assert.Equal(0, device.UploadStatistics.DirtyBatchSubmitCount);
    }

    [Fact]
    public async Task Invalid_dirty_ranges_are_rejected_without_allocating_staging()
    {
        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        await using var buffer = device.CreateStorageBuffer(64);
        var invalid = new[]
        {
            RenderRecordChange.Upsert(4, 7, 0, 16),
            RenderRecordChange.Upsert(1, 7, 0, 17)
        };

        var result = device.ApplyDirtyRecords(buffer, invalid, 16, 4);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AcceptedRecords);
        Assert.Equal(2, result.RejectedRecords);
        Assert.Equal(0, result.UploadRuns);
        Assert.Equal(0, device.UploadStatistics.StagingAllocationCount);
        Assert.Equal(0, device.UploadStatistics.DirtyBatchSubmitCount);
    }
}
