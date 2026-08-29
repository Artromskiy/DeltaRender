# DeltaRender TODO

The authoritative public surface is [docs/CONTRACT.md](docs/CONTRACT.md). The ordered
implementation/removal plan is [docs/MIGRATION.md](docs/MIGRATION.md). Do not redesign
the contract or add a compatibility facade while executing this list.

## P0 - one Vulkan session and graph executor

- [ ] Implement one internal `VulkanRenderSession` for compute-only, offscreen
  and windowed modes.
- [ ] Implement session capabilities, target, persistent buffers/textures/
  samplers, generation-checked release and target resize.
- [ ] Implement graph build storage, deterministic scheduling, transient
  lifetime reuse, barriers, cached pipelines, staging and explicit readback.
- [ ] Ensure ordinary execution never waits for queue idle; only
  `CopyReadback` may wait for its producing submission.

## P1 - migrate every producer

- [ ] Rewrite Maths conformance to transfer + compute + readback graph passes.
- [ ] Rewrite fullscreen and mesh samples as raster graph passes.
- [ ] Rewrite DeltaRender.Text atlas uploads and glyph draws as graph passes.
- [ ] Rewrite DeltaRender.XAML UI submission as graph features without a
  second frame packet.

## P1 - DeltaRender.Text integration acceptance

The current [DeltaRender.Text contract](docs/TEXT_CONTRACT.md) and
[internal design](docs/TEXT_INTERNAL.md) describe the first bounded
`TextRenderFeature` implementation and the remaining acceptance work. Complete
this slice without adding a second text or frame contract:

- [ ] Add a concrete submission path from borrowed
  `Delta.XAML.Contract.UiDisplayList` into reusable feature-owned storage;
  document the synchronous consume/copy lifetime because `UiDisplayList` is a
  `ref struct` and `IRenderFeature.AddPasses` has no frame-data parameter.
- [ ] Create persistent atlas pages, samplers and instance buffers through
  `IRenderFrameSession`, import them into each graph build, and define
  resize/device-loss/dispose behavior.
- [ ] Resolve the missing producer identity contract before claiming
  incremental text updates: the frozen `UiTextDraw` currently carries no
  XAML `Owner`, `OwnerGeneration` or text `Version`. Do not fabricate these
  from object references or hashes; either consume an approved producer delta
  or explicitly document full instance re-encoding as the current fallback.
- [ ] Preserve ordering across visual and text commands. Separate
  `UiDisplayList.Visuals` and `UiDisplayList.Text` spans do not encode a mixed
  order; do not claim general `A-B-A` preservation until the ordering semantics
  are resolved by the canonical contract or explicitly constrained.
- [ ] Include `FontInstanceId` plus generation, glyph ID, pixels-per-em,
  image mode/encoding, distance range, color palette and padding policy in the
  atlas key. Preserve `ShapedGlyph` offsets, advances, clusters and
  `GlyphImage.PlaneBounds` when encoding instances.
- [ ] Use format-specific shader paths and validation for Coverage/SDF R8,
  MSDF RGB and premultiplied-sRGB color glyphs; pass distance range and color
  semantics through the canonical `DeltaShader.Contract` artifact.
- [ ] Add bounded headless tests for first insert/upload, cache hit without
  upload, page-generation recycling, multi-page and nested-clip batches,
  mixed ordering, borrowed lifetime, and zero allocations after warm-up.

## P2 - delete legacy surface

- [ ] Remove standalone compute device/storage/pipeline implementations.
- [ ] Remove direct frame state/packet and begin/end/submit implementations.
- [ ] Remove public pipeline/text-atlas factories and renderer-owned UI/text
  packet models.
- [ ] Remove obsolete tests, samples and documentation after their graph
  replacements are active.

## Deferred

- multiple targets in one session;
- multiple Vulkan queues and queue-family ownership transfers;
- async readback convenience APIs;
- graph performance specialization without bounded evidence.
