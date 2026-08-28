# DeltaRender

Vulkan-only renderer for game output, editor chrome, viewports and runtime UI.
SDL3 supplies windows and Vulkan surfaces; MoltenVK is the macOS portability
layer. Neither is a second renderer backend.

## Boundary

DeltaRender supports one GPU execution path:

```text
IRenderFrameSession
  -> IRenderGraph.Build(features)
  -> transfer / compute / raster passes
  -> IRenderGraph.Execute()
  -> optional present or explicit readback
```

The same session and graph implementation serve windowed, offscreen and
compute-only work. Final shaders come from `DeltaShader.Contract`; XAML and
text adapters translate their producer-owned data into graph passes. Render
does not compile source, poll input, shape text or own ECS state.

## Navigation

- [CONTRACT.md](CONTRACT.md): authoritative cross-project contract.
- [USER_API.md](USER_API.md): graph authoring and resource usage.
- [INTERNAL.md](INTERNAL.md): Vulkan implementation design.
- [MIGRATION.md](MIGRATION.md): removal of every legacy submission path.
- [TODO.md](TODO.md): selected implementation work.
- [WORKFLOW.md](WORKFLOW.md): bounded local checks.
- [Vulkan/SDL3/MoltenVK ADR](docs/adr/0001-vulkan-sdl3-moltenvk-stack.md):
  platform decision.
