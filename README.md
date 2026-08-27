# DeltaRender

Standalone Vulkan renderer for game output, editor chrome, viewports and
runtime UI. SDL3-CS owns windows and surfaces, Silk.NET owns Vulkan, and macOS
uses MoltenVK.

## Boundaries

- `DeltaRender` contains platform-neutral renderer contracts and the
  graph-first frame boundary.
- `DeltaRender.Vulkan` owns Vulkan resources, pipelines and presentation.
- `DeltaRender.Platform.SDL3` owns SDL window and surface integration.
- `DeltaRender.Text` adapts DeltaText's immutable glyph-image and shaping
  values to renderer-owned atlas storage.
- DeltaEngine owns timing, event polling and input; DeltaXAML owns layout and
  produces `UiDisplayList`; DeltaText owns shaping and rasterization.
- Shader artifacts come from `DeltaShader.Contract` as SPIR-V plus resolved
  `ShaderAbi`; the renderer does not compile source or duplicate shader ABI.
- Compute pipeline creation consumes that artifact directly; raw SPIR-V import
  is a low-level path that accepts the same canonical `ShaderAbi`.

## Navigation

- [USER_API.md](USER_API.md): stable user-facing renderer contracts.
- [INTERNAL.md](INTERNAL.md): Render-owned implementation boundaries.
- [WORKFLOW.md](WORKFLOW.md): bounded build, test, format and metrics commands.
- [TODO.md](TODO.md): selected project work only.
- [CONTRACT.md](CONTRACT.md): the complete Vulkan-only cross-project renderer
  contract, including RenderGraph, frame, resource, UI/text and compute APIs.
- [Vulkan/SDL3/MoltenVK ADR](docs/adr/0001-vulkan-sdl3-moltenvk-stack.md):
  platform and package decisions.

The renderer never owns the event loop. A frame session creates a graph;
features declare passes and resources, and `graph.Execute()` owns acquire,
submission and presentation. Compute and graphics preserve their existing
SSBO, dispatch, swapchain and presentation paths. UI/text batches are grouped
by pipeline, atlas page and clip.
