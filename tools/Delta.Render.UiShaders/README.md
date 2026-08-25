# Delta.Render UI shader source

This project is the C# source of the minimal panel graphics artifacts consumed
by `Delta.Render.Smoke`. It is compiled by the external `Delta.Shader` CLI; the
target renderer handoff is a `Delta.Shader.Contract.IShaderArtifact` containing
SPIR-V and the resolved binary `ShaderAbi`.

The current smoke publication still writes an `.spv` plus `.shader.json` pair
and loads it through `Delta.Shader.Abstractions.ShaderArtifact`. That pair is
compatibility packaging for the consumer migration, not the final runtime
artifact shape.

From `DeltaRender`, refresh both smoke shader families with the canonical bounded preparation script:

```bash
./tools/prepare-smoke-shaders.sh
```

The script builds `Delta.Shader.Tool`, this project, and the local fullscreen
authoring project, then emits Vulkan 1.2 / SPIR-V 1.5 output and optional GLSL
460 inspection sidecars, validates them with `glslangValidator` and
`spirv-val`, checks the current compatibility-manifest version, and atomically
publishes only the four expected smoke pairs. GLSL, Roslyn/compiler state, live
generic values and content hashes do not cross the runtime boundary.
These projects supply checked-in renderer shader source; they do not define the
production UI handoff. The current path is DeltaXAML `IUiDrawList` ->
`UiRenderBatchAdapter` -> borrowed `UiRenderBatch`, with no Vulkan dependency in
DeltaXAML.
