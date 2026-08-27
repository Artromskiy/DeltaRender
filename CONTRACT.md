# DeltaRender cross-project contract

This is the canonical cross-project contract supplied by DeltaRender. It is
the graph-first renderer-facing API for the engine and editor, not an internal
description of the RenderGraph implementation. The contract is Vulkan-only;
SDL3 supplies the window/surface bridge and MoltenVK supplies the macOS Vulkan
portability layer.

The declarations live in one flat folder one level above the
`DeltaRender` project. The project contains no implementation beside the
contract assembly:

```text
src/Contract/*.cs
```

There are no contract subfolders. The project identity is `DeltaRender`; the
public CLR namespace for this contract is `Delta.Render`. Implementation
namespaces remain private to the renderer modules.

## Producer and consumers

- **Producer/implementer:** `DeltaRender` (`DeltaRender` contracts and
  `DeltaRender.Vulkan` implementation).
- **Direct consumers:** DeltaEngine render features and DeltaEditor
  composition/adapters.
- **Upstream producers consumed here:** `DeltaShader.Contract` artifacts,
  `DeltaXAML.Contract` display data and `DeltaText.Contract` shaped/glyph
  data.

Consumers adapt to these types; they must not publish a second renderer ABI,
copy Vulkan handles into their own public model or make Render poll input,
parse XAML/C#, shape text or own ECS storage.

## Contract surface

The flat `Contract` folder contains the following public areas:

- **Window/lifecycle:** `IRenderWindowFactory`, `IRenderWindow`,
  `IRenderFrameSession`, `WindowConfiguration` and `WindowMetrics`.
- **RenderGraph:** `IRenderGraph`, `IRenderGraphBuilder`,
  `IRenderFeature`, raster/compute/transfer pass interfaces, command contexts,
  resource descriptions and graph-local handles.
- **Resources and views:** persistent/imported handles, transient graph
  handles, `RenderView`, `RenderViewport`, `PixelRect`, attachments and usage
  flags.
- **Shader handoff:** `IShaderArtifact`, `IGraphicsShaderProgram` and the
  resolved `ShaderAbi` from `DeltaShader.Contract`. Render consumes final
  SPIR-V plus ABI; it does not consume GLSL, Roslyn state, live generic values
  or compiler manifests.
- **Compute:** storage buffers, uploads/readback, dispatch and dirty-record
  application. Raw SPIR-V import is an explicit low-level overload and still
  requires the canonical `ShaderAbi`.
- **Text/UI:** renderer-owned atlas/page upload contracts, glyph instances,
  UI rectangles, clip records, text submissions and borrowed UI frame views.
  DeltaText remains renderer-neutral; atlas packing, UV assignment, staging,
  descriptors and batching belong to Render.
- **Diagnostics:** `RenderDiagnostic`, `RenderDiagnosticBag` and lifecycle/
  creation results.

## Frame and graph flow

```text
Engine/editor extraction
  -> feature.Submit(data)
  -> graph.Build(frame, features)
  -> feature.AddPasses(graph, context)
  -> graph derives dependencies and Vulkan synchronization
  -> pass.Record(commandContext)
  -> graph.Execute()
  -> present
```

`IRenderFeature.Submit` replaces feature data; it does not perform GPU work.
Features declare resource reads/writes before recording. The Vulkan executor
owns pass ordering, resource lifetime, barriers and command recording.

`IRenderFrameSession.CreateRenderGraph()` returns a graph using the same
session device, queues and lifetime. Consumers do not access raw Vulkan
handles. The graph owns the complete frame lifecycle: target acquisition,
recording, submission and presentation.

`RenderGraphFrame` contains frame identity and one or more `RenderView` values.
Each view owns a surface handle, viewport and pixel-space `PixelRect`, so a
frame may target multiple surfaces. No Render contract carries `DeltaTime` or
defines a clock.

There is no separate direct frame-submission or packet API in this
cross-project contract. UI, text, mesh and compute work enters through graph
features and pass-owned submission data; `IRenderGraph.Execute()` performs the
single submission path.

## Ownership and lifetime

- Render owns Vulkan images, buffers, samplers, descriptors, staging memory,
  pipeline caches and disposal.
- UI/text producers own their source objects and payloads; adapters copy or
  borrow them for exactly one frame according to the documented API.
- Borrowed packets, graph handles and UI frame views expire at their stated
  frame/build/mutation boundary and must not be retained.
- Engine owns event polling, scheduling and time domains. DeltaXAML owns
  retained UI/layout. DeltaText owns shaping and glyph-image generation.

## Non-goals

This contract does not expose Vulkan command-buffer types, allocator details,
render-pass implementation, ECS chunks/rows, XAML controls, text shaping
internals, GLSL source or shader compiler state. Such details belong in
`INTERNAL.md` and implementation code.
