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
- [x] CI runs `eng/check-package-boundaries.sh` before restore/build. The gate
  keeps the three published Render packages aligned and floating, rejects
  source consumption of those assemblies, and preserves `DeltaRender.Text` and
  `DeltaRender.XAML` as non-packable source adapters with explicit producer
  edges.

### P1 - DeltaRender.XAML UI display-list adapter

This is the selected consumer slice for the frozen `DeltaXAML.Contract` paint
and clip data. The contract version is owned by its project metadata and is
intentionally not duplicated here. The slice belongs in
`src/DeltaRender.XAML/`, not in the core Render contract and not in DeltaXAML.

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
  and resolution through generated `DeltaRender.UIShaders` typed helpers, using the
  producer's cached ABI accessors and reusable feature-owned storage. A
  mismatched or unknown program is rejected with a deterministic diagnostic.
- [x] Support the current solid/rounded/text path when the matching generated
  rectangle or text artifact is supplied. Image and non-rectangular clip
  values are tracked as explicit renderer gaps below and must not
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
  accepted by the matching generated `DeltaRender.UIShaders` artifacts. Independent
  corner radii are preserved in the packed instance payload; the rounded-slice
  path and registered visual cached-mask path are covered by headless evidence.
- [ ] Complete `UiVisualKind.Image`: `DeltaXAML` emits an image resource identity
  and the registry can import its texture, but `UiVisualShaderContract` has no
  image ABI classifier, so `AddPasses` currently diagnoses the visual as an
  unsupported kind before recording it. Add a generated image artifact mapping,
  typed payload packing and ordered texture binding.
- [ ] Complete arbitrary custom visuals: the registry can store a program, but
  the adapter still requires an explicit generated artifact/resource mapping
  and diagnostics for incompatible payloads. Canonical linear/radial gradients
  are handled by the dedicated registered resource path above.
- [ ] Render rounded clips. `DeltaXAML` can emit `UiClipKind.RoundedRectangle`
  with four radii and parent links, but `UiClipResolver` currently accepts only
  rectangular regions and reports rounded clips as unsupported. Add the
  renderer-owned stencil/mask/analytic path while preserving nested clip order.
- [x] Complete text paint submission for the current generated text-effect path.
  Registered outline/glow/shadow resources are routed to the matching text shader
  variant, and `UiEffectParameters.Units` is converted at the Render boundary so
  logical effect geometry is scaled while device geometry is preserved. Text
  CachedMask remains a separate explicitly unsupported path.
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

## Performance evidence

The 10,000-frame headless Snake workload was measured with frames 1,001-10,000
included (9,000 samples), 16 simulated frame slots, and no per-frame report
printing. The profile tool used the same `MAD x 6` filter with `IQR x 1.5` as a
fallback; `median-error` is the robust standard-error estimate for the sample
median, while `MAD` is the observed deviation. The raw and summary files are:

- baseline: `/tmp/delta-snake-profile-10000.raw.tsv` and
  `/tmp/delta-snake-profile-10000.summary.json`;
- topology-cache experiment: `/tmp/delta-snake-profile-10000-topology.raw.tsv`
  and `/tmp/delta-snake-profile-10000-topology.summary.json`.

The experiment enabled the existing internal `VulkanRenderGraph` topology cache
without changing its public API. It was rejected: GPU pass median moved from
`185.us` to `180.us` (-2.59%), but build moved from `17.2us` to `17.6us`
(+2.17%), record from `15.0us` to `15.3us` (+1.94%), submit/present from
`137.us` to `141.us` (+2.52%), and layout/shaping from `40.0us` to `42.0us`
(+5.00%). The measured host composition (build + acquire + record +
submit/present + fence wait + layout/shaping) moved from `213.us` to `219.us`
(+2.96%), so this is not an accepted optimization. The flag remains disabled.

| metric | baseline median / error / MAD | experiment median / error / MAD |
| --- | --- | --- |
| acquire | `3.17us / 10.7ns / 541ns` | `3.33us / 11.6ns / 583ns` |
| build | `17.2us / 10.3ns / 501ns` | `17.6us / 17.4ns / 875ns` |
| fence wait | `167ns / 0.835ns / 42.0ns` | `167ns / 0.843ns / 42.0ns` |
| layout/shaping | `40.0us / 46.1ns / 2.30us` | `42.0us / 58.0ns / 2.90us` |
| record | `15.0us / 8.65ns / 417ns` | `15.3us / 13.4ns / 666ns` |
| submit/present | `137.us / 145ns / 7.33us` | `141.us / 165ns / 8.38us` |
| pass CPU | `14.5us / 7.80ns / 376ns` | `14.7us / 11.0ns / 545ns` |
| pass GPU | `185.us / 140ns / 6.58us` | `180.us / 164ns / 7.66us` |

Pass/resource/draw/descriptor/upload counters were unchanged at `16` / `4` /
`445` / `11` / `36,976` bytes. This result is a failed performance experiment,
not evidence that topology caching is beneficial; the deferred graph
specialization gate remains open until a lower-overhead cache design is proven.

A second five-run experiment replaced the per-pass descriptor `_bound` clear with
an epoch array. This preserved the validation rule that every descriptor must be
provided for every pass, but did not meet the no-regression criterion. Each side
used five independent 10,000-frame headless runs with frames 1,001-10,000
measured; the per-run filtered medians, median error and MAD are in these files:

- baseline: `/tmp/delta-snake-profile-10000-duplicate-validation-{1..5}.summary.json`;
- descriptor epoch: `/tmp/delta-snake-profile-10000-binding-epoch-{1..5}.summary.json`.

The median-of-five-run medians were:

| metric | baseline | descriptor epoch | change |
| --- | ---: | ---: | ---: |
| build | `18.8us` | `19.0us` | `+0.67%` |
| acquire | `3.50us` | `3.42us` | `-2.37%` |
| record | `16.7us` | `16.4us` | `-1.51%` |
| submit/present | `144.us` | `145.us` | `+0.46%` |
| fence wait | `167ns` | `167ns` | `0%` |
| layout/shaping | `42.3us` | `42.6us` | `+0.71%` |
| pass CPU | `16.1us` | `15.8us` | `-1.79%` |
| pass GPU | `187.us` | `182.us` | `-2.54%` |

The host composition moved from `226.us` to `226.us` (`+0.21%` using exact
nanosecond values `225,715` to `226,184`). Counters stayed at `16` passes, `4`
resources, `445` draws, `11` descriptor binds and `36,976` upload bytes. The
descriptor epoch experiment is rejected because total host time increased;
`VulkanGraphDescriptorState` retains the original per-pass clear.

The accepted Render-owned follow-up uses a `ulong` binding mask for pipelines
with at most 64 descriptor bindings and keeps the original cleared boolean array
only for larger pipelines. Every pass still calls `BeginBindings`, every binding
is still required before draw/dispatch, and descriptor cache reuse is unchanged.
Five independent 10,000-frame headless runs were compared on each side, with
frames 1,001-10,000 measured. The mask summaries are
`/tmp/delta-snake-profile-10000-binding-mask.summary.json` and
`/tmp/delta-snake-profile-10000-binding-mask-{2..5}.summary.json`; their raw TSV
files have the same names with `.raw.tsv`. The baseline summaries are the five
`/tmp/delta-snake-profile-10000-duplicate-validation-{1..5}.summary.json` files.

The table reports median-of-five-run medians; error and deviation are the
medians of the per-run robust median-error and MAD values:

| metric | baseline median / error / MAD | binding mask median / error / MAD | change |
| --- | --- | --- | ---: |
| acquire | `3.50us / 12.4ns / 626ns` | `3.50us / 11.6ns / 583ns` | `0%` |
| build | `18.8us / 17.3ns / 874ns` | `17.9us / 23.1ns / 1.17us` | `-5.09%` |
| fence wait | `167ns / 0.851ns / 42.0ns` | `167ns / 0.843ns / 42.0ns` | `0%` |
| layout/shaping | `42.3us / 40.0ns / 2.00us` | `43.3us / 64.2ns / 3.20us` | `+2.36%` |
| record | `16.7us / 13.4ns / 666ns` | `15.8us / 17.0ns / 854ns` | `-5.00%` |
| submit/present | `144.us / 94.0ns / 4.71us` | `142.us / 174ns / 8.83us` | `-1.56%` |
| pass CPU | `16.1us / 11.8ns / 586ns` | `15.3us / 15.7ns / 790ns` | `-5.17%` |
| pass GPU | `187.us / 164ns / 7.67us` | `185.us / 150ns / 7.04us` | `-1.09%` |

Exact host composition decreased from `225,715ns` to `222,484ns` (`-1.43%`).
Pass/resource/draw/descriptor/upload counters remained `16` / `4` / `445` /
`11` / `36,976` bytes. This is accepted as a Render-path optimization in
`VulkanGraphDescriptorState`; the increased layout/shaping time is outside
DeltaRender ownership and remains an open measurement gap.

### Required descriptor-mask fast path: accepted

The internal `VulkanGraphDescriptorState.Bind` path now uses a precomputed required mask for pipelines with up to 64 bindings. A complete valid binding set is accepted with one mask comparison; incomplete sets still use the existing per-binding diagnostic loop. The fallback bool array remains for larger binding counts. This changes no public contract and does not skip descriptor-state reset between passes.

Evidence: five independent headless Snake runs of 10,000 frames, measured frames 1,000-9,999, with 16 slots. Baseline used `delta-snake-profile-10000-binding-mask-{,2,3,4,5}.summary.json`; candidate used `/tmp/delta-snake-profile-10000-required-mask-{1,2,3,4,5}.summary.json`. Median-of-five values are in nanoseconds:

| Metric | Baseline | Required mask | Delta |
| --- | ---: | ---: | ---: |
| Record | 15,834 | 14,959 | -5.53% |
| Pass CPU | 15,289 | 14,458 | -5.44% |
| Build | 17,875 | 17,208 | -3.73% |
| Submit/present | 141,708 | 136,333 | -3.79% |
| Pass GPU | 185,043 | 185,292 | +0.13% |

Counters remained unchanged: 16 passes, 4 resources, 445 draws, 11 descriptor binds, and 36,976 upload bytes. The CPU component sum of the measured metrics decreased from 237,673ns to 226,474ns (-4.71%); the GPU delta is noise-level and the optimization is CPU-only.

### Layout verdict cache experiment: rejected

The attempted removal of the second `HasUniformVisualInstanceLayout` scan was reverted. It moved the same uniform-layout verdict into `TryPrepareVisual`, but the five-series benchmark did not preserve total execution time.

Evidence: baseline `/tmp/delta-snake-profile-10000-required-mask-{1,2,3,4,5}.summary.json`; candidate `/tmp/delta-snake-profile-10000-layout-verdict-{1,2,3,4,5}.summary.json`. Each series ran 10,000 headless Snake frames with frames 1,000-9,999 measured; spike filtering was MAD x 6 with IQR x 1.5 fallback. Median-of-five values are in nanoseconds:

| Metric | Baseline | Candidate | Delta |
| --- | ---: | ---: | ---: |
| Build | 17,208 | 16,750 | -2.66% |
| Acquire | 3,250 | 3,541 | +8.95% |
| Record | 14,959 | 15,250 | +1.95% |
| Submit/present | 136,333 | 137,667 | +0.98% |
| Fence wait | 166 | 166 | 0.00% |
| Layout/shaping | 40,100 | 40,700 | +1.50% |
| Pass CPU | 14,458 | 14,669 | +1.46% |
| Pass GPU | 185,292 | 186,751 | +0.79% |

Counters were unchanged: 16 passes, 4 resources, 445 draws, 11 descriptor binds, and 36,976 upload bytes. Because record, submit/present, and pass GPU all regressed, the candidate is rejected and its source change is not retained.

### Bounded visual clip elision: rejected

The attempted recording-only scissor elision for known `SolidRectangle`, `RoundedRectangle`, `Border`, and `Image` visuals was reverted. Although the GPU benchmark improved, the existing contract test requires the effective clip to remain observable as a scissor command even when rasterized bounds are contained by it. A future clip-in-shader design may reduce these draws, but this local optimization cannot.

Evidence: five independent headless Snake runs of 10,000 frames, measured frames 1,000-9,999, with 16 slots. Baseline: `/tmp/delta-snake-profile-10000-required-mask-{1,2,3,4,5}.summary.json`; candidate: `/tmp/delta-snake-profile-10000-clip-elision-{1,2,3,4,5}.summary.json`. Spike filtering was MAD x 6 with IQR x 1.5 fallback. Median-of-five values are in nanoseconds:

| Metric | Baseline | Clip elision | Delta |
| --- | ---: | ---: | ---: |
| Build | 17,208 | 18,583 | +7.99% |
| Acquire | 3,250 | 2,083 | -35.91% |
| Record | 14,959 | 8,250 | -44.85% |
| Submit/present | 136,333 | 108,938 | -20.10% |
| Fence wait | 166 | 166 | 0.00% |
| Layout/shaping | 40,100 | 39,600 | -1.25% |
| Pass CPU | 14,458 | 7,709 | -46.68% |
| Pass GPU | 185,292 | 172,377 | -6.97% |

The measured host phase sum (Build + Acquire + Record + Submit/present) decreased from 171,750ns to 137,854ns (-19.74%); this is a phase sum, not a claim that GPU and CPU phases are non-overlapping. Counters changed only where expected in the experiment: draws 445 -> 155 (-65.17%); passes remained 16, resources 4, descriptor binds 11, and upload bytes 36,976. All five runs submitted successfully without device-loss diagnostics, but the candidate is rejected because the headless contract test failed with expected scissor (0,0,40,40) versus candidate viewport scissor (0,0,100,80).

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

### Bounded XAML visual dirty-range upload: rejected

- [ ] Do not enable partial visual SSBO uploads in the current transfer path. The experiment compared five independent 10,000-frame headless Snake runs, measuring frames 1,000-9,999 after warm-up.
- Candidate summaries: `/tmp/delta-snake-profile-10000-segment-ranges-{1..5}.summary.json` and `/tmp/delta-snake-profile-10000-merged-ranges-{1..5}.summary.json`.
- Against the accepted required-mask baseline (`/tmp/delta-snake-profile-10000-binding-mask*.summary.json`), the best segment-range series reduced build to `17.6us` (`-1.63%`), record to `13.6us` (`-14.2%`), pass CPU to `13.1us` (`-14.2%`), submit-present to `128.5us` (`-9.29%`), and upload bytes from `36.976KB` to `2.016KB` (`-94.55%`).
- The same series increased pass GPU from `185.043us` to `195.208us` (`+5.49%`); merging gaps made it worse at `199.750us` (`+7.95%`). This fails the no-overall-regression criterion, so the source was restored to the accepted full-upload path.
- No public/frozen contract changed. A future partial-upload design needs one renderer-owned compact staging/transfer strategy or equivalent GPU-side scatter/update path; adding more `UploadBuffer` calls is not an optimization for this workload.

### Packed-segment visual dirty check: accepted

- [x] Keep the full visual SSBO upload path, but determine the dirty flag by comparing only the reusable packed visual segments already recorded in `_visualInstanceOffsets`, `_visualInstanceCounts`, and `_visualInstanceStrides`. This avoids comparing alignment/text gaps and does not alter upload bytes, bindings, draw order, or GPU layout.
- Evidence uses fresh paired conditions on the same headless Snake workload: five 10,000-frame runs, measured frames 1,000-9,999, with fresh full-upload baseline in `/tmp/delta-snake-profile-10000-fresh-baseline-{1..5}.summary.json` and candidate in `/tmp/delta-snake-profile-10000-fresh-segment-{1..5}.summary.json`.
- Median-of-five: build `16.542us -> 16.833us` (`+1.76%`), record `14.458us -> 14.291us` (`-1.16%`), submit-present `127.458us -> 126.792us` (`-0.52%`), pass CPU `13.998us -> 13.833us` (`-1.18%`), pass GPU `193.666us -> 191.126us` (`-1.31%`). Acquire/fence stayed at `1.833us`/`83ns`; layout-shaping changed `38.200us -> 38.400us` (`+0.52%`) outside Render ownership.
- Counters were identical: `16` passes, `4` resources, `445` draws, `11` descriptor binds, and `36.976KB` upload per frame. The lower submit and GPU medians offset the small build-check cost, so this candidate passes the no-overall-regression criterion for the measured path.
- Release build and `UiDisplayListGraphFeature` tests: `0` errors, `0` warnings, `21/21` passed. No public or frozen contract changed.
### Descriptor binding lookup cache: rejected

A one-entry cache for repeated `VulkanGraphDescriptorState.FindBinding` lookups was measured against the accepted packed-segment candidate with five headless Snake runs of 10,000 frames each, using frames 1,000-9,999 for medians. It did not change the workload (`445` draws, `11` descriptor binds, `36.976KB` uploaded), while median `record` rose from `14.291us` to `14.542us` (+1.76%), `pass CPU` from `13.833us` to `14.084us` (+1.81%), and `pass GPU` from `191.126us` to `198.875us` (+4.05%). `submit/present` changed from `126.792us` to `126.917us` (+0.10%). The cache was removed; the linear lookup remains the current implementation.
### Producer-keyed text preparation reuse: rejected

An internal cache of prepared text instances keyed by valid producer
`RunId/Generation/Version`, placement, paint, clip and atlas epoch was measured
against the accepted packed-segment candidate with five independent headless
Snake runs of 10,000 frames each, using frames 1,000-9,999 for medians. It
preserved the workload (`16` passes, `4` resources, `445` draws, `11` descriptor
binds and `36.976KB` uploaded), but added signature checks without producing a
usable reuse hit in this workload. Median `record` changed from `14.291us` to
`14.833us` (+3.79%), `pass CPU` from `13.833us` to `14.374us` (+3.91%),
`submit/present` from `126.792us` to `130.291us` (+2.76%), and `pass GPU` from
`191.126us` to `204.165us` (+6.82%). Build changed from `16.833us` to
`17.416us` (+3.46%) and layout/shaping from `38.400us` to `39.400us` (+2.60%).
The candidate was removed; the existing per-run cache and upload dirty check
remain the current implementation. Reports are
`/tmp/delta-snake-profile-10000-text-reuse-{1..5}.summary.json`.
### Duplicate raster-segment validation removal: rejected

Removing the second `ValidateRasterSegment()` call from the ordinary graph
build was measured with five independent headless Snake runs of 10,000 frames,
using frames 1,000-9,999 for medians. The candidate changed no graph workload
(`16` passes, `4` resources, `445` draws, `11` descriptor binds and `36.976KB`
uploaded). Against the accepted packed-segment baseline, build moved from
`16.833us` to `16.792us` (-0.24%), acquire from `1.833us` to `1.750us`
(-4.53%), and record from `14.291us` to `14.292us` (+0.01%), while
submit/present moved from `126.792us` to `126.875us` (+0.07%) and pass GPU
from `191.126us` to `198.543us` (+3.88%). The candidate was removed because
it provided no meaningful CPU improvement and regressed GPU timing; both
validation calls remain in the graph build path.
### Dependency planner span traversal: rejected

Replacing the internal dependency planner's indexed/foreach traversal with
`CollectionsMarshal.AsSpan` for the production pass list and resource reader
lists was measured with five independent headless Snake runs of 10,000 frames,
using frames 1,000-9,999 for medians. The workload stayed identical (`16`
passes, `4` resources, `445` draws, `11` descriptor binds and `36.976KB`
uploaded). Median build moved from `16.833us` to `16.875us` (+0.25%), record
stayed at `14.291us`, and pass CPU moved from `13.833us` to `13.793us`
(-0.29%); submit/present moved from `126.792us` to `126.750us` (-0.03%),
while pass GPU moved from `191.126us` to `199.374us` (+4.32%). The candidate
was removed because it did not improve CPU timing and regressed GPU timing.
Reports are `/tmp/delta-snake-profile-10000-dependency-span-{1..5}.summary.json`.
### Adapter-owned visual packed-storage reuse: rejected

- Candidate: reuse the previously packed visual instance bytes when the copied
  `UiVisualDraw` and full copied `UiDrawRef` order are unchanged.
- Baseline: `/tmp/delta-snake-profile-10000-fresh-segment-{1..5}.summary.json`.
- Candidate: `/tmp/delta-snake-profile-10000-visual-cache-{1..5}.summary.json`.
- Measurement: five headless 10,000-frame Snake runs, frames 1000..9999
  included, MAD/IQR spike filtering.
- Five-run median: build `16.833us -> 16.667us` (-0.99%), record
  `14.291us -> 14.375us` (+0.59%), submit/present `126.792us -> 127.708us`
  (+0.72%), pass CPU `13.833us -> 13.918us` (+0.61%), pass GPU
  `191.127us -> 200.167us` (+4.73%).
- Counters stayed unchanged: 16 passes, 4 resources, 445 draws, 11 descriptor
  binds and 36976 upload bytes.
- Decision: rejected. The small build median change did not improve total frame
  time and GPU timing moved in the wrong direction; the source was restored.

### In-pass pipeline/descriptor rebind suppression: rejected

- Candidate: skip repeated `BindPipeline`/`BindDescriptorSets` calls while the
  current pipeline descriptor state is unchanged within one pass; force a bind
  after `BeginBindings` or any descriptor update.
- Baseline: `/tmp/delta-snake-profile-10000-fresh-segment-{1..5}.summary.json`.
- Candidate: `/tmp/delta-snake-profile-10000-bind-suppression-{1..5}.summary.json`.
- Measurement: five headless 10,000-frame Snake runs, frames 1000..9999
  included, MAD/IQR spike filtering.
- Five-run median: record `14.291us -> 13.458us` (-5.83%), pass CPU
  `13.833us -> 12.961us` (-6.30%), but build `16.833us -> 17.000us`
  (+0.99%), submit/present `126.792us -> 128.291us` (+1.18%), and pass GPU
  `191.127us -> 192.040us` (+0.48%).
- Counters stayed unchanged: 16 passes, 4 resources, 445 draws, 11 descriptor
  binds and 36976 upload bytes.
- Decision: rejected because total measured frame time increased despite the CPU
  recording improvement; the source was restored.

### Descriptor completeness validation: rejected

- Candidate: cache the result of required descriptor-binding validation for the
  current pass; invalidate it only when a descriptor payload actually changes.
- Scope: internal CPU validation only. Descriptor contents, pipeline state, draw
  order, resource lifetime, and public contracts were unchanged. The candidate
  was reverted after measurement.
- Evidence: five independent headless 10,000-frame Snake runs, with frames
  1000..9999 analyzed by `tools/analyze-snake-profile.py`. Baseline summaries:
  `/tmp/delta-snake-profile-10000-fresh-segment-{1,2,3,4,5}.summary.json`;
  candidate summaries:
  `/tmp/delta-snake-profile-10000-descriptor-validation-{1,2,3,4,5}.summary.json`.
  Median-of-five values: build `16.833us -> 16.917us`, record
  `14.291us -> 14.375us`, submit/present `126.792us -> 127.916us`, pass CPU
  `13.833us -> 13.917us`, and pass GPU `191.127us -> 194.877us`. Counters
  stayed at 16 passes, 4 resources, 445 draws, 11 descriptor binds, and 36976
  upload bytes. Total execution did not improve.

### Render-graph topology cache: rejected

- Candidate: enable the existing `DELTA_RENDER_TOPOLOGY_CACHE=1` path so an
  unchanged pass/resource topology reuses the compiled order instead of running
  dependency compilation again.
- Scope: no source change was retained; this was a runtime-only comparison of
  the existing implementation against the normal disabled baseline.
- Evidence: five independent headless 10,000-frame Snake runs, with frames
  1000..9999 analyzed by `tools/analyze-snake-profile.py`. Baseline summaries:
  `/tmp/delta-snake-profile-10000-fresh-segment-{1,2,3,4,5}.summary.json`;
  candidate summaries:
  `/tmp/delta-snake-profile-10000-topology-cache-{1,2,3,4,5}.summary.json`.
  Median-of-five values: build `16.833us -> 17.125us` (+1.73%), record
  `14.291us -> 14.708us` (+2.92%), submit/present `126.792us -> 131.084us`
  (+3.39%), pass CPU `13.833us -> 14.290us` (+3.30%), and pass GPU
  `191.127us -> 201.210us` (+5.28%). Counters stayed at 16 passes, 4
  resources, 445 draws, 11 descriptor binds, and 36976 upload bytes. The
  capture/compare overhead outweighs the skipped dependency compilation for
  this workload, so the cache remains opt-in.

### Duplicate raster-segment validation: candidate

- Candidate: retain the validation immediately after dependency compilation and
  remove the second identical `ValidateRasterSegment()` scan at the end of
  `VulkanRenderGraph.Build`.
- Safety: a newly compiled order is still validated before topology publication;
  a topology-cache hit reuses an order that was validated when it was compiled.
  No pass/resource order, barriers, pipeline validation, or public contract is
  changed.
- Evidence will be recorded after five independent headless 10,000-frame Snake
  runs, measuring frames 1000..9999 with the standard profile analyzer.

### Duplicate raster-segment validation: rejected

- The candidate above was reverted after the required five-series headless
  measurement. Baseline summaries:
  `/tmp/delta-snake-profile-10000-fresh-segment-{1,2,3,4,5}.summary.json`;
  candidate summaries:
  `/tmp/delta-snake-profile-10000-raster-validation-{1,2,3,4,5}.summary.json`.
- Median-of-five values: build `16.833us -> 16.958us` (+0.74%), record
  `14.291us -> 14.458us` (+1.17%), submit/present `126.792us -> 128.375us`
  (+1.25%), pass CPU `13.833us -> 13.998us` (+1.19%), and pass GPU
  `191.127us -> 193.708us` (+1.35%). Counters stayed at 16 passes, 4
  resources, 445 draws, 11 descriptor binds, and 36976 upload bytes. The
  duplicate validation removal did not improve total execution time.

The same candidate was rechecked against the current accepted `merge-gap64`
baseline after the source was restored. Five independent 10,000-frame runs
again measured frames `1,000..9,999` (`45,000` filtered samples total); every
run completed with GPU timestamps and no device-loss or validation diagnostics.
The median-of-five values were build `4.875us -> 4.959us` (+1.723%), acquire
`1.916us -> 1.875us` (-2.140%), record `13.417us -> 13.542us` (+0.932%),
submit/present `120.000us -> 122.792us` (+2.327%), fence `83ns -> 83ns`,
layout/shaping `38.200us -> 38.600us` (+1.047%), pass CPU
`13.001us -> 13.085us` (+0.646%) and pass GPU
`173.085us -> 172.376us` (-0.409%). The CPU composite increased
`191.409us -> 194.853us` (+1.799%), and CPU plus measured GPU increased
`364.494us -> 367.229us` (+0.750%); counters stayed at `15`/`4`/`445`/`11`/
`2.016KB`. Candidate summaries:
`/tmp/delta-snake-profile-10000-raster-validation-{1,2,3,4,5}.summary.json`.
Decision remains rejected.

### Reuse unchanged visual preparation: candidate

- Candidate: compare the borrowed `Visuals`, `Clips`, and canonical `Order` spans
  before frame storage is cleared. When they are equal to the previous prepared
  frame, reuse the existing generated visual instance bytes/offsets and only
  re-evaluate uploaded-byte dirtiness. Any payload, clip, or order change keeps
  the existing packing path.
- Scope: visual preparation only; text, graph topology, barriers, draw order,
  resource ownership, and public contracts are unchanged.
- Evidence will be recorded after five independent headless 10,000-frame Snake
  runs, measuring frames 1000..9999 with the standard profile analyzer.

### Reuse unchanged visual preparation: rejected

- The candidate was reverted after the required five-series headless measurement.
  Baseline summaries:
  `/tmp/delta-snake-profile-10000-fresh-segment-{1,2,3,4,5}.summary.json`;
  candidate summaries:
  `/tmp/delta-snake-profile-10000-visual-preparation-reuse-{1,2,3,4,5}.summary.json`.
- Median-of-five values: build `16.833us -> 16.958us` (+0.74%), record
  `14.291us -> 14.500us` (+1.46%), submit/present `126.792us -> 127.542us`
  (+0.59%), pass CPU `13.833us -> 14.041us` (+1.50%), and pass GPU
  `191.127us -> 201.250us` (+5.30%). Counters stayed at 16 passes, 4
  resources, 445 draws, 11 descriptor binds, and 36976 upload bytes. Full-span
  equality checks cost more than the packing saved on this workload; no source
  change is retained.

### Dirty-range instance upload: rejected

- Tested visual and text dirty-range uploads using the existing `UploadBuffer` offset overload; the production path was reverted because reducing upload bytes did not reduce total frame time.
- Full byte-scan evidence: `/tmp/delta-snake-profile-10000-dirty-range-{1,2,3,4,5}.summary.json`; upload bytes fell from `36976` to `24183` (`-34.60%`), while build increased by `50.50%`.
- Compact run-cache evidence: `/tmp/delta-snake-profile-10000-dirty-cache-compact-{1,2,3,4,5}.summary.json`; upload bytes fell from `36976` to `7280` (`-80.31%`), but build increased from `16.833us` to `19.083us` (`+13.37%`), submit/present from `126.792us` to `134.542us` (`+6.11%`), and pass GPU from `191.127us` to `195.000us` (`+2.03%`).
- Current behavior remains full-buffer upload with payload equality checks; no unproven dirty-range optimization is active.

### Whole-frame text composition reuse: rejected

- Candidate reused the retained text instances/batches when every queued run matched the previous cache key, generation, version, origin, color, clip and merge flag. It still touched atlas pages and preserved the existing upload decision.
- Evidence: `/tmp/delta-snake-profile-10000-text-frame-reuse-{1,2,3,4,5}.summary.json`, each run measured `10000` headless frames and the analyzer used frames `1000..9999`.
- Median-of-five comparison against the current baseline: build `16.833us -> 16.916us` (`+0.49%`), record `14.291us -> 14.542us` (`+1.76%`), submit/present `126.792us -> 127.916us` (`+0.89%`), pass CPU `13.833us -> 14.083us` (`+1.81%`), pass GPU `191.127us -> 190.456us` (`-0.35%`). Draws, descriptor binds and upload bytes were unchanged. The candidate was reverted because total/CPU submission did not improve.

### List.RefAt in VulkanRenderGraph hot paths: rejected

- Replaced internal pooled `List<GraphPass>`/`List<GraphResource>` indexing with the existing trusted `RefAt` extension backed by `CollectionsMarshal.AsSpan`; graph order, dependencies and barriers were unchanged.
- Evidence: `/tmp/delta-snake-profile-10000-list-refat-{1,2,3,4,5}.summary.json`, five independent 10k-frame headless runs analyzed over frames `1000..9999`.
- Median-of-five versus baseline: build `16.833us -> 17.083us` (`+1.49%`), record `14.291us -> 14.583us` (`+2.04%`), submit/present `126.792us -> 129.084us` (`+1.81%`), pass CPU `13.833us -> 14.124us` (`+2.10%`), pass GPU `191.127us -> 199.499us` (`+4.38%`). Counters were unchanged. The candidate was reverted.

### Push-constant command deduplication: rejected

- Tested an internal `VulkanCommandWriter` cache keyed by layout, stage flags, offset and payload bytes; it reset on command-buffer/queue state reset and did not change ABI or public contracts.
- Evidence: `/tmp/delta-snake-profile-10000-push-cache-{1,2,3,4,5}.summary.json`, five independent 10k-frame headless runs analyzed over frames `1000..9999`.
- Median-of-five versus baseline: record `14.291us -> 14.375us` (`+0.59%`), submit/present `126.792us -> 127.083us` (`+0.23%`), pass CPU `13.833us -> 13.916us` (`+0.60%`), pass GPU `191.127us -> 199.750us` (`+4.51%`). Draws, descriptor binds and upload bytes were unchanged. The candidate was reverted.

### Text atlas page-use deduplication: rejected

- Tested a reusable per-pass epoch table to avoid repeated `GraphPass.AddUse` lookups for atlas pages. It preserved page bindings, batch order and descriptor semantics, but added a page-table check to every text batch.
- Evidence: `/tmp/delta-snake-profile-10000-page-dedupe-{1,2,3,4,5}.summary.json`, five independent 10k-frame headless runs analyzed over frames `1000..9999`.
- Median-of-five versus baseline: build `16.833us -> 16.958us` (`+0.74%`), record `14.291us -> 14.625us` (`+2.34%`), submit/present `126.792us -> 130.167us` (`+2.66%`), pass CPU `13.833us -> 14.167us` (`+2.41%`), pass GPU `191.127us -> 206.461us` (`+8.02%`). Counters were unchanged. The candidate was reverted.

### Source-tracked visual dirty ranges: accepted

`UiDisplayListGraphFeature` now reuses the existing visual instance layout when
order, clips, bounds, kind, resource and layout-affecting paint fields are
unchanged. A frame that changes only fill/stroke payloads repacks only the
changed instances. Dirty ranges are coalesced when the gap is at most `128 B`,
then uploaded with their original destination offsets; structural changes keep
the existing full preparation/upload fallback. No public or frozen contract
changed, and all instance data remains owned by the adapter and its session
buffer.

Evidence: five independent headless Snake runs of `10,000` frames, measuring
all frames `1,000..9,999` (`9,000` samples per run), using MAD x 6 with IQR x
1.5 fallback. Baseline summaries:
`/tmp/delta-snake-profile-10000-current-{1,2,3,4,5}.summary.json`.
Candidate summaries:
`/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`.
No device-loss, validation, fatal or segmentation diagnostics were found.
Median-of-five filtered medians:

| Metric | Baseline | Candidate | Delta |
| --- | ---: | ---: | ---: |
| Build | `16.791us` | `4.834us` | `-71.21%` |
| Acquire | `1.917us` | `1.959us` | `+2.19%` |
| Record | `14.459us` | `13.667us` | `-5.48%` |
| Submit/present | `126.458us` | `127.167us` | `+0.56%` |
| Fence wait | `83ns` | `83ns` | `0.00%` |
| Layout/shaping | `39.200us` | `39.700us` | `+1.28%` |
| Pass CPU | `14.002us` | `13.210us` | `-5.66%` |
| Pass GPU | `193.293us` | `191.582us` | `-0.89%` |
| Upload bytes | `36.976KB` | `2.016KB` | `-94.55%` |

Stable counters stayed at `16` passes, `4` resources, `445` draws and `11`
descriptor binds. The observed host phase sum (Build + Acquire + Record +
Submit/present) fell from `159.625us` to `147.627us` (`-7.52%`); this is a
phase sum, not a claim that CPU and GPU timings are non-overlapping. The
candidate is retained because total measured host work and pass GPU timing both
improved while upload traffic fell substantially.

### Duplicate raster validation removal: rejected again

Removing the inner `ValidateRasterSegment()` call from
`VulkanRenderGraph.Build` was tested against the accepted source-tracked visual
dirty-range candidate. The outer validation remains required as the common
post-topology-validation boundary; the duplicate-looking call was not removed
because the measured end-to-end result regressed.

Evidence: five independent headless Snake runs of `10,000` frames, measuring
all frames `1,000..9,999` (`9,000` samples per run), with MAD x 6 and IQR x 1.5
fallback. Baseline summaries:
`/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`.
Candidate summaries:
`/tmp/delta-snake-profile-10000-single-validation-{1,2,3,4,5}.summary.json`.
No device-loss, validation, fatal or segmentation diagnostics were found.
Median-of-five filtered medians:

| Metric | Accepted baseline | Single validation | Delta |
| --- | ---: | ---: | ---: |
| Build | `4.834us` | `5.041us` | `+4.28%` |
| Acquire | `1.959us` | `2.000us` | `+2.09%` |
| Record | `13.667us` | `13.750us` | `+0.61%` |
| Submit/present | `127.167us` | `132.125us` | `+3.90%` |
| Fence wait | `83ns` | `83ns` | `0.00%` |
| Layout/shaping | `39.700us` | `40.300us` | `+1.51%` |
| Pass CPU | `13.210us` | `13.292us` | `+0.62%` |
| Pass GPU | `191.582us` | `212.624us` | `+10.98%` |

Counters remained `16` passes, `4` resources, `445` draws, `11` descriptor
binds and `2.016KB` upload bytes. The source change is reverted.

### Visual dirty-range coalescing at `512 B`: rejected

Increasing the accepted dirty-range merge gap from `128 B` to `512 B` did not
reduce upload traffic or draw/descriptor counts and made the measured render
path slower. The proven `128 B` threshold is restored.

Evidence: five independent headless Snake runs of `10,000` frames, measuring
`1,000..9,999` (`9,000` samples per run), with MAD x 6 and IQR x 1.5 fallback.
Baseline summaries:
`/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`.
Candidate summaries:
`/tmp/delta-snake-profile-10000-dirty-ranges-512-{1,2,3,4,5}.summary.json`.
No device-loss, validation, fatal or segmentation diagnostics were found.
Median-of-five filtered medians:

| Metric | `128 B` baseline | `512 B` candidate | Delta |
| --- | ---: | ---: | ---: |
| Build | `4.834us` | `5.208us` | `+7.74%` |
| Acquire | `1.959us` | `1.917us` | `-2.14%` |
| Record | `13.667us` | `13.959us` | `+2.14%` |
| Submit/present | `127.167us` | `131.000us` | `+3.01%` |
| Fence wait | `83ns` | `83ns` | `0.00%` |
| Layout/shaping | `39.700us` | `40.500us` | `+2.02%` |
| Pass CPU | `13.210us` | `13.501us` | `+2.20%` |
| Pass GPU | `191.582us` | `216.875us` | `+13.20%` |
| Upload bytes | `2.016KB` | `2.016KB` | `0.00%` |

Counters remained `16` passes, `4` resources, `445` draws and `11` descriptor
binds. No source change from this candidate is retained.

### Topology cache enabled on dirty-range baseline: rejected

The existing `DELTA_RENDER_TOPOLOGY_CACHE=1` switch was re-measured on the
accepted source-tracked visual dirty-range implementation. It captures and
compares graph tokens, but does not improve the end-to-end frame path for the
current Snake workload; the switch remains opt-in and disabled by default.

Evidence: five independent headless Snake runs of `10,000` frames, measuring
`1,000..9,999` (`9,000` samples per run), with MAD x 6 and IQR x 1.5 fallback.
Baseline summaries:
`/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`.
Candidate summaries:
`/tmp/delta-snake-profile-10000-topology-cache-current-{1,2,3,4,5}.summary.json`.
No device-loss, validation, fatal or segmentation diagnostics were found.
Median-of-five filtered medians:

| Metric | Disabled baseline | Enabled candidate | Delta |
| --- | ---: | ---: | ---: |
| Build | `4.834us` | `4.875us` | `+0.85%` |
| Acquire | `1.959us` | `2.000us` | `+2.09%` |
| Record | `13.667us` | `13.834us` | `+1.22%` |
| Submit/present | `127.167us` | `127.875us` | `+0.56%` |
| Fence wait | `83ns` | `83ns` | `0.00%` |
| Layout/shaping | `39.700us` | `38.600us` | `-2.77%` |
| Pass CPU | `13.210us` | `13.375us` | `+1.25%` |
| Pass GPU | `191.582us` | `198.830us` | `+3.78%` |

Counters remained `16` passes, `4` resources, `445` draws, `11` descriptor
binds and `2.016KB` upload bytes. No source change is retained from this
experiment.

### Raster-entry resource epoch marks: rejected

A headless-only candidate replaced the nested `UsesResource` scan in
`src/DeltaRender.Vulkan/VulkanGraphBarrierPlanner.cs` with a reusable epoch-mark
array. It preserved the same resource-index membership semantics, but the
median-of-five 10,000-frame Snake comparison regressed the required total host
work and GPU timing. Each run measured all frames 1,001-10,000 after MAD x 6
filtering; no device-loss or validation error occurred.

- baseline summaries: `/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-barrier-marks-{1,2,3,4,5}-20260901.summary.json`
- candidate run medians: build `5.00us`, acquire `1.96us`, record `13.6us`, submit/present `129.us`, fence `125ns`, layout/shaping `39.6us`, pass CPU `13.2us`, pass GPU `203.us`.
- accepted baseline medians: build `4.834us`, acquire `1.959us`, record `13.667us`, submit/present `127.167us`, fence `83ns`, layout/shaping `39.700us`, pass CPU `13.210us`, pass GPU `191.582us`.
- estimated host composition changed from `187.410us` to `189.285us` (`+1.00%` using the displayed medians), while pass GPU increased about `+5.96%`.
- counters stayed at `16` passes, `4` resources, `445` draws, `11` descriptor binds and `2.016KB` upload.

The epoch table and its allocation were removed; the production path remains
the original exact scan. Do not retry without a workload showing enough
raster-entry resource fan-out to amortize the extra mark setup.

### Duplicate push-constant suppression: rejected

A Vulkan command-writer candidate cached the last exact push-constant range
(layout, stages, offset, size and bytes) and skipped an identical consecutive
`vkCmdPushConstants`. It was safe for partial ranges because a different key
replaced the single cache entry, but it failed the performance gate. Five
independent 10,000-frame Snake runs measured frames 1,001-10,000 with MAD x 6;
all runs completed without device-loss or validation errors.

- baseline summaries: `/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-push-cache-{1,2,3,4,5}-20260901.summary.json`
- median-of-five candidate medians: build `4.880us`, acquire `1.960us`, record `13.600us`, submit/present `129.us`, fence `125ns`, layout/shaping `38.800us`, pass CPU `13.100us`, pass GPU `202.us`.
- accepted baseline medians: build `4.834us`, acquire `1.959us`, record `13.667us`, submit/present `127.167us`, fence `83ns`, layout/shaping `39.700us`, pass CPU `13.210us`, pass GPU `191.582us`.
- estimated host composition changed from `187.410us` to `188.365us` (`+0.51%` using the displayed medians); pass GPU increased about `+5.44%`.
- counters stayed at `16` passes, `4` resources, `445` draws, `11` descriptor binds and `2.016KB` upload.

The push-constant cache was removed; `VulkanCommandWriter` retains the
original unconditional push behavior. Do not retry without a command stream
where identical ranges are both frequent and isolated from intervening ranges.

### Barrier planner reusable arrays: rejected

A candidate replaced the warmed `List<BufferMemoryBarrier>` and
`List<ImageMemoryBarrier>` in `VulkanGraphBarrierPlanner` with reusable arrays
and explicit counts. The barrier order and payload were unchanged, but the
median-of-five 10,000-frame Snake run regressed the host and GPU metrics. Frames
1,001-10,000 were measured after MAD x 6 filtering; all runs completed without
device-loss or validation errors.

- baseline summaries: `/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-barrier-arrays-{1,2,3,4,5}-20260901.summary.json`
- median-of-five candidate medians: build `5.210us`, acquire `2.000us`, record `14.600us`, submit/present `134.us`, fence `125ns`, layout/shaping `40.200us`, pass CPU `14.000us`, pass GPU `206.us`.
- accepted baseline medians: build `4.834us`, acquire `1.959us`, record `13.667us`, submit/present `127.167us`, fence `83ns`, layout/shaping `39.700us`, pass CPU `13.210us`, pass GPU `191.582us`.
- estimated host composition changed from `187.410us` to `196.135us` (`+4.66%` using the displayed medians); pass GPU increased about `+7.52%`.
- counters stayed at `16` passes, `4` resources, `445` draws, `11` descriptor binds and `2.016KB` upload.

The arrays/counts were removed; barrier planning retains the warmed list
storage and `CollectionsMarshal.AsSpan` handoff.

### Barrier emission/state update fusion: rejected

A candidate moved `ResourceState` updates into the end of
`VulkanGraphBarrierPlanner.Emit` and removed the second `pass.Uses` traversal
from `VulkanRenderGraph.Execute`. The change was semantically equivalent for a
successful frame, but five independent 10,000-frame Snake runs did not meet the
no-regression gate. Frames 1,001-10,000 were measured after MAD x 6 filtering;
all runs completed without device-loss or validation errors.

- baseline summaries: `/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-state-fusion-{1,2,3,4,5}-20260901.summary.json`
- median-of-five candidate medians: build `4.960us`, acquire `1.920us`, record `13.500us`, submit/present `129.us`, fence `83ns`, layout/shaping `38.700us`, pass CPU `13.100us`, pass GPU `198.us`.
- accepted baseline medians: build `4.834us`, acquire `1.959us`, record `13.667us`, submit/present `127.167us`, fence `83ns`, layout/shaping `39.700us`, pass CPU `13.210us`, pass GPU `191.582us`.
- estimated host composition changed from `187.410us` to `188.163us` (`+0.40%` using the displayed medians); pass GPU increased about `+3.35%`.
- counters stayed at `16` passes, `4` resources, `445` draws, `11` descriptor binds and `2.016KB` upload.

The fusion was removed; `VulkanGraphBarrierPlanner.UpdateStates` remains a
separate post-record update, preserving the original failure behavior.

### Combined XAML visual/text upload registration: accepted

`UiDisplayListGraphFeature` now prepares `TextRenderFeature` without registering
its standalone upload pass, then registers one reusable transfer pass for the
visual instance buffer, text instance buffer and dirty atlas pages. Standalone
`TextRenderFeature.AddPasses` keeps its original independent upload path. Upload
recording order remains text first, then visual; raster order, clips, ownership
and payload bytes are unchanged.

Headless evidence used five independent 10,000-frame Snake runs with frames
`1,000..9,999` (`9,000` samples per run), MAD x 6 filtering and IQR x 1.5
fallback:

- summaries: `/tmp/delta-snake-profile-10000-text-visual-merge-{1,2,3,4,5}.summary.json`
- accepted baseline: `/tmp/delta-snake-profile-10000-dirty-ranges-merge-{1,2,3,4,5}.summary.json`
- passes: `16 -> 15` (`-6.25%`)
- pass GPU median: `191.582us -> 175.124us` (`-8.591%`)
- submit/present median: `127.167us -> 122.959us` (`-3.309%`)
- record median: `13.667us -> 13.584us` (`-0.607%`)
- build median: `4.834us -> 4.917us` (`+1.717%`)
- acquire median: `1.959us -> 1.834us` (`-6.381%`)
- fence wait: `83ns -> 83ns`; layout/shaping: `39.700us -> 38.600us`
- unchanged: `4` resources, `445` draws, `11` descriptor binds, `2.016KB` upload

All five runs completed without device loss, validation errors or fatal
diagnostics. The focused regression and full UI/reuse set passed `37/37`; the
new mixed visual/text test passed and `git diff --check` is clean.

### Text push-constant CPU packing cache: rejected

A candidate packed the generated `TextParameters` once during composite
preparation and reused the existing bytes for each text raster pass. It kept
the required push-constant command on every pass and changed no ABI. The
candidate did not meet the no-regression gate against the immediately preceding
combined-upload baseline.

- candidate summaries: `/tmp/delta-snake-profile-10000-text-push-pack-{1,2,3,4,5}.summary.json`
- baseline summaries: `/tmp/delta-snake-profile-10000-text-visual-merge-{1,2,3,4,5}.summary.json`
- median-of-five record: `13.584us -> 13.500us` (`-0.618%`)
- median-of-five submit/present: `122.959us -> 122.084us` (`-0.712%`)
- median-of-five pass GPU: `175.124us -> 174.540us` (`-0.333%`)
- median-of-five fence wait: `83ns -> 125ns` (`+50.602%`)
- build was unchanged at `4.917us`; draw/resource/bind/upload counters were
  unchanged at `445`/`4`/`11`/`2.016KB`

The candidate source was removed. Keep per-pass generated packing until a
workload demonstrates the CPU gain without increasing fence or other frame
timings.

### Visual dirty-range merge gap 64 bytes: accepted

The XAML visual upload coalescing threshold was reduced from `128` to `64`
bytes. This preserves visual order, instance layout and dirty-range semantics
while avoiding merging small separated updates. It changes no public or frozen
contract.

Evidence uses five independent 10,000-frame headless Snake runs, frames
`1,000..9,999` (`9,000` samples per run), MAD x 6 filtering and IQR x 1.5
fallback:

- candidate summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- baseline summaries: `/tmp/delta-snake-profile-10000-text-visual-merge-{1,2,3,4,5}.summary.json`
- build: `4.917us -> 4.875us` (`-0.854%`)
- record: `13.584us -> 13.417us` (`-1.229%`)
- submit/present: `122.959us -> 120.000us` (`-2.406%`)
- layout/shaping: `38.600us -> 38.200us` (`-1.036%`)
- pass CPU: `13.127us -> 13.001us` (`-0.960%`)
- pass GPU: `175.124us -> 173.084us` (`-1.165%`)
- fence wait remained `83ns`; resources/draws/binds/upload remained
  `4`/`445`/`11`/`2.016KB`
- acquire increased from `1.834us` to `1.916us` (`+4.471%`)
- CPU composite sum (build through pass CPU): `195.104us -> 191.492us`
  (`-1.852%`)
- CPU plus measured GPU composite: `370.228us -> 364.576us` (`-1.526%`)

All runs completed without device loss, validation errors or fatal diagnostics;
the UI/reuse regression set passed `37/37`. Keep the threshold at `64` for
this workload, while treating the acquire increase as an explicit limitation
of the current evidence rather than a per-metric improvement.

### Visual dirty-range merge gap 256 bytes: rejected

The threshold `256` was tested against the accepted `64`-byte configuration
with the same five independent 10,000-frame headless Snake runs and filtering.
It produced no change in upload bytes, draw count, descriptor binds or pass
count, and regressed the measured frame composite.

- candidate summaries: `/tmp/delta-snake-profile-10000-merge-gap256-{1,2,3,4,5}.summary.json`
- baseline summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- composite CPU: `191.492us -> 195.878us` (`+2.291%`)
- composite CPU plus measured GPU: `364.576us -> 371.754us` (`+1.967%`)
- pass GPU: `173.084us -> 175.876us` (`+1.613%`)
- record: `13.417us -> 13.541us` (`+0.925%`)
- submit/present: `120.000us -> 123.750us` (`+3.125%`)

The threshold was restored to `64`; no source from this candidate is retained.

### Visual dirty-range merge gap 32 bytes: rejected

The accepted `64`-byte threshold was tested against a `32`-byte threshold in
five independent headless Snake series. Each series ran `10,000` frames and
the analyzer measured frames `1,000..9,999` (`45,000` filtered samples total)
with MAD x 6 and IQR x 1.5 fallback. Every run had GPU timestamps, no
device-loss or validation diagnostics, and unchanged counters: `15` passes,
`4` resources, `445` draws, `11` descriptor binds and `2.016KB` upload.

- baseline summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-merge-gap32-{1,2,3,4,5}.summary.json`
- build: `4.875us -> 5.042us` (`+3.426%`)
- acquire: `1.916us -> 1.875us` (`-2.140%`)
- record: `13.417us -> 13.750us` (`+2.482%`)
- submit/present: `120.000us -> 124.584us` (`+3.820%`)
- fence: `83ns -> 83ns`
- layout/shaping: `38.200us -> 38.600us` (`+1.047%`)
- pass CPU: `13.001us -> 13.335us` (`+2.569%`)
- pass GPU: `173.085us -> 173.960us` (`+0.506%`)
- CPU composite: `191.409us -> 197.186us` (`+3.018%`)
- CPU plus measured GPU: `364.494us -> 371.146us` (`+1.824%`)

Upload bytes and upload-range shape did not improve, so the candidate was
removed and the production threshold remains `64` bytes.

### Accepted baseline refresh: gap64 observation

The unchanged accepted `gap64` source was rerun to refresh the environmental
reference. Five independent headless Snake runs used `10,000` frames, measured
frames `1,000..9,999` (`45,000` filtered samples total), MAD x 6 and IQR x 1.5
fallback, with GPU timestamps enabled. All runs completed without device-loss
or validation diagnostics; counters stayed at `15` passes, `4` resources,
`445` draws, `11` descriptor binds and `2.016KB` upload.

- summaries: `/tmp/delta-snake-profile-10000-baseline-refresh-{1,2,3,4,5}.summary.json`
- build: `4.958us`; acquire: `1.959us`; record: `13.666us`
- submit/present: `123.083us`; fence: `83ns`; layout/shaping: `38.800us`
- pass CPU: `13.248us`; pass GPU: `177.165us`
- CPU composite: `195.714us`; CPU plus measured GPU: `372.879us`

The previously accepted `merge-gap64` reference remains the comparison
baseline (`CPU+GPU 364.494us`); this refresh is recorded separately because
the unchanged source produced a `+2.301%` environmental shift and must not be
mistaken for a code change.

### Snake solid/rounded-slice shader wiring: rejected

The Snake sample was temporarily A/B tested with the generated
`SolidRectangleGraphicsShaderProgram` and `RoundedRectangleGraphicsShaderProgram`
passed through the existing `UiDisplayListGraphFeature` optional program slots.
The legacy side used only the existing rounded program. No Render or XAML
contract was changed, and the sample wiring was restored after measurement.

- legacy summaries: `/tmp/delta-snake-profile-10000-legacy-visual-path-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-optimized-visual-path-{1,2,3,4,5}.summary.json`
- both sides: `10,000` frames, measured `1,000..9,999`, `45,000` filtered
  samples per side, GPU timestamps enabled, no device-loss or validation
  diagnostics
- build: `5.000us -> 6.208us` (`+24.160%`)
- acquire: `1.916us -> 2.083us` (`+8.716%`)
- record: `13.458us -> 15.209us` (`+13.011%`)
- submit/present: `123.084us -> 137.458us` (`+11.678%`)
- fence: `84ns -> 125ns` (`+48.810%`)
- layout/shaping: `38.700us -> 42.100us` (`+8.786%`)
- pass CPU: `13.040us -> 14.708us` (`+12.791%`)
- pass GPU: `173.375us -> 196.333us` (`+13.242%`)
- CPU composite: `195.198us -> 217.766us` (`+11.562%`)
- CPU plus measured GPU: `368.573us -> 414.099us` (`+12.352%`)
- upload: `2.016KB -> 3.792KB` (`+88.095%`); passes/resources/draws/binds
  stayed `15`/`4`/`445`/`11`

The candidate was removed. The current Snake benchmark remains on the rounded
path; no alternate rounded decomposition is retained.

### Retained borrowed-payload copy elision: rejected

The adapter already retained reusable backing arrays and source-tracked visual
dirty state. A candidate additionally skipped unchanged `Visuals`, `Text`,
`Clips` and `Order` copies after value comparisons. It preserved the borrowed
lifetime contract and passed the existing UI/reuse tests, but the extra
`SequenceEqual` work and branches regressed the frame composite.

- candidate summaries: `/tmp/delta-snake-profile-10000-retained-copy-{1,2,3,4,5}.summary.json`
- baseline summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- composite CPU: `191.492us -> 194.932us` (`+1.798%`)
- composite CPU plus measured GPU: `364.576us -> 369.764us` (`+1.422%`)
- pass GPU: `173.084us -> 174.833us` (`+1.010%`)
- record unchanged at `13.417us`; acquire/fence and all counters remained
  within the same workload shape

The candidate was removed; `Consume` retains its required full borrowed-span
copy, while visual instance packing still uses the proven dirty-range reuse.


## Rejected optimization: power-of-two frame-slot advance

The `VulkanFrameSlots.Advance` mask fast path was measured as a five-series
headless Snake comparison, not retained. Each series used 10,000 frames with
frames 0-999 skipped, and the analyzer processed all 9,000 remaining samples
with MAD x6 and IQR x1.5 fallback. Compared with accepted `merge-gap64`
median-of-five, build regressed `4.875us -> 5.041us` (`+3.405%`), acquire
`1.916us -> 2.000us` (`+4.384%`), record `13.417us -> 13.750us`
(`+2.482%`), submit/present `120.000us -> 123.459us` (`+2.882%`), pass CPU
`13.001us -> 13.332us` (`+2.546%`), and pass GPU `173.085us -> 176.499us`
(`+1.973%`). Fence remained `83ns`; pass/resource/draw/descriptor/upload
counters remained `15/4/445/11/2.016KB`. Candidate reports:
`/tmp/delta-snake-profile-10000-slot-mask-{1,2,3,4,5}.summary.json`.
Decision: rejected and source restored.

## Rejected optimization: skip repeated context rebinding

A candidate skipped `VulkanCommandContext.Rebind` when consecutive graph passes
used the same cached `VulkanGraphPipeline`. It preserved the existing
per-pass `BeginBindings` validation and did not change native pipeline or
resource binding semantics. Five headless Snake series used 10,000 frames,
skipped frames 0-999, and analyzed all 9,000 remaining samples with MAD x6
and IQR x1.5 fallback.

Compared with accepted `merge-gap64` median-of-five, pass GPU changed from
`173.085us` to `170.708us` (`-1.373%`), but submit/present changed from
`120.000us` to `122.375us` (`+1.979%`) and record changed from `13.417us` to
`13.500us` (`+0.619%`). The measured CPU composite (build + acquire + record +
submit/present + layout/shaping + pass CPU) changed from `191.409us` to
`194.151us` (`+1.432%`); CPU plus measured GPU changed from `364.494us` to
`364.859us` (`+0.100%`). Counters stayed `15` passes, `4` resources, `445`
draws, `11` descriptor binds and `2.016KB` uploaded. Candidate reports:
`/tmp/delta-snake-profile-10000-rebind-cache-{1,2,3,4,5}.summary.json`.

Decision: rejected; `VulkanRenderGraph` keeps the original explicit rebind path.

## Rejected optimization: O(1) descriptor binding coverage count

A candidate added a per-pass bound-binding count so `VulkanGraphDescriptorState.Bind`
could avoid scanning all descriptors on the valid path. The missing-binding
path retained its existing diagnostic scan and all native bindings/ABI stayed
unchanged. Five headless Snake series used 10,000 frames, skipped frames 0-999,
and analyzed all 9,000 remaining samples with MAD x6 and IQR x1.5 fallback.

Compared with accepted `merge-gap64` median-of-five, record improved
`13.417us -> 13.375us` (`-0.313%`), pass CPU improved
`13.001us -> 12.918us` (`-0.638%`), and pass GPU changed
`173.085us -> 171.126us` (`-1.132%`). However, CPU composite increased
`191.648us -> 193.283us` (`+0.853%`). The full CPU+GPU composite changed
`363.477us -> 363.310us` (`-0.046%`), a `167ns` difference below the observed
GPU median error scale; submit/present also increased `120.000us -> 121.542us`
(`+1.285%`). Counters stayed `15` passes, `4` resources, `445` draws, `11`
descriptor binds and `2.016KB` uploaded. Candidate reports:
`/tmp/delta-snake-profile-10000-bound-count-{1,2,3,4,5}.summary.json`.

Decision: rejected; descriptor validation and mask implementation were restored.

## Rejected optimization: allocation-free pipeline-cache factory

A candidate added a stateful static-factory overload to `VulkanPipelineCache`
to avoid the capturing lambda used by raster/compute cache lookups. Pipeline
keys, cache ownership and native pipeline lifetime were unchanged. Five
headless Snake series used 10,000 frames, skipped frames 0-999, and analyzed
all 9,000 remaining samples with MAD x6 and IQR x1.5 fallback.

Compared with accepted `merge-gap64` median-of-five, build was unchanged at
`4.875us`, while record changed `13.417us -> 13.625us` (`+1.550%`), pass CPU
`13.001us -> 13.207us` (`+1.584%`), submit/present
`120.000us -> 123.250us` (`+2.708%`), layout/shaping
`38.200us -> 38.600us` (`+1.047%`), and pass GPU
`173.085us -> 173.919us` (`+0.482%`). CPU composite changed
`191.648us -> 195.475us` and CPU+GPU composite
`363.477us -> 368.182us`. Counters stayed `15` passes, `4` resources, `445`
draws, `11` descriptor binds and `2.016KB` uploaded. Candidate reports:
`/tmp/delta-snake-profile-10000-pipeline-factory-{1,2,3,4,5}.summary.json`.

Decision: rejected; the original cache API and factory calls were restored.
### Duplicate raster-segment validation removal: rejected (2026-09-01)

The candidate removed the validation call immediately after dependency
compilation, relying on the unconditional validation below the topology-cache
branch. The source was restored because the five-run comparison did not show a
reliable CPU or total-frame improvement.

- baseline summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-single-validation-{1,2,3,4,5}.summary.json`
- both sides: `10,000` frames, measured `1,000..9,999`, `45,000` filtered
  samples per side, MAD x 6 and IQR x 1.5 fallback, GPU timestamps enabled,
  no device-loss or validation diagnostics
- baseline -> candidate medians: build `4.875us -> 4.917us` (`+0.862%`),
  acquire `1.916us -> 1.917us` (`+0.052%`), record
  `13.417us -> 13.458us` (`+0.306%`), submit/present
  `120.000us -> 122.000us` (`+1.667%`), fence `83ns -> 83ns`,
  layout/shaping `38.200us -> 38.300us` (`+0.262%`), pass CPU
  `13.001us -> 13.002us` (`+0.008%`), pass GPU
  `173.085us -> 169.585us` (`-2.022%`)
- CPU composite increased from `191.409us` to `193.594us` (`+1.141%`);
  CPU plus measured GPU changed from `364.494us` to `363.179us`
  (`-0.361%`), which is not sufficient evidence because the GPU-only gain
  is offset by higher CPU submission and all counters are unchanged
- counters stayed at `15` passes, `4` resources, `445` draws, `11` descriptor
  binds and `2.016KB` upload per frame

The duplicate validation remains intentionally explicit in both compiled and
cached-topology paths to keep the validation boundary local to each build.
### Incremental topology-token capture: rejected (2026-09-01)

The candidate recorded topology tokens while `VulkanRenderGraph` registered
resources, passes and uses, avoiding the post-registration walk used by the
existing opt-in topology cache. The source was restored after the candidate
increased the measured frame cost.

- baseline summaries: `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`
- candidate summaries: `/tmp/delta-snake-profile-10000-topology-incremental-{1,2,3,4,5}.summary.json`
- both sides: `10,000` frames, measured `1,000..9,999`, `45,000` filtered
  samples per side, MAD x 6 and IQR x 1.5 fallback, GPU timestamps enabled,
  no device-loss or validation diagnostics
- baseline -> candidate medians: build `4.875us -> 4.959us` (`+1.723%`),
  acquire `1.916us -> 1.917us` (`+0.052%`), record
  `13.417us -> 13.417us` (`0.000%`), submit/present
  `120.000us -> 123.000us` (`+2.500%`), fence `83ns -> 83ns`,
  layout/shaping `38.200us -> 38.600us` (`+1.047%`), pass CPU
  `13.001us -> 12.999us` (`-0.015%`), pass GPU
  `173.085us -> 173.460us` (`+0.217%`)
- CPU composite increased from `191.409us` to `194.892us` (`+1.819%`);
  CPU plus measured GPU increased from `364.494us` to `368.352us`
  (`+1.059%`)
- counters stayed at `15` passes, `4` resources, `445` draws, `11` descriptor
  binds and `2.016KB` upload per frame

The candidate was removed. The existing topology cache remains opt-in and uses
the original exact post-registration token capture.

### Rejected optimization: raster-entry buffer-barrier batching (2026-09-01)

- [x] Проверен headless Snake workload на пяти независимых прогонах по 10,000 кадров; в каждом отчёте 9,000 измеренных кадров (`1000..9999`), GPU timestamps доступны.
- [x] Workload сохранён: `15` passes, `4` resources, `445` draws, `11` descriptor binds, `2,016` upload bytes.
- [x] Кандидат объединял независимые raster-entry buffer barriers в один `PipelineBarrier`, сохраняя flush перед повторным ресурсом.
- [ ] Кандидат отклонён: median CPU+GPU выросла с `364.494us` до `367.796us` (`+0.906%`); record вырос с `13.417us` до `13.666us`, submit/present с `120.000us` до `122.291us`, pass GPU с `173.085us` до `173.582us`.
- Evidence: `/tmp/delta-snake-profile-10000-barrier-batch-{1,2,3,4,5}.summary.json` против `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`.
- Исходный barrier path восстановлен; новый batching не оставлен в production.

### Rejected optimization: deferred graph state update list (2026-09-01)

- [x] Candidate replaced the post-recording `pass.Uses` state traversal with a reusable list populated during barrier emission and applied after pass recording.
- [x] Compared five independent headless Snake runs of `10,000` frames; frames `1,000..9,999` were analyzed, `9,000` samples per run, GPU timestamps enabled.
- [x] Workload remained `15` passes, `4` resources, `445` draws, `11` descriptor binds and `2,016` upload bytes.
- [ ] Rejected: CPU composite changed from `191.409us` to `191.424us` (`+0.008%`), while CPU+GPU changed from `364.494us` to `367.048us` (`+0.701%`). Pass GPU changed from `173.085us` to `175.624us` (`+1.467%`) and fence wait from `83ns` to `125ns` (`+50.602%`).
- Evidence: `/tmp/delta-snake-profile-10000-state-updates-{1,2,3,4,5}.summary.json` against `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json`.
- The original post-recording state traversal is restored in production.

### Rejected optimization: value-only frame-storage clear elision (2026-09-01)

- [x] Candidate skipped `Array.Clear` for copied `UiVisualDraw`, `UiClipRegion`
  and `UiDrawRef` arrays because the active ranges are fully overwritten by the
  next borrowed display list. Reference-containing `_texts` and
  `_visualPrograms` cleanup remained in place.
- [x] Compared five independent headless Snake runs of `10,000` frames;
  frames `1,000..9,999` were analyzed (`9,000` samples per run), with GPU
  timestamps and MAD/IQR filtering.
- [ ] Rejected by the no-regression gate. Median-of-five values against
  `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json` were:
  `record 13.417us -> 13.333us (-0.626%)`, `pass CPU 13.001us -> 12.915us
  (-0.661%)`, but `submit/present 120.000us -> 122.625us (+2.188%)`.
  `pass GPU` was effectively unchanged: `173.085us -> 172.915us (-0.098%)`.
- Workload remained `15` passes, `4` resources, `445` draws, `11` descriptor
  binds and `2,016` upload bytes. Candidate reports are
  `/tmp/delta-snake-profile-10000-value-clear-{1,2,3,4,5}.summary.json`.
- The original active-range clears are restored in production.

### Current performance boundary after bounded candidate audit (2026-09-01)

- [x] The comparable five-run headless workload remains `15` passes, `4`
  resources, `445` draws, `11` descriptor binds and `2,016` upload bytes;
  each run analyzes frames `1,000..9,999` (`9,000` samples) with GPU
  timestamps and MAD/IQR filtering.
- [x] The current accepted baseline is approximately `4.875us` build,
  `13.417us` record, `120.000us` submit/present and `173.085us` pass GPU.
  The measured host phases are therefore dominated by queue submission/present;
  graph build is only about `4.9us`.
- [ ] Further reduction of the dominant `pass GPU`/draw cost requires a
  renderer-visible batching change that can preserve clip/order semantics
  (for example shader-side per-instance clip data), while further reduction of
  `submit/present` requires a session/queue submission change. Neither is a
  safe local CPU optimization under the current frozen graph and shader ABI.
- The recent barrier/state, topology, descriptor lookup, validation, upload
  range, and visual/text reuse candidates are documented above with their
  five-run rejection or acceptance evidence; no unmeasured candidate is kept
  enabled in production.

### Rejected optimization: inline graph resource-state update (2026-09-01)

- [x] Candidate updated each deduplicated pass resource state while emitting
  barriers and removed the post-recording `pass.Uses` traversal. `GraphPass`
  merges repeated declarations of the same resource before execution, so the
  candidate preserved the current barrier semantics; no public contract changed.
- [x] Compared five independent headless Snake runs of `10,000` frames;
  frames `1,000..9,999` were analyzed (`9,000` samples per run), with GPU
  timestamps enabled and the standard MAD/IQR spike filter.
- [ ] Rejected by the no-regression gate. Median-of-five values against
  `/tmp/delta-snake-profile-10000-merge-gap64-{1,2,3,4,5}.summary.json` were:
  `build 4.875us -> 4.958us (+1.703%)`, `record 13.417us -> 13.542us
  (+0.932%)`, `submit/present 120.000us -> 121.208us (+1.007%)`,
  `pass CPU 13.001us -> 13.124us (+0.946%)`, and `pass GPU 173.085us ->
  172.334us (-0.434%)`. The measured host phase sum
  (`build+acquire+record+submit/present`) increased from `140.208us` to
  `141.624us` (`+1.010%`); the small GPU decrease does not offset the CPU
  regression.
- Workload remained `15` passes, `4` resources, `445` draws, `11` descriptor
  binds and `2,016` upload bytes. Candidate reports are
  `/tmp/delta-snake-profile-10000-direct-state-{1,2,3,4,5}.summary.json`.
- The original post-recording state update path is restored in production.

### Rejected optimization: skip borrowed-resource Dispose dispatch (2026-09-01)

The candidate guarded `GraphResource.Dispose(_session)` with `Owns` during `VulkanRenderGraph.ResetBuild`, avoiding a no-op method call for imported resources. It was rejected after five independent headless Snake runs of 10,000 frames each, measuring frames 1,000-9,999 (9,000 samples per run) with MAD x6/IQR x1.5 filtering. The run-level median moved build from 4.875us to 4.917us (+0.862%), record from 13.417us to 13.375us (-0.313%), submit-present from 120us to 121.75us (+1.458%), layout/shaping from 38.2us to 38.4us (+0.524%), pass CPU from 13.001us to 12.917us (-0.646%), and pass GPU from 173.085us to 172.544us (-0.312%). Counters remained 15 passes, 4 resources, 445 draws, 11 descriptor binds, and 2,016 upload bytes. The candidate was therefore rolled back because the dominant host submit path regressed and no overall performance improvement was demonstrated. Raw reports: `/tmp/delta-snake-profile-10000-resource-own-{1..5}.raw.tsv`; summaries: `/tmp/delta-snake-profile-10000-resource-own-{1..5}.summary.json`.

### Rejected configuration: topology cache enabled (2026-09-01)

The existing `DELTA_RENDER_TOPOLOGY_CACHE=1` mode was measured without source changes using five independent headless Snake runs of 10,000 frames, with frames 1,000-9,999 measured and the same MAD x6/IQR x1.5 filtering. Run-level medians changed build from 4.875us to 4.834us (-0.841%), acquire from 1.916us to 1.792us (-6.472%), record from 13.417us to 13.458us (+0.306%), submit-present from 120us to 121.584us (+1.320%), pass CPU from 13.001us to 13us (-0.008%), and pass GPU from 173.085us to 172.086us (-0.577%). The combined build+acquire+record+submit-present host sum increased from 140.165us to 141.793us (+1.161%). Counters stayed at 15 passes, 4 resources, 445 draws, 11 descriptor binds, and 2,016 upload bytes. Keep the default disabled; the configuration does not meet the no-regression acceptance criterion. Current reports: `/tmp/delta-snake-profile-10000-topology-cache-{1..5}.raw.tsv` and `/tmp/delta-snake-profile-10000-topology-cache-{1..5}.summary.json`.

### Rejected optimization: omit duplicate raster validation in normal builds (2026-09-01)

The candidate kept the pre-commit `ValidateRasterSegment` call only when topology-cache publication was enabled and relied on the unconditional post-compile validation otherwise. Five candidate runs and five immediately subsequent control runs were collected with the same 10,000-frame headless Snake protocol (frames 1,000-9,999 measured, MAD x6/IQR x1.5 filtering). Candidate versus control medians were: build 5.250us vs 5.291us (-0.775%), acquire 3.208us vs 2.917us (+9.976%), record 13.958us vs 13.959us (-0.007%), submit-present 128.792us vs 129.042us (-0.194%), pass CPU 13.456us vs 13.460us (-0.030%), and pass GPU 177.812us vs 177.041us (+0.435%). The combined host sum was 151.208us vs 151.209us, effectively unchanged. This does not prove a meaningful improvement and the source was restored; the unconditional validation remains for the default path, while the extra validation remains before topology publication.

### Audited non-candidate: transient and stable visual preparation reuse (2026-09-01)

The Render-side lifetime audit found no safe unused reuse path to optimize in the current Snake workload. `VulkanRenderSession.CreateTransientBuffer/CreateTransientTexture` first take matching allocations from the session-owned transient pools; `GraphResource.Dispose` defers live allocations with the current frame slot, and `ReclaimDeferredTransientsForSlot` returns them only after that slot is reusable. The graph pool therefore reuses native transient allocations after the fence rather than allocating them on every frame. On the UI side, stable non-image visuals skip repeated program/ABI resolution after the first prepared frame, retained instance layout is compared and reused, and `PrepareReusedVisualInstances` repacks only dirty visual payloads. No source change is warranted by this audit; a further reduction requires producer-provided version identity or clip-aware shader instancing.

### Accepted optimization: retain dynamic viewport/scissor state across render-pass begin (2026-09-01)

`VulkanCommandWriter.BeginRenderPass` no longer invalidates its cached viewport/scissor flags. `VulkanRenderGraph.Execute` still resets command-writer state at command-buffer start and after a real queue switch, and each feature continues to issue its required viewport/scissor state; therefore a classic render-pass boundary does not force redundant dynamic-state commands. Five candidate and five immediately subsequent control headless Snake runs used 10,000 frames each, measured frames 1,000-9,999 with MAD x6/IQR x1.5 filtering. Candidate versus control medians were: record `13.875us` vs `13.959us` (-0.602%), submit-present `128.833us` vs `129.042us` (-0.162%), pass CPU `13.335us` vs `13.460us` (-0.929%), pass GPU `175.812us` vs `177.041us` (-0.694%), and layout/shaping `39.6us` vs `39.7us` (-0.252%). Build was unchanged at `5.291us`; acquire increased from `2.917us` to `2.958us` (+1.406%); fence remained `166ns`; the combined host sum was `151.208us` vs `151.209us` (-1ns, no regression). Counters stayed at 15 passes, 4 resources, 445 draws, 11 descriptor binds, and 2,016 upload bytes. The generated square readback remained valid: `/tmp/delta-dynamic-state-square.ppm`, 15,748 non-clear pixels and checksum 7,480,488. Candidate reports: `/tmp/delta-snake-profile-10000-dynamic-state-{1..5}.raw.tsv` and `.summary.json`; control reports: `/tmp/delta-snake-profile-10000-validate-control-{1..5}.raw.tsv` and `.summary.json`.

### 10k confirmation: dynamic viewport/scissor state retention (2026-09-01)

Fresh headless confirmation for the accepted `VulkanCommandWriter` optimization used five independent Snake runs with `--frames 10000 --skip 1000 --slots 16`; each report contains 9,000 filtered samples, `gpu-timestamps=True`, and unchanged workload counters (`passes=15`, `resources=4`, `draws=445`, `descriptor-binds=11`, `upload-bytes=2016`). Reports: `/tmp/delta-snake-profile-current-10000.summary.json`, `/tmp/delta-snake-profile-current-10000-2.summary.json`, `/tmp/delta-snake-profile-current-10000-3.summary.json`, `/tmp/delta-snake-profile-current-10000-4.summary.json`, `/tmp/delta-snake-profile-current-10000-5.summary.json`.

Median of the five filtered run medians, compared with the immediate control series `/tmp/delta-snake-profile-10000-validate-control-{1..5}.summary.json`: build `4.917us` vs `5.291us` (`-7.069%`), acquire `1.834us` vs `2.917us` (`-37.127%`), record `13.583us` vs `13.959us` (`-2.694%`), submit-present `122.291us` vs `129.042us` (`-5.232%`), fence wait `83ns` vs `166ns` (`-50.000%`), layout/shaping `38.400us` vs `39.700us` (`-3.275%`), pass CPU `13.128us` vs `13.460us` (`-2.467%`), and pass GPU `176.669us` vs `177.041us` (`-0.210%`). The older `merge-gap64` series is retained as a historical reference only; its cross-run comparison is not treated as acceptance evidence because the host environment drifted between series.

### Performance baseline refresh after dynamic-state fix (2026-09-01)

For subsequent candidates, use the median of five fresh filtered 10k headless runs on the current checkout as the paired baseline: build `4.917us`, acquire `1.834us`, record `13.583us`, submit-present `122.291us`, fence wait `83ns`, layout/shaping `38.400us`, pass CPU `13.128us`, and pass GPU `176.669us`. Each run measured frames `1000..9999` (`9,000` samples) with MAD x6/IQR x1.5 filtering. Workload is fixed at `15` passes, `4` resources, `445` draws, `11` descriptor binds, and `2016` upload bytes; GPU timestamps were available in all runs. Source reports are `/tmp/delta-snake-profile-current-10000.summary.json` and `/tmp/delta-snake-profile-current-10000-{2..5}.summary.json`. This baseline is paired to the current implementation and supersedes the older `merge-gap64` values for future local comparisons.
