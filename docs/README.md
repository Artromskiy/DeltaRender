# DeltaRender

Vulkan renderer for game output, editor viewports and runtime UI.

## What it provides

- One RenderGraph path for transfer, compute and raster work.
- Windowed, offscreen and compute-only sessions.
- Persistent buffers, textures and samplers with lifetime and generation checks.
- Transient graph resources and automatic pass ordering and hazard handling.
- Canonical DeltaShader artifacts with ABI validation and typed producer packers.
- Optional readback for rendered or computed data.

## Quick start

Add the renderer packages to an application:

```xml
<PackageReference Include="DeltaRender" Version="*" />
<PackageReference Include="DeltaRender.Vulkan" Version="*" />
```

Obtain an `IRenderFrameSession` from the platform integration, create its graph,
add features, then execute the frame:

```csharp
IRenderGraph graph = session.CreateRenderGraph();
graph.Build(frameNumber, features);
RenderGraphExecutionResult result = graph.Execute();
```

The result is a recorded and submitted frame for a windowed session, or an
offscreen/compute result for a headless session. Explicit readback is available
when CPU-visible output is required.

## Core concepts

```text
session -> graph.Build(features) -> transfer/compute/raster -> graph.Execute()
```

The session owns persistent resources and the optional presentation target.
The graph owns temporary graph handles, dependencies and execution. Features
translate application data into ordinary graph passes without exposing Vulkan
objects.

## Capabilities and limits

- Supported rendering backend: Vulkan, including MoltenVK on macOS.
- Supported shader input: final `DeltaShader.Contract` artifacts and their ABI.
- UI coordinates use a top-left origin; texture UV `(0,0)` is top-left.
- Depth and stencil attachments are supported when declared by the raster
  contract and compatible with the target.
- A session has one target; multi-window coordination belongs to the host.
- Input polling, text shaping, XAML layout and ECS state are outside the
  renderer.
- GPU timing and asynchronous readback depend on device capabilities.

## Public packages and examples

- [`DeltaRender`](../src/DeltaRender/DeltaRender.csproj): renderer-neutral
  public contract.
- [`DeltaRender.Vulkan`](../src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj):
  Vulkan implementation.
- [`DeltaRender.Platform.SDL3`](../src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj):
  SDL3 window and surface integration.
- [Headless shader playground](../samples/DeltaRender.HeadlessShaderPlayground/README.md):
  offscreen shader rendering.
- [Shader sandbox](../samples/DeltaRender.ShaderSandbox/README.md): windowed
  shader experiments.

## Further reading

- [User API](USER_API.md)
- [Renderer contract](CONTRACT.md)
- [Text integration contract](TEXT_CONTRACT.md)
- [License](../LICENSE)
