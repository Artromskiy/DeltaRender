using Delta.Shader.Abstractions;

namespace Delta.Render.Shader.Compute;

public static class Doubler
{
    [ComputeShader(localSizeX: 64)]
    public static void Compute(
        [ReadOnlyStorageBuffer(0, 0)] ReadOnlyStorageBuffer<uint> input,
        [ReadWriteStorageBuffer(0, 1)] ReadWriteStorageBuffer<uint> output,
        [GlobalInvocationId] uint invocation)
    {
        if (invocation < input.Length) output.Store(invocation, input.Load(invocation) * 2u + 1u);
    }
}
