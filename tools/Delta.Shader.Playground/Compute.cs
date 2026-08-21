using Delta.Shader.Abstractions;

namespace Delta.Shader.Playground;

public static class ComputeShader
{
    [DeltaCompute(localSizeX: 64)]
    public static void Compute(
        [ReadOnlyStorageBuffer(0, 0)] ReadOnlyStorageBuffer<uint> input,
        [ReadWriteStorageBuffer(0, 1)] ReadWriteStorageBuffer<uint> output,
        [GlobalInvocationId] uint id)
    {
        if (id < input.Length)
            output[id] = input[id] * 2u + 1u;
    }
}
