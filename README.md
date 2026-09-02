# DeltaRender

Vulkan renderer for game output, editor viewports and runtime UI. It provides
one graph-based path for windowed, offscreen and compute-only work.

## What it provides

- Transfer, compute and raster passes in one RenderGraph.
- Persistent buffers, textures and samplers with checked lifetimes.
- Transient graph resources and dependency-aware execution.
- Final DeltaShader artifacts with ABI validation.
- Explicit readback for CPU-visible output.
- Vulkan presentation through the SDL3 platform package.

## Quick start

Reference the public renderer packages:

```xml
<PackageReference Include="DeltaRender" Version="*" />
<PackageReference Include="DeltaRender.Vulkan" Version="*" />
```

After obtaining an `IRenderFrameSession` from the platform integration, build
and execute a frame:

```csharp
IRenderGraph graph = session.CreateRenderGraph();
graph.Build(frameNumber, features);
RenderGraphExecutionResult result = graph.Execute();
```

For a windowed session this submits the frame for presentation. For an
offscreen or compute-only session it produces graph output that can be read
back explicitly when needed.

## Core concepts

```text
session -> graph.Build(features) -> transfer/compute/raster -> graph.Execute()
```

The session owns persistent resources and the optional presentation target.
The graph owns temporary graph handles and execution dependencies. Features
provide application data and pass recording without exposing Vulkan objects.

## Capabilities and limits

- Vulkan is the supported rendering backend; MoltenVK enables macOS support.
- Shaders must be final `DeltaShader.Contract` artifacts with a compatible ABI.
- UI coordinates use a top-left origin; UV `(0,0)` is the top-left texel.
- Depth and stencil are available when declared and compatible with the target.
- Input polling, text shaping, XAML layout and ECS state belong to host systems.
- GPU timing and asynchronous readback depend on device capabilities.

## Public packages and examples

- [`DeltaRender`](src/DeltaRender/DeltaRender.csproj): renderer-neutral API.
- [`DeltaRender.Vulkan`](src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj):
  Vulkan implementation.
- [`DeltaRender.Platform.SDL3`](src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj):
  SDL3 window and surface integration.
- [Headless shader playground](samples/DeltaRender.HeadlessShaderPlayground/README.md).

## Further reading

- [User API](docs/USER_API.md)
- [Renderer contract](docs/CONTRACT.md)
- [Text integration contract](docs/TEXT_CONTRACT.md)
- [Documentation index](docs/README.md)
