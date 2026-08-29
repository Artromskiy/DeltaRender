# DeltaRender.Text internal design

This document is implementation-only. It describes the intended ownership and
data flow for the `DeltaRender.Text` adapter and is not a public API.

## Current state

The project is a dependency and documentation boundary. The first runtime
implementation belongs here rather than in `Delta.Render` or
`Delta.Render.Vulkan`. No text shaping or Vulkan resource code is added to the
Core project by creating this project.

## Internal stages

The implementation should remain a short pipeline:

1. Consume already shaped DeltaText glyphs and validate their image request,
   encoding, dimensions and metrics.
2. Resolve a renderer-owned atlas cache entry using all raster-affecting
   identity: font instance, glyph ID, pixels-per-em, image mode, encoding,
   distance range and any padding policy.
3. Copy pixels into a format-isolated page allocator. Coverage/SDF R8 pages and
   MSDF RGB pages must never share storage or descriptors.
4. Encode compact glyph instances into a reusable storage buffer and record
   only dirty atlas and instance ranges as transfer work.
5. Build stable ordered batches keyed by text pipeline, atlas page and clip.
   Compatible adjacent items may share one instanced draw; producer order is
   always authoritative.
6. Add ordinary graph resource uses and raster/transfer passes. The graph owns
   dependency ordering, barriers, layout transitions and submission.

## Atlas ownership

The atlas cache owns CPU page metadata and renderer-owned pixel storage. A page
has a format, packing cursor, generation and last-use information. Recycling a
bounded page resets its allocator and invalidates every handle from the old
generation before accepting new glyphs. Cache hits update page usage as well as
entry usage.

The Vulkan side receives page uploads through the existing session/graph
resource path. It must not expose `VkImage`, `VkImageView`, `VkSampler` or
descriptor objects to DeltaText, DeltaXAML or Engine.

## Buffer and upload policy

Instance and upload storage is grow-only during normal operation and reused on
warm frames. Growth is transactional: create the replacement first, copy or
re-encode the required data, then release the old allocation only after the new
resource is ready. A failed growth leaves no destroyed handle in the owner and
allows a later retry.

Dirty ranges are validated, coalesced when adjacent or overlapping, and copied
through the graph's transfer pass. Empty work produces no upload command. The
implementation must not allocate one object, staging buffer or draw command per
glyph.

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

The headless tests for the eventual implementation should cover:

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
