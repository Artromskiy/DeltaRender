# Delta.Shader.Playground

Editable compile-time compute shader playground. Change `Compute.cs`, then run
the shader through GLSL 460, SPIR-V validation and a Vulkan dispatch/readback.

The shader contract is a static method, not a runtime delegate:

```csharp
[DeltaCompute(localSizeX: 64)]
public static void Compute(
    [ReadOnlyStorageBuffer(0, 0)] ReadOnlyStorageBuffer<uint> input,
    [ReadWriteStorageBuffer(0, 1)] ReadWriteStorageBuffer<uint> output,
    [GlobalInvocationId] uint id)
{
    if (id < input.Length)
        output[id] = input[id] * 2u + 1u;
}
```

Run from this directory:

```bash
./run.sh
```

In VS Code, open this directory and press `F5`. The task creates
`artifacts/Compute.spv`, `artifacts/Compute.shader.json` and the validated
SPIR-V before launching the executable.

For a terminal-only run, use `./run.sh`; it performs the same build and then
executes the Vulkan dispatch.
