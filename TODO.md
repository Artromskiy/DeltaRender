# DeltaRender TODO

Ordered cross-project ownership and gates are in
[../HIGH_PRIORITY_TODO.md](../HIGH_PRIORITY_TODO.md).

## P0 - canonical contracts

- [x] Consume `Delta.Shader.Contract.IShaderArtifact`, `ShaderAbi` and
  `IGraphicsShaderProgram` directly in Core/Vulkan; do not duplicate ABI DTOs.
- [x] Keep the Vulkan-only RenderGraph contract separate from allocation,
  barriers and command recording.
- [x] Keep the frame session to `BeginFrame` plus canonical packet `EndFrame`;
  `SubmitFrame` is an extension that sequences that pair once.
- [ ] Implement RenderGraph scheduling in `Delta.Render.Vulkan` and migrate
  specialized fullscreen/UI/text/mesh submissions to graph passes.

## P1 - canonical UI/text submission

- [x] Keep `UiRenderBatchAdapter` as the renderer-owned copying boundary and
  preserve draw, clip, owner/order, generation and dirty-version data.
- [x] Keep `RenderFramePacket` and `IUiRenderFrameSource` borrowed-lifetime
  semantics explicit without ECS or retained-UI dependencies in Core.
- [x] Adapt the canonical DeltaText `GlyphImage` and `ShapedGlyph` values in
  `Delta.Render.Text`; cache identity includes font instance, glyph, size,
  mode and distance range.
- [ ] Add a public DeltaText image fixture/factory consumer test, then verify
  atlas insertion, page rollover/recycle, dirty upload and disposal end to end.
- [ ] Verify grayscale SDF and MSDF presentation, atlas growth, partial clips,
  page disposal and two DPI scales through contract tests and bounded MoltenVK.
- [ ] Coordinate `UiDisplayList` adapter integration in the editor consumer;
  Render remains independent of Delta.XAML contract types.

## P2 - lifecycle and platform

- [x] Make `VulkanWindowSession` construction transactional with reverse-order
  rollback, source-backed surface ownership and partial-view cleanup.
- [ ] Implement the Vulkan RenderGraph runtime while preserving the current
  surface/session contract.

Shared acceptance lives in [../EDITOR_UI_TODO.md](../EDITOR_UI_TODO.md).
