# Graphics semantics and render providers

The shader package `Delta.Shader` owns the default graphics builtins
`Position` and `FragmentColor`. The independent common package
`Delta.Graphics.Semantics` contains only cross-domain stage values: `Uv0`,
`Uv1`, and `VertexColor`. It has no dependency on the shader compiler or
Vulkan runtime.

Domain providers own the values that describe their payloads:

- `DeltaRender.UIShaders` owns UI rectangle and rounded-rectangle values.
- `DeltaRender.Text` owns glyph payloads, text parameters, and atlas shader fields.
- `DeltaRender.Mesh` owns mesh payloads and coordinate-space-qualified mesh values.

Provider projects publish their generated `ShaderAbi`, shader artifacts, and
typed pack helpers. Render consumers use those generated surfaces and do not
recalculate offsets, strides, formats, or interstage layout.

`DeltaShader` recognizes the fixed builtins from its own assembly and the
common/provider value wrappers by their `Value` shape. `[Interstage]` marks the
stage boundary; ordinary mapped `float2`, `float3`, and `float4` fields are
transferred by shape and declaration order. The compiler does not infer whether
an ordinary `float3` means a normal, color, or tangent. That meaning belongs to
the provider that owns the payload.

The `DeltaRender.slnx` entry is an aggregation convenience only. The common
semantics and every provider remain independent package boundaries.
