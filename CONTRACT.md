# DeltaRender cross-project contract

This is the canonical cross-project contract supplied by DeltaRender. It is
the complete renderer-facing API for the engine and editor, not an internal
description of the RenderGraph implementation. The contract is Vulkan-only;
SDL3 supplies the window/surface bridge and MoltenVK supplies the macOS Vulkan
portability layer.

The declarations live in one flat folder one level above the
`DeltaRender` project. The project contains no implementation beside the
contract assembly:

```text
src/Contract/*.cs
```

There are no contract subfolders. The project identity is `DeltaRender`; its
public CLR namespaces are `Delta.Render.Core` for general values and
`Delta.Render.Core.RenderGraph` for graph values.

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
  `IRenderWindowFrameSession`, `IRenderGraphFactory`, `WindowConfiguration`,
  `WindowMetrics` and `RenderFrameState`.
- **Frame submission:** borrowed `RenderFramePacket`, `UiDrawList`, text draw
  values, dirty records and the `BeginFrame`/`EndFrame` session boundary.
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

`IRenderWindowFrameSession` also implements `IRenderGraphFactory`; its single
`CreateRenderGraph()` method returns a graph using the same session device,
queues and lifetime. Consumers do not access raw Vulkan handles.

`RenderGraphFrame` contains frame identity and one or more `RenderView` values.
Each view owns a surface handle, viewport and pixel-space `PixelRect`, so a
frame may target multiple surfaces. No Render contract carries `DeltaTime` or
defines a clock.

`RenderFramePacket` is a borrowed, one-frame submission value for the existing
window session. Its spans remain valid until `EndFrame` returns. The packet is
clear-only when pipelines and draw lists are empty. `SubmitFrame` is only a
convenience extension that performs one `BeginFrame` followed by one
`EndFrame`; it is not a second submission model.

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
