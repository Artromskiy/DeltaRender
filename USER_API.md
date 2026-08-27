# DeltaRender user API

This file is the user-facing API summary. The complete cross-project contract
is [CONTRACT.md](CONTRACT.md); implementation details belong in
`INTERNAL.md`.

## Shader input

Consumers provide `DeltaShader.Contract.IShaderArtifact` and
`IGraphicsShaderProgram`. An artifact owns validated SPIR-V bytes, its emitted
entry point and the resolved `ShaderAbi`. DeltaRender consumes that contract; it
does not compile C# or GLSL and does not define a second ABI.

## Window and render-graph session

`IRenderWindowFactory.CreateWindow` creates an `IRenderWindow`. The platform
provider supplies the Vulkan instance extensions and surface operations.
`IRenderFrameSession` exposes the graph entry point:

```csharp
IRenderGraph CreateRenderGraph();
```

The consumer creates a `RenderGraphFrame`, lets features declare their passes
and resources, then builds and executes the graph:

```csharp
IRenderGraph graph = session.CreateRenderGraph();
graph.Build(in frame, features);
graph.Execute();
```

`Execute()` owns target acquisition, pass ordering, synchronization, command
submission and presentation. The session does not poll events or own
input/game-loop state. Resize is handled by the session outside an active graph
execution.

## UI handoff

`UiRenderBatchAdapter` is the copying boundary for renderer-neutral UI records.
Its borrowed `UiRenderBatch` preserves rectangles, resource handles,
owner/generation/order, clip identity and hierarchy, text submissions and dirty
version ranges. `IUiRenderFrameSource.BorrowFrame()` returns one borrowed batch
and the matching borrowed atlas-page span. The view expires after the next
replace/prepare/cache mutation or dispose.

The renderer-facing batch contains no ECS storage and no retained-XAML object.
An integration adapter converts `UiDisplayList` and consumer-owned shaped text
into these records before the one frame submission.

## Text input

`DeltaRender.Text.TextAtlasCache` consumes immutable DeltaText `GlyphImage`
values and copies their pixels into renderer-owned pages. Shaping is supplied
as DeltaText `ShapedGlyph` values; Render does not accept strings or perform
shaping. `TextGlyphPlacement.ToInstance` accepts the caller's fractional
baseline position and applies the final pixel conversion only when producing a
`TextGlyphInstance`.

`ITextAtlasDevice` owns GPU page creation and dirty uploads. The consumer keeps
atlas-page borrows alive through the frame only. Text draws are grouped by
pipeline, atlas page and clip, with one instanced draw per group.

## Compute input

`IComputeDevice` accepts device-local storage buffers, staging uploads,
host-visible readback, canonical shader artifacts, dispatch dimensions and
dirty record ranges. The normal pipeline entry point is
`CreateComputePipeline(IShaderArtifact)`. The raw SPIR-V overload is an explicit
low-level import and accepts only the producer-owned `DeltaShader.Contract.ShaderAbi`;
there is no Render-owned compute metadata model. `IComputePipeline.Abi` exposes
that same canonical ABI. Storage layout is std430. Zero-sized dispatches are a
successful no-op; invalid ranges, foreign handles and disposed resources are
rejected.
