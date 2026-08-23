using System.Text.Json;
using System.Runtime.InteropServices;
using Delta.Render.Core;
using Delta.Render.Vulkan;
using Delta.Shader.Abstractions;
if (args.Length == 0)
{
    Console.Error.WriteLine("Expected the compile-time artifact directory as the first argument.");
    return 1;
}

var artifactDirectory = args[0];
var spirvPath = Path.Combine(artifactDirectory, "Compute.spv");
var manifestPath = Path.Combine(artifactDirectory, "Compute.shader.json");
if (!File.Exists(spirvPath))
{
    Console.Error.WriteLine($"Missing compile-time SPIR-V artifact: {spirvPath}");
    return 1;
}

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Missing compile-time shader manifest: {manifestPath}");
    return 1;
}

var manifest = JsonSerializer.Deserialize<ShaderAbiManifest>(await File.ReadAllTextAsync(manifestPath));
if (manifest is null)
{
    Console.Error.WriteLine($"Could not deserialize compile-time shader manifest: {manifestPath}");
    return 1;
}

var artifact = new ShaderArtifact(await File.ReadAllBytesAsync(spirvPath), manifest);
await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
await using var dispatcher = new ComputeDispatcher<IComputeStorageBuffer>(device, artifact, static buffer => buffer);

const int elementCount = 9;
var inputValues = Enumerable.Range(0, elementCount).Select(index => (uint)(index + 1)).ToArray();
var inputBytes = MemoryMarshal.AsBytes(inputValues.AsSpan()).ToArray();
var byteLength = checked((ulong)inputBytes.Length);
await using var input = device.CreateStorageBuffer(byteLength, ComputeBufferAccess.ReadOnly);
await using var output = device.CreateStorageBuffer(byteLength, ComputeBufferAccess.ReadWrite);
if (!device.Upload(input, inputBytes) || !device.Upload(output, new byte[inputBytes.Length]))
{
    Console.Error.WriteLine("Unable to upload compute buffers.");
    return 1;
}

var request = new ComputeDispatchRequest<IComputeStorageBuffer>(
    artifact,
    ComputeDispatchDimensions.ForElements(artifact, elementCount),
    [
        new ComputeDispatchBinding<IComputeStorageBuffer>(0, 0, input),
        new ComputeDispatchBinding<IComputeStorageBuffer>(0, 1, output)
    ]);
await dispatcher.DispatchAsync(request);

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
