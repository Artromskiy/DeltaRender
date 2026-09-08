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

The analytic rounded-rectangle source also publishes `InnerShadow` in the
typed `UiEffectParameters` payload. Its fragment order is outer shadow, outer
glow, fill, inner shadow, then stroke; the generated instance packer remains the
single ABI authority. Cached-mask and backdrop-blur effects are intentionally
not represented by this analytic artifact because they require a separate
texture/readback path.
