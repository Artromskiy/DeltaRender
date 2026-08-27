# DeltaRender TODO

Ordered cross-project ownership and gates are in
[../HIGH_PRIORITY_TODO.md](../HIGH_PRIORITY_TODO.md).

## P0 - canonical contracts

- [x] Consume `DeltaShader.Contract.IShaderArtifact`, `ShaderAbi` and
  `IGraphicsShaderProgram` directly in the contract/Vulkan boundary; do not
  duplicate ABI DTOs.
- [x] Keep the complete Vulkan-only cross-project contract in `CONTRACT.md`
  and its flat `src/Contract` declarations, separate from
  allocation, barriers and command recording.
- [ ] Replace the transitional direct frame path with the graph-first
  `IRenderFrameSession`; graph execution must own acquire, submit and present.
  Remove the old packet/state/session surface from the public contract rather
  than wrapping it in another packet. Keep the replacement minimal, expose
  `CreateRenderGraph()` on the session, and migrate the Vulkan implementation
  in `DeltaRender.Vulkan` without touching neighboring project contracts.
- [ ] Implement RenderGraph scheduling in `DeltaRender.Vulkan` and migrate
  specialized fullscreen/UI/text/mesh submissions to graph passes.

## P1 - canonical UI/text submission

- [x] Keep `UiRenderBatchAdapter` as the renderer-owned copying boundary and
  preserve draw, clip, owner/order, generation and dirty-version data.
- [x] Keep `IUiRenderFrameSource` borrowed-lifetime semantics explicit without
  ECS or retained-UI dependencies in the renderer contract.
- [x] Adapt the canonical DeltaText `GlyphImage` and `ShapedGlyph` values in
  `DeltaRender.Text`; cache identity includes font instance, glyph, size,
  mode and distance range.
- [ ] Add a public DeltaText image fixture/factory consumer test, then verify
  atlas insertion, page rollover/recycle, dirty upload and disposal end to end.
- [ ] Verify grayscale SDF and MSDF presentation, atlas growth, partial clips,
  page disposal and two DPI scales through contract tests and bounded MoltenVK.
- [ ] Coordinate `UiDisplayList` adapter integration in the editor consumer;
  Render remains independent of DeltaXAML contract types.

## P2 - lifecycle and platform

- [x] Make `VulkanWindowSession` construction transactional with reverse-order
  rollback, source-backed surface ownership and partial-view cleanup.
- [ ] Implement the Vulkan RenderGraph runtime while preserving the current
  surface/session contract.

Shared acceptance lives in [../EDITOR_UI_TODO.md](../EDITOR_UI_TODO.md).
