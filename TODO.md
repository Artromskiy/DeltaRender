# DeltaRender TODO

## P0 — shared graphics contract

- Remove the local duplicate `GraphicsShaderProgram` after
  `Delta.Shader.Abstractions` is the canonical owner; restore a clean
  DeltaRender -> DeltaEngine build without adapter copies.

## P1 — text submission

- Consume generated SDF/MSDF manifests and submit compact glyph instances from
  the shared atlas service.
- Group draws by pipeline, atlas page and clip; never draw or allocate per glyph.
- Verify grayscale SDF and MSDF, atlas growth, partial clips, page disposal and
  two DPI scales through contract tests and a bounded MoltenVK smoke.
- Add the ECS/XAML-neutral submission seam: owner kind + generation, screen/world
  anchor, positioned glyph run, clip/order and dirty generation. The current
  core slice resolves anchors and feeds the existing allocation-free batching;
  producer adapters remain outside Render.

Shared acceptance lives in [../EDITOR_UI_TODO.md](../EDITOR_UI_TODO.md).
