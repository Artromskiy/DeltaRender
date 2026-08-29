# DeltaRender TODO

The authoritative public surface is [docs/CONTRACT.md](docs/CONTRACT.md). The ordered
implementation/removal plan is [docs/MIGRATION.md](docs/MIGRATION.md). Do not redesign
the contract or add a compatibility facade while executing this list.

## Detailed status by micro-task

The high-level checkboxes below remain acceptance gates. This section records
which implementation pieces already exist and which pieces still need a
cross-project proof, so a completed subtask is not mistaken for a completed
milestone.

### P0 - session and graph foundation

Purpose: every producer must share one owner for native resources, scheduling
and synchronization instead of implementing a private submission path.

- [x] `VulkanRenderSession` exists for compute-only, offscreen and windowed
  sessions. Shared ownership is the reason this is one session type.
- [x] Session capabilities, target handles, persistent buffer/texture/sampler
  creation, generation-checked release and resize exist. Features therefore
  pass opaque handles rather than raw Vulkan objects.
- [x] Surface/session cleanup has idempotent and rollback paths. This keeps a
  failed session from double-destroying its surface or leaking partial state.
- [x] Graph `Build`/`Execute`, dependency ordering, barriers, layout changes,
  staging upload and explicit readback exist. These are the common execution
  primitives used by text, UI and compute features.
- [ ] Transient allocation reuse and pipeline-cache reuse still need bounded
  evidence and any remaining implementation work. Do not claim this as a
  performance guarantee yet.
- [ ] Full compute/offscreen/windowed acceptance still needs a single recorded
  gate; targeted tests alone do not prove all three modes.

### P1 - producer migration

Purpose: producers describe work as graph features so ordering, barriers and
resource lifetime are decided in one place.

- [ ] Maths conformance must use transfer -> compute -> readback graph passes;
  the CPU bundle and ShaderAbi remain producer-owned.
- [ ] Fullscreen and mesh samples must use raster graph passes and a
  session-owned target, not synthetic surfaces.
- [x] `DeltaRender.Text.TextRenderFeature` now emits ordinary transfer + raster
  graph passes for its bounded text slice.
- [ ] DeltaRender.XAML must consume the text feature through a neutral adapter
  without adding another frame packet or input-polling owner.

### P1 - DeltaRender.Text: reusable implementation slice

Purpose: keep shaping in DeltaText and make atlas/packing/upload/batching
reusable by UI or another feature without copying producer or shader ABI types.

- [x] `DeltaRender.Text` references only `Delta.Render` and `DeltaText`; it has
  no DeltaXAML, DeltaEngine or DeltaECS dependency.
- [x] `TextRenderFeature.AddRun(ShapedText, originX, originY, Vector4, PixelRect)`
  accepts positioned producer data, never strings, and consumes it during graph
  build while the caller-owned shaped value remains valid through `Execute`.
- [x] Atlas image, sampler and instance buffer are created through
  `IRenderFrameSession`, imported into each graph and released idempotently by
  the feature. This keeps native lifetime in the session.
- [x] Glyph cache identity includes `FontInstanceId` and generation, glyph ID,
  pixels-per-em, mode, encoding, distance range, color palette and padding.
  This prevents reuse across different raster requests.
- [x] Plane bounds, glyph offsets and run advances are preserved in compact
  instances; fractional positions are not rounded in the adapter.
- [x] Coverage/SDF R8, MSDF source data and premultiplied-sRGB color data use
  distinct atlas formats. Descriptor bindings are discovered from the supplied
  canonical ShaderAbi manifest.
- [x] A reusable grow-only instance array and adjacent clip batching produce
  one transfer pass and one raster pass, with instanced draws rather than one
  draw per glyph.
- [x] Effective clips are intersected with the current viewport, and the
  feature exposes a resize update without rebuilding the atlas.

### P1 - DeltaRender.Text: acceptance still open

Purpose: these items are required before the text path can claim complete
DeltaXAML/editor integration rather than only a reusable Render-side feature.

- [ ] Add the synchronous borrowed `UiDisplayList` -> feature storage adapter
  outside this project. It must flatten effective clips while the `ref struct`
  borrow is valid and must not pass XAML types into Render.Text.
- [ ] Define device-loss/reinitialization and transactional multi-page atlas
  replacement. The current implementation intentionally has one bounded page
  and fails deterministically when it is full.
- [ ] Obtain an approved producer identity/delta contract. Frozen `UiTextDraw`
  has no Owner, OwnerGeneration or Version; object references and hashes are not
  valid substitutes. Current fallback is full instance re-encoding.
- [ ] Resolve mixed visual/text ordering. Separate `Visuals` and `Text` spans
  cannot prove a general A-B-A order; the canonical producer contract must
  provide or constrain that ordering.
- [ ] Add bounded headless tests for first upload, cache hit without upload,
  format isolation, UV/plane metrics, clip/order/lifetime behavior, resize and
  warm-frame allocations. Existing Render tests do not cover TextRenderFeature.
- [ ] Add a bounded native text smoke only after the producer adapter and final
  shader artifact are available. A skipped native run is not a pass.

### P2 - legacy removal gate

Purpose: remove competing ownership only after active producers are migrated;
otherwise deletion would turn an incomplete migration into a broken build.

- [ ] Remove standalone compute device/storage/pipeline implementations after
  Maths conformance is graph-only.
- [ ] Remove direct frame state/packet and begin/end/submit implementations
  after all consumers use `IRenderGraph.Build/Execute`.
- [ ] Remove public pipeline/text-atlas factories and old UI/text packet models
  after their graph replacements are active.
- [ ] Remove obsolete tests, samples and docs only after replacement paths and
  migration search are clean.

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
