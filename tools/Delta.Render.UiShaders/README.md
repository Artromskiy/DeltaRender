# Delta.Render UI shader source

This project is the C# source of the minimal panel graphics artifacts consumed
by `Delta.Render.Smoke`. It is compiled by the external `Delta.Shader` CLI; the
renderer receives only the resulting `ShaderArtifact` SPIR-V bytes and manifest.

From `DeltaRender`:

```bash
dotnet run --project ../DeltaShader/src/Delta.Shader.Tool/Delta.Shader.Tool.csproj \
  -c Release --no-build -- build \
  tools/Delta.Render.UiShaders/Delta.Render.UiShaders.csproj \
  --profile vulkan1.2 --spirv 1.5 --glsl 460 \
  --out /tmp/delta-render-ui-generated
```

Copy `Vertex.*` and `Fragment.*` to the sample's `shaders/ui-panel.*` names
when refreshing checked-in artifacts. This project supplies checked-in renderer
shader source; it does not define the production UI handoff. The current path
is DeltaXAML `IUiDrawList` -> `UiRenderBatchAdapter` -> borrowed
`UiRenderBatch`, with no Vulkan dependency in DeltaXAML.
