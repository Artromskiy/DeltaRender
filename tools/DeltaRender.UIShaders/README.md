# DeltaRender UI shader source

This project contains C# source for the minimal panel graphics pair consumed by
the smoke sample. DeltaShader generates the GLSL and paired
`DeltaShader.Contract` artifact factory from this source. The runtime receives
SPIR-V and the generated resolved `ShaderAbi`; it does not compile source or
parse a producer manifest.

Build this project through its normal project build. The `DeltaShader.Tool`
build integration discovers the producer source and emits outputs in the
producer build directory; no consumer-side shader copy is maintained here.
Use the generated program/factory API for `ShaderArtifact`, `ShaderAbi`,
`VertexAbi`/`FragmentAbi` and typed packers. Do not parse `.shader.json` or
calculate ABI offsets in the renderer; those sidecars are inspection output.
This source project is not the UI layout or renderer submission boundary.
