# DeltaRender.Text internal design

This document is implementation-only. It describes the intended ownership and
data flow for the `DeltaRender.Text` adapter and is not a public API.

## Current state

`TextRenderFeature` is the small reusable implementation in this project. It
consumes DeltaText values synchronously during graph build, owns a bounded CPU
atlas and reusable GPU instance buffer, and emits ordinary transfer/raster
graph passes. It is deliberately not a display-list adapter and has no
producer owner/version model.

Atlas packing stores row zero at the top of each page and emits UVs in the
shared top-left convention. Glyph positions and plane metrics stay in top-left
UI coordinates until the generated shader artifact performs the canonical
pixel-to-clip conversion. The feature must not compensate for an inverted
producer artifact with an extra CPU or scissor flip.

## Internal stages

The implementation remains a short pipeline:

1. `AddRun` queues already shaped values without retaining source strings.
2. Glyph images are cached by exact font generation, glyph ID, size, mode,
   encoding, distance range, color palette and feature padding.
3. Validated pixels are copied into one format-specific bounded page.
4. Compact instances use a private 48-byte GPU layout and a reusable grow-only
   array.
5. One transfer pass uploads the changed page and current instances; one raster
   pass draws adjacent clip batches with instancing.

For SDF and MSDF images, `DeltaText` supplies a distance margin of
`ceil(DistanceRange)` plus its additional guard pixel. `DeltaRender.Text` adds a
one-pixel gap between atlas slots. With the current linear, single-level,
clamped sampler this is sufficient to keep adjacent glyph slots from being
sampled as one another; the invariant is covered by the headless
`SdfAtlasPaddingSeparatesAdjacentGlyphSlots` regression. Increasing atlas
padding to the distance range would duplicate producer-owned SDF margin and
increase atlas memory without improving the sampling boundary.

## Atlas ownership

The feature owns CPU page metadata and renderer-owned pixel storage. The first
slice has one page and deterministic shelf packing. It rejects overflow rather
than silently evicting or replacing a live Vulkan image. Future multi-page
support must add explicit generation invalidation rather than exposing native
handles.

The Vulkan side receives page uploads through the existing session/graph
resource path. It must not expose `VkImage`, `VkImageView`, `VkSampler` or
descriptor objects to DeltaText, DeltaXAML or Engine.

## Buffer and upload policy

Instance and upload storage is grow-only during normal operation and reused on
warm frames. Growth is transactional: create the replacement first, copy or
re-encode the required data, then release the old allocation only after the new
resource is ready. A failed growth leaves no destroyed handle in the owner and
allows a later retry.

The page is uploaded once when it changes, while the reusable instance storage
is uploaded once per non-empty feature build. The implementation must not
allocate one object, staging buffer or draw command per glyph.

## Shader and graph boundary

Text pipeline creation consumes the final `IShaderArtifact`/
`IGraphicsShaderProgram` and validates the manifest at the Vulkan boundary.
`DeltaRender.Text` may prepare a feature/pass description, but Vulkan remains
the owner of pipeline cache, descriptor allocation, synchronization and native
cleanup. No generated class name or replacement ABI is allowed in this layer.

The normal frame route is:

```text
borrow UI/text input
  -> DeltaRender.Text adds transfer + raster work
  -> one IRenderGraph.Build
  -> one IRenderGraph.Execute
  -> optional present
```

Input polling, frame clocks and XAML lifetime remain outside this project.

## Verification obligations

The remaining headless tests should cover:

- first atlas insert and dirty upload;
- cache hit without re-upload;
- deterministic page rollover and repeated generation-safe recycling;
- R8/MSDF format isolation;
- UV and plane-metric correctness;
- invalid, foreign, disposed and stale page references;
- empty, one-glyph and multi-glyph runs;
- multi-page and multi-clip batch counts;
- UI/text ordering including `A-B-A`;
- resize/DPI coordinate conversion;
- repeated warm-frame reuse and allocation counts.

Native MoltenVK coverage is a separate bounded smoke. A skipped native run is
not evidence that the graph text path works.
