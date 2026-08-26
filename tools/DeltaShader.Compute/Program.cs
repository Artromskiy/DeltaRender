using System.Runtime.InteropServices;
using Delta.Render.Core;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;
if (args.Length == 0)
{
    Console.Error.WriteLine("Expected the compile-time artifact directory as the first argument.");
    return 1;
}

var artifactDirectory = args[0];
var spirvPath = Path.Combine(artifactDirectory, "Compute.spv");
if (!File.Exists(spirvPath))
{
    Console.Error.WriteLine($"Missing compile-time SPIR-V artifact: {spirvPath}");
    return 1;
}

var shader = await File.ReadAllBytesAsync(spirvPath);
await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
var artifact = new ShaderArtifact(shader, "main", new ShaderAbi(
    ShaderStage.Compute,
    resources:
    [
        new ShaderResourceBinding(
            new ShaderBinding(0, 0),
            ShaderResourceKind.StorageBuffer,
            ShaderResourceAccess.Read,
            ShaderStageMask.Compute,
            new ShaderAbiLayout(4, 4, arrayStride: 4)),
        new ShaderResourceBinding(
            new ShaderBinding(0, 1),
            ShaderResourceKind.StorageBuffer,
            ShaderResourceAccess.ReadWrite,
            ShaderStageMask.Compute,
            new ShaderAbiLayout(4, 4, arrayStride: 4))
    ],
    workgroupSize: new ShaderWorkgroupSize(64, 1, 1)));

const int elementCount = 9;
var inputValues = Enumerable.Range(0, elementCount).Select(index => (uint)(index + 1)).ToArray();
var inputBytes = MemoryMarshal.AsBytes(inputValues.AsSpan()).ToArray();
var byteLength = checked((ulong)inputBytes.Length);
await using var pipeline = device.CreateComputePipeline(artifact);
await using var input = device.CreateStorageBuffer(byteLength, ComputeBufferAccess.ReadOnly);
await using var output = device.CreateStorageBuffer(byteLength, ComputeBufferAccess.ReadWrite);
if (!device.Upload(input, inputBytes) || !device.Upload(output, new byte[inputBytes.Length]))
{
    Console.Error.WriteLine("Unable to upload compute buffers.");
    return 1;
}

var groupCount = checked((uint)((elementCount + 63) / 64));
var result = device.Dispatch(
    pipeline,
    [
        new ComputeBufferBinding(0, 0, input),
        new ComputeBufferBinding(0, 1, output)
    ],
    groupCount);
if (!result.Succeeded)
{
    Console.Error.WriteLine($"Compute dispatch failed: {result.Error ?? result.Status.ToString()}");
    return 1;
}

var outputBytes = new byte[inputBytes.Length];
if (!device.Readback(output, outputBytes))
{
    Console.Error.WriteLine("Unable to read back compute output.");
    return 1;
}

var outputValues = MemoryMarshal.Cast<byte, uint>(outputBytes);
for (var index = 0; index < outputValues.Length; index++)
{
    var expected = inputValues[index] * 2u + 1u;
    if (outputValues[index] != expected)
    {
        Console.Error.WriteLine($"Compute mismatch at {index}: expected {expected}, got {outputValues[index]}.");
        return 1;
    }
}

Console.WriteLine($"Compile-time DeltaCompute shader dispatch passed for {elementCount} elements.");
return 0;
