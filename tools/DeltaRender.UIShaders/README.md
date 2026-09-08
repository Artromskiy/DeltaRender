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

The `cached-mask-rounded-rectangle` pair is the prepared cached-mask quality
tier. It consumes one premultiplied RGBA mask texture at set `0`, binding `1`,
while its instance record remains at set `0`, binding `0`; generated ABI and
pack helpers are authoritative for both resources. The mask is sampled in
`MaskUv` space and multiplied by the typed instance color. This is a fixed
artifact, not runtime shader composition or file probing.

The `ShadowOnly` quality tier publishes standalone solid and rounded
outer-shadow pairs. Their vertex stage expands the raster quad by the offset
and spread/blur extent while retaining UVs in the original rectangle space;
their fragment stage outputs only premultiplied shadow. Use a separate base
pass, selected from `solid-rectangle`, `solid-stroke`, `rounded-rectangle`, or
`rounded-stroke`. Shadow-only instance records omit `FillColor`, use set `0`,
binding `0`, and keep frame push constants in `UiFrameConstants`.

The UI shader source also publishes standalone `OuterGlowOnly` solid and
rounded pairs. Their vertex stage expands the raster quad by the typed glow
radius and their fragment stage emits only premultiplied glow. They are used
as a separate pass before the ordinary base artifact; runtime does not compose
shader source or inspect files.
