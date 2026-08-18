using System.Runtime.InteropServices;
using Delta.Render.Core;
using Delta.Render.Vulkan;
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
}
