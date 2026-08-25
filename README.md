# DeltaRender

Standalone Vulkan renderer for game output, editor chrome, viewports and runtime
UI. SDL3-CS owns windows/surfaces, Silk.NET owns Vulkan, and macOS uses MoltenVK.

Project boundaries:

- `Delta.Render.Core` contains renderer-facing draw/resource contracts;
- `Delta.Render.Vulkan` owns Vulkan resources, pipelines and presentation;
- `Delta.Render.Platform.SDL3` owns platform window/surface integration;
- DeltaEngine owns frame timing, event polling and input translation;
- DeltaXAML owns retained UI/layout; DeltaText owns shaping/glyph pixels;
- The final shader boundary is `Delta.Shader.Contract.IShaderArtifact` /
  `IGraphicsShaderProgram`: SPIR-V plus the resolved binary `ShaderAbi` only.
  The current renderer APIs still accept the older
  `Delta.Shader.Abstractions.ShaderArtifact` compatibility model while that
  consumer migration is in progress.

Renderer-facing frame contracts contain frame identity, views, surfaces and
render data only. They never carry `DeltaTime` or define a clock. Simulation,
fixed-step, scaled, unscaled and editor time domains belong to DeltaEngine and
are converted into explicit feature or shader data before render submission.
The next canonical submission boundary is specified by the
[`RenderGraph` contracts](src/Delta.Render.Core/RenderGraph/README.md); the
existing specialized frame APIs remain migration paths until their consumers
move to it.

Compute supports validated artifacts, multiple SSBO bindings, upload, dispatch
and readback. Graphics supports paired vertex/fragment artifacts, swapchain
presentation, dynamic viewport/scissor, alpha blending, fullscreen and mesh
draws, push constants and UI quads.

Text contracts describe atlas pages, positioned glyph instances and batches.
The renderer owns images, views, samplers, descriptors, staging and disposal;
callers own strings, shaping, controls and reusable instance storage. The
submission contract is designed for batches by pipeline, atlas page and clip,
never one draw per glyph. Text-instance buffer growth is failure-atomic: the
previous allocation is released exactly once, a failed replacement leaves no
dangling owned handle, and retry/final disposal remain safe. Completion of the
larger text handoff is tracked in `TODO.md`.

Window-session construction is failure-atomic. Session-local Vulkan handles
remain in an acquisition ledger until every stage and the surface ownership
transfer succeed; failures release acquired resources in reverse order. The
surface is always released exactly once through the same platform source that
created it, rather than through a second raw-handle destruction path.

The canonical renderer-facing UI boundary is
`DeltaXAML IUiDrawList -> UiRenderBatchAdapter -> borrowed UiRenderBatch`.
Legacy `EngineUiQuad`/direct `UiQuad` conversions are migration/test-only, not
the production contract. DeltaRender no longer defines a local
`GraphicsShaderProgram`, but its current public pipeline methods still use the
shared `Delta.Shader.Abstractions.GraphicsShaderProgram` compatibility type.
They have not yet migrated to the canonical `Delta.Shader.Contract`
interfaces. GLSL, Roslyn/compiler state, live generic values and a content hash
are not part of the runtime artifact; renderer cache keys are consumer-owned.

`UiRenderBatchAdapter` copies draw, clip-node, text-submission and dirty-record
structures into reusable adapter-owned backing storage. The canonical replace
path preserves draw kind, resource handle, effective clip and clip identity,
parent clip hierarchy, owner/order metadata and command/clip/text dirty ranges
with their base/next versions. Nested `TextRun.Glyphs` and
`RenderRecordChange.Payload` memory remains borrowed from the producer for the
current frame; it must remain valid through submission and must not be retained
after the next adapter replace or dispose operation. Borrowed frame tokens are
generation-checked and become stale after either operation. Resource metadata
preservation does not yet imply sampled-image rendering by the Vulkan UI path.

Desktop targets are Windows/Linux Vulkan and macOS arm64/x64 through MoltenVK
portability enumeration. Native macOS runs require an explicit RID.

See [WORKFLOW.md](WORKFLOW.md) for build and smoke commands,
[TODO.md](TODO.md) for selected work, the [platform ADR](docs/adr/0001-vulkan-sdl3-moltenvk-stack.md)
for durable choices and [AGENTS.md](AGENTS.md) for routing.
