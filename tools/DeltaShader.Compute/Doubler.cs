using System.Diagnostics.CodeAnalysis;
using Delta.Shader.Abstractions;

namespace Delta.Render.Shader.Compute;

public static class Doubler
{
    [DeltaCompute(localSizeX: 64)]
    [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "This method is a compiler entry point; shader parameters are validated by DeltaShader during generation.")]
    public static void Compute(
        [ReadOnlyStorageBuffer(0, 0)] ReadOnlyStorageBuffer<uint> input,
        [ReadWriteStorageBuffer(0, 1)] ReadWriteStorageBuffer<uint> output,
        [GlobalInvocationId] uint invocation)
    {
        if (invocation < input.Length)
        {
            output[invocation] = input[invocation] * 2u + 1u;
        }
    }
}
