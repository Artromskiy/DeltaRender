using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Delta.Render.Core;
using Delta.Render.Vulkan;
using Delta.Shader.Abstractions;
using Delta.Shader.Runtime;

Expression<Action<ReadOnlyStorageBuffer<uint>, ReadWriteStorageBuffer<uint>, uint>> kernel =
    (input, output, invocation) => output.Store(
        invocation,
        invocation < input.Length
            ? input.Load(invocation) * 2u + 1u
            : 0u);

var compilation = await ExpressionComputeShaderCompiler.CompileAsync(
    kernel,
    new ComputeExpressionOptions
    {
        InvocationParameterIndex = 2,
        Bindings =
        [
            new ComputeExpressionBinding(0, 0, 0, ShaderResourceAccess.ReadOnly),
            new ComputeExpressionBinding(1, 0, 1, ShaderResourceAccess.ReadWrite)
        ]
    });
if (!compilation.Success || compilation.Artifact is null)
{
    foreach (var diagnostic in compilation.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.Id}: {diagnostic.Message}");
    }

    return 1;
}

var artifact = compilation.Artifact;
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

Console.WriteLine($"Runtime expression shader dispatch passed for {elementCount} elements (cache key {compilation.CacheKey}).");
return 0;
