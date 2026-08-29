# DeltaRender UI shader source

This project contains C# source for the minimal panel graphics pair consumed by
the smoke sample. DeltaShader generates the GLSL and paired
`DeltaShader.Contract` artifact factory from this source. The runtime receives
SPIR-V and the generated resolved `ShaderAbi`; it does not compile source or
parse a producer manifest.

Refresh the checked-in smoke files with:

```bash
./tools/prepare-smoke-shaders.sh
```

The preparation script builds the producer tools, emits Vulkan 1.2 / SPIR-V
1.5 output with GLSL 460 inspection sidecars, validates the output and updates
only the expected shader files. This source project is not the UI layout or
renderer submission boundary.
