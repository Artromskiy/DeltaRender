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
- [x] Session-owned descriptor-keyed transient pools and pipeline-cache reuse
  are implemented; headless tests cover warm hits, descriptor isolation,
  double-return rejection and drain ownership. This is reuse/lifetime evidence,
  not a performance guarantee or native timing result.

### P1 - producer migration

Purpose: producers describe work as graph features so ordering, barriers and
resource lifetime are decided in one place.

- [x] `tools/DeltaRender.MathConformance/Program.cs` uses the graph-only
  transfer -> compute -> readback path: `CreateComputeSession`,
  `CreateRenderGraph`, `GraphConformanceFeature` with an upload transfer pass,
  an `IComputePass`, `ReadbackBuffer`, then `Build` -> `Execute` ->
  `CopyReadback` and CPU comparison. The CPU bundle and `ShaderAbi` remain
  producer-owned; this is source/headless evidence, not a completed GPU gate.
- [x] Existing fullscreen samples (`ShaderSandbox`, `HeadlessShaderPlayground`
  and the fullscreen smoke mode) use raster graph passes with a
  session-owned `session.Target`; their feature/pass state and feature span are
  created before the frame loop. Headless artifact/probe tests provide the
  non-window evidence for this route.
- [x] `samples/DeltaRender.MeshSample` uses the raster graph/session-target path:
  session-owned vertex/index buffers are uploaded by a transfer pass and drawn
  by one indexed raster pass. The checked-in descriptor-free mesh pair is
  loaded as canonical `ShaderArtifact` data; native execution remains a
  separate evidence gate.
- [x] `DeltaRender.Text.TextRenderFeature` now emits ordinary transfer + raster
  graph passes for its bounded text slice.
- [x] DeltaRender.XAML consumes the text feature through a neutral adapter
  without adding another frame packet or input-polling owner.

### P1 - DeltaRender.XAML UI display-list adapter

This is the selected consumer slice for the `DeltaXAML.Contract` v0.0.8 paint
and clip data. It belongs in `src/DeltaRender.XAML/`, not in the core Render
contract and not in DeltaXAML.

- [x] Create `UiDisplayListGraphFeature : IRenderFeature, IDisposable` with a
  synchronous `Consume(UiDisplayList)` followed by normal `AddPasses`.
- [x] Walk only `Order`, validate visual/text indices and resolve nested clip
  parents to viewport-intersected rectangles without losing draw order.
- [x] Route shaped text and paint data to the reusable text adapter; keep
  `DeltaRender.Text` independent from XAML and do not duplicate DeltaText
  values or ABI types.
- [x] Add adapter-owned resource/type registration for `UiResourceId` and
  `UiVisualTypeId`; missing, stale or foreign entries must be diagnostics.
- [x] Keep reusable clip/order storage and batch adjacent compatible text
  commands; preserve mixed visual/text `A-B-A` order through one transfer stage
  and one raster pass per contiguous text segment or visual command. GPU
  material/dirty upload reuse remains a separate shader/resource milestone.
- [x] Pack solid/rounded/border rectangle bounds, fill, stroke, corner radius
  and resolution through generated `DeltaShader.UI` typed helpers, using the
  producer's cached ABI accessors and reusable feature-owned storage. A
  mismatched or unknown program is rejected with a deterministic diagnostic.
- [x] Support the current solid/rounded/text path when the matching generated
  rectangle or text artifact is supplied. Image, gradient and non-rectangular
  clip values are tracked as explicit renderer gaps below and must not
  silently fall back.
- [x] Headless evidence for borrowed lifetime, paint-only updates, registry cache
  hits and zero-allocation unchanged frames is covered. Tests include synchronous
  order copy, nested rectangular clips, deterministic cycle diagnostics,
  unsupported paint rejection, resource registration and rejection of borrowed
  frame access after feature disposal. Native submission remains separate
  acceptance work.

#### Producer values not yet rendered by the current adapter

This audit compares the values emitted by `DeltaXAML` with the actual branches in
`src/DeltaRender.XAML/`. The entries below are renderer-owned gaps, not requests
to duplicate XAML state or silently fall back to a solid rectangle.

- [x] Solid rectangles, rounded rectangles and local border/stroke data are
  accepted by the matching generated `DeltaShader.UI` artifacts. Independent
  corner radii are preserved in the packed instance payload; the rounded-slice
  path is covered by headless readback evidence.
- [ ] Complete `UiVisualKind.Image`: `DeltaXAML` emits an image resource identity
  and the registry can import its texture, but `UiVisualShaderContract` has no
  image ABI classifier, so `AddPasses` currently diagnoses the visual as an
  unsupported kind before recording it. Add a generated image artifact mapping,
  typed payload packing and ordered texture binding.
- [ ] Complete custom and gradient visuals: `UiBrush` emits stable linear/radial
  gradient identities through `UiVisualKind.Custom`, while the registry can store
  a program; the current classifier still rejects `Custom` and does not pack
  gradient parameters or resolve gradient resources. Add explicit generated
  artifact/resource mappings with diagnostics for incompatible payloads.
- [ ] Render rounded clips. `DeltaXAML` can emit `UiClipKind.RoundedRectangle`
  with four radii and parent links, but `UiClipResolver` currently accepts only
  rectangular regions and reports rounded clips as unsupported. Add the
  renderer-owned stencil/mask/analytic path while preserving nested clip order.
- [ ] Complete text paint submission. `DeltaXAML` emits outline color/width and
  a text-effect resource identity, but `ValidateText` rejects non-zero effect
  data and `TextShaderPacking` always packs zero outline parameters. Add a
  matching generated text-effect artifact/resource path; do not ignore these
  fields or substitute another shader.
- [ ] Close native text pixel evidence for the XAML adapter. Graph construction,
  atlas allocation and cache/version forwarding are covered, but the latest
  2048/Snake headless evidence still records text commands without non-clear
  text pixels. Keep this as an end-to-end shader/atlas/readback gate until a
  shaped text sample changes the target pixels.
- [ ] Coordinate the text metadata boundary. `DeltaXAML` retains wrapping,
  trimming, line-height, weight, style and decorations, while frozen
  `UiTextDraw` exposes only shaped text, baseline and `UiTextPaint`. The
  renderer cannot apply those semantics until the producer/contract owners
  publish an approved neutral extension; it must not infer them from strings.

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

### P1 - DeltaRender.Text: remaining integration work

Purpose: these items are required before the text path can claim complete
DeltaXAML/editor integration rather than only a reusable Render-side feature.

- [x] Add the synchronous borrowed `UiDisplayList` -> feature storage adapter
  in `src/DeltaRender.XAML/`. It flattens effective clips while the `ref struct`
  borrow is valid and passes only shaped text and neutral values into
  `DeltaRender.Text`.
- [x] Implement transactional bounded multi-page atlas allocation and page-level
  LRU recycling. Recycled pages invalidate their cached placements, retain their
  session-owned texture handles and return to the dirty upload set.
- [x] Define device-loss/reinitialization and atlas replacement. The explicit
  `IRenderFrameSession.TryReinitializeAfterDeviceLoss` boundary invalidates
  graph/persistent device resources, preserves a window surface, recreates
  session-local Vulkan state, and requires producers to re-upload resources.
- [x] Consume the approved producer identity/delta contract. The XAML adapter
  forwards `UiTextRunId.Value/Generation` and `UiTextDraw.Version` into the
  neutral text feature; matching runs reuse packed instances, while the legacy
  identity-less `AddRun` path remains an explicit full-encoding fallback.
- [x] Resolve mixed visual/text ordering through the canonical `Order` span;
  the XAML adapter preserves arbitrary visual/text interleaving while the
  text feature batches only adjacent text entries.
- [x] Add bounded headless tests for first upload, cache hit without upload,
  format isolation, UV/plane metrics, clip/order/lifetime behavior, resize and
  feature-level warm-frame allocations. Existing tests cover first upload,
  cache-hit without upload, changed payload upload, resize reuse, atlas format
  selection, packed UV/plane metrics, page allocation/recycling, page-aware
  upload/binding and allocation failure cleanup. New tests cover
  viewport-intersected effective clips, compatible adjacent batching with
  non-adjacent `A-B-A` clip order, and the `Clear` borrowed-run boundary. After
  reusable cache-hit warm-up, `PrepareComposite`, composite recording and
  `Clear` allocate zero bytes, and unchanged frames register no transfer pass.
  The feature-owned raster description is cached; Core/native graph allocation
  evidence remains open. Nested clip hierarchy is resolved by the XAML adapter
  before this feature receives its effective clip.

### P2 - legacy removal gate

Purpose: remove competing ownership only after active producers are migrated;
otherwise deletion would turn an incomplete migration into a broken build.

- [x] Remove standalone compute device/storage/pipeline implementations. The
  migration search found no active implementation or caller; Maths conformance
  uses the graph-only path.
- [x] Remove direct frame state/packet and begin/end/submit implementations. No
  active source, sample or tool uses those entry points.
- [x] Remove public pipeline/text-atlas factories and old UI/text packet models.
  No active symbols remain; current UI and text features submit graph passes.
- [x] Remove obsolete tests, samples and docs after replacement paths are
  active. Remaining migration wording is retained as plan/history, not an
  active API reference.

## Remaining project migration gates

The detailed status sections above are authoritative; this section keeps only
the still-open project-level gates and avoids repeating completed work.

- [x] Add transient allocation reuse and pipeline-cache reuse with bounded
  headless evidence; native resource lifetime remains session-owned. Native
  allocation timing is not claimed without a Vulkan run.
- [x] Introduce a graph-first mesh sample. `DeltaRender.MeshSample` uses
  `IRenderFrameSession -> CreateRenderGraph -> AddTransferPass/AddRasterPass`,
  `session.Target`, persistent session buffers, and `DrawIndexed`; it does not
  use a synthetic surface or direct Vulkan submission. A native render/readback
  run remains open because this bounded slice does not execute GPU tests.
- [x] Implement bounded multi-page atlas allocation/recycling for
  `DeltaRender.Text`; device-loss recovery invalidates pages and requires
  producer-owned atlas data to be uploaded again after reinitialization.
- [x] Consume the approved producer identity/delta path for incremental text
  updates. Matching identity/version and unchanged placement/paint/clip reuse
  cached packed instances; version, generation, payload or atlas recycling
  invalidates that cache. Identity-less callers retain full re-encoding.
- [x] Complete feature-level text warm-frame allocation evidence. After two
  reusable cache-hit warm-up cycles, `PrepareComposite`, composite recording
  and `Clear` allocate zero bytes, and unchanged frames register no transfer
  pass. Nested clip hierarchy is resolved by the XAML adapter; ordering and
  page recycling have bounded evidence. Core/native graph allocation evidence
  remains open.
- [x] Remove superseded compute/frame/pipeline/text packet implementations,
  tests and docs. The detailed P2 gate above records no active legacy symbols;
  remaining migration wording is plan/history, not an active API reference.

## Deferred

- multiple targets in one session;
- multiple Vulkan queues and queue-family ownership transfers;
- async readback convenience APIs;
- graph performance specialization without bounded evidence.
