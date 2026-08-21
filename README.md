# DeltaRender

Standalone Vulkan renderer for game output, editor chrome, viewports and runtime
UI. SDL3-CS owns windows/surfaces, Silk.NET owns Vulkan, and macOS uses MoltenVK.

Project boundaries:

- `Delta.Render.Core` contains renderer-facing draw/resource contracts;
- `Delta.Render.Vulkan` owns Vulkan resources, pipelines and presentation;
- `Delta.Render.Platform.SDL3` owns platform window/surface integration;
- DeltaEngine owns frame timing, event polling and input translation;
- DeltaXAML owns retained UI/layout; DeltaText owns shaping/glyph pixels;
- DeltaRender consumes `Delta.Shader.Abstractions.ShaderArtifact` and never
  parses C# or defines a second shader ABI.

Compute supports validated artifacts, multiple SSBO bindings, upload, dispatch
and readback. Graphics supports paired vertex/fragment artifacts, swapchain
presentation, dynamic viewport/scissor, alpha blending, fullscreen and mesh
draws, push constants and UI quads.

Text contracts describe atlas pages, positioned glyph instances and batches.
The renderer owns images, views, samplers, descriptors, staging and disposal;
callers own strings, shaping, controls and reusable instance storage. The
submission contract is designed for batches by pipeline, atlas page and clip,
never one draw per glyph; completion is tracked in `TODO.md`.

Desktop targets are Windows/Linux Vulkan and macOS arm64/x64 through MoltenVK
portability enumeration. Native macOS runs require an explicit RID.

See [WORKFLOW.md](WORKFLOW.md) for build and smoke commands,
[TODO.md](TODO.md) for selected work, the [platform ADR](docs/adr/0001-vulkan-sdl3-moltenvk-stack.md)
for durable choices and [AGENTS.md](AGENTS.md) for routing.
