using Delta.Shader.Abstractions;

namespace Delta.Render.Shader.Compute;

public static class Doubler
{
    [ComputeShader(localSizeX: 64)]
    public static void Compute(
        [ReadWriteStorageBuffer(0, 0)] ReadWriteStorageBuffer<uint> values,
        [GlobalInvocationId] uint invocation)
    {
        if (invocation < 256u) values.Store(invocation, values.Load(invocation) * 2u + 1u);
    }
}
