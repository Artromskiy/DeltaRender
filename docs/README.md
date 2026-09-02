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

## Packages

The `0.0.14` release is published as one aligned package set:

| Package | Required first-party packages |
|---|---|
| `DeltaRender` | `DeltaShader.Contract 0.0.16` |
| `DeltaRender.Vulkan` | `DeltaRender 0.0.14`, `DeltaShader.Contract 0.0.16` |
| `DeltaRender.Platform.SDL3` | `DeltaRender 0.0.14` |

Use matching DeltaRender package versions. The Vulkan and SDL3 packages are
implementation layers over the renderer-neutral `DeltaRender` contract.

`DeltaRender.Text` and `DeltaRender.XAML` are repository-internal source
adapters, not public NuGet packages. They consume the source-only generated
`DeltaShader.Text` and `DeltaShader.UI` assemblies respectively, so publishing
either adapter before those producer assemblies have runtime packages would
create an incomplete dependency graph. Cross-repository samples consume the
published base renderer packages and retain source `ProjectReference` entries
only for these adapters and their source-only shader producers.

## Navigation

- [CONTRACT.md](CONTRACT.md): authoritative cross-project contract.
- [USER_API.md](USER_API.md): graph authoring and resource usage.
- [INTERNAL.md](INTERNAL.md): Vulkan implementation design.
- [TEXT_CONTRACT.md](TEXT_CONTRACT.md): DeltaRender.Text renderer integration boundary.
- [TEXT_INTERNAL.md](TEXT_INTERNAL.md): DeltaRender.Text ownership and implementation details.
- [RENDER_BATCHING.md](RENDER_BATCHING.md): renderer-owned instance batching,
  ordering and dirty-upload behavior.
- [MIGRATION.md](MIGRATION.md): removal of every legacy submission path.
- [TODO.md](../TODO.md): selected implementation work.
- [WORKFLOW.md](../WORKFLOW.md): bounded local checks.
- [Vulkan/SDL3/MoltenVK ADR](adr/0001-vulkan-sdl3-moltenvk-stack.md):
  platform decision.
