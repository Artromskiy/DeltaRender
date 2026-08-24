# DeltaRender TODO

Ordered cross-project ownership and gates are in
[../HIGH_PRIORITY_TODO.md](../HIGH_PRIORITY_TODO.md).

## P0 — shared graphics contract

- [x] Remove the local duplicate `GraphicsShaderProgram`; consume the canonical
  `Delta.Shader.Abstractions` owner without adapter copies.

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
- Make `VulkanWindowSession` construction transactional: if any native
  allocation fails before the session object is returned, release every
  render-pass, swapchain, image-view/framebuffer, synchronization and command
  resource already created. Cover the cleanup ordering through a headless
  fault-injection seam before relying on a native smoke.

Shared acceptance lives in [../EDITOR_UI_TODO.md](../EDITOR_UI_TODO.md).
