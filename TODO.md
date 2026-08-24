# DeltaRender TODO

Ordered cross-project ownership and gates are in
[../HIGH_PRIORITY_TODO.md](../HIGH_PRIORITY_TODO.md).

## P0 — shared graphics contract

- Remove the local duplicate `GraphicsShaderProgram` after
  `Delta.Shader.Abstractions` is the canonical owner; restore a clean
  DeltaRender -> DeltaEngine build without adapter copies.

## P1 — canonical UI/text submission

- Keep `UiRenderBatchAdapter -> borrowed UiRenderBatch` as the production
  renderer handoff; migrate remaining Engine/Editor consumers and then remove
  `UiQuad` compatibility paths.
- Consume DeltaText positioned glyph/bitmap data, own atlas packing/upload,
  UVs, GPU pages and compact glyph instances.
- Group draws by pipeline, atlas page and clip; never draw or allocate per glyph.
- Verify grayscale SDF and MSDF, atlas growth, partial clips, page disposal and
  two DPI scales through contract tests and a bounded MoltenVK smoke.
- Preserve owner/generation, anchor, clip/order and dirty generation without
  importing XAML/ECS types or raw storage handles.

## P2 — frame surface cleanup

- After consumer migration, keep one frame submission path and separate
  lifecycle, pipeline creation and uploads into small contracts. Name copying
  adapters and borrowed views distinctly.

Shared acceptance lives in [../EDITOR_UI_TODO.md](../EDITOR_UI_TODO.md).
