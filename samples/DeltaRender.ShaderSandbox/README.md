# DeltaRender shader sandbox

This sample opens a Vulkan window and submits a fullscreen pass through the
DeltaRender render graph. It consumes generated SPIR-V and `ShaderAbi` data
from a DeltaShader producer; it does not compile shader source itself.

Run it from the DeltaRender repository root with a generated fullscreen pair:

```sh
dotnet run \
  --project samples/DeltaRender.ShaderSandbox/DeltaRender.ShaderSandbox.csproj \
  -c Release -- \
  --shader-dir <generated-artifact-directory> \
  --frames 3
```

The directory must contain `Vertex.vert.spv` and `Fragment.frag.spv`. Add
`--interactive` to keep the window running until it is closed. Resize and
event pumping are owned by the sample host; the renderer/session remains a
Vulkan render-graph implementation.
