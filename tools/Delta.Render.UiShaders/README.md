# Delta.Render UI shader source

This project is the C# source of the minimal panel graphics artifacts consumed
by `Delta.Render.Smoke`. It is compiled by the external `Delta.Shader` CLI; the
renderer receives only the resulting `ShaderArtifact` SPIR-V bytes and manifest.

From `DeltaRender`, refresh both smoke shader families with the canonical bounded preparation script:

```bash
./tools/prepare-smoke-shaders.sh
```

The script builds `Delta.Shader.Tool`, this project, and the local fullscreen
authoring project, then emits Vulkan 1.2 / SPIR-V 1.5 / GLSL 460 artifacts,
validates them with `glslangValidator` and `spirv-val`, checks the current ABI
manifest version, and atomically publishes only the four expected smoke pairs.
These projects supply checked-in renderer shader source; they do not define the
production UI handoff. The current path is DeltaXAML `IUiDrawList` ->
`UiRenderBatchAdapter` -> borrowed `UiRenderBatch`, with no Vulkan dependency in
DeltaXAML.
