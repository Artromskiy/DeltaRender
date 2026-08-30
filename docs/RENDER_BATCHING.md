# Renderer-owned batching

`RenderBatcher` keeps persistent instance data in renderer-owned buffers and
turns producer changes into graph transfer and raster passes. It accepts
already-packed instance bytes; shader ABI packing remains the responsibility
of the producer's generated `DeltaShader` helper.

## Frame flow

1. Register a raster pipeline with its instance binding, stride and vertex count.
2. Register materials once and update their push-constant bytes only when the
   material version advances.
3. Apply each item change with its stable identity, version, order, key and
   one packed instance payload.
4. Call `AddPasses` once. The batcher records dirty uploads followed by one
   instanced draw for each active segment.

`RenderBatchItemChange.InstanceData` is borrowed for `TryApply` only. The
batcher copies it into reusable segment storage. Pipeline buffers and retired
buffers are owned by the batcher/session and released exactly once by
`Dispose`.

## Ordering modes

`Ordered` is the default for alpha UI and other order-sensitive content. Items
are grouped only while compatible items are adjacent in producer order. An
`A-B-A` sequence stays three ordered segments, while adjacent equal keys share
one draw. No global material sort is performed.

`Unordered` is for content whose producer explicitly permits reordering. It
uses buckets and swap-pop removal, so a removal can move one item and upload
only that moved slot.

## Dirty data and cost

Each pipeline has one persistent instance buffer. A changed payload updates
only its slot; adjacent dirty slots are coalesced into one transfer range per
segment. An unchanged warm frame registers no transfer pass. Material-only
changes update push constants and do not rewrite instance data. Capacity grows
geometrically and is reused; old GPU allocations remain retained until the
batcher is disposed so in-flight work cannot observe a destroyed buffer.

The batching reduction is limited by compatible adjacent segments: changing
pipeline, material or clip closes the current segment. The batcher does not
own input polling, ECS/XAML state, shader compilation or text shaping.

## Internal ownership

The public `RenderBatcher` is only the execution-order coordinator. Its
implementation is deliberately split by responsibility:

- `RenderBatchResourceRegistry` owns pipeline/material handles and stale-handle
  validation.
- `RenderBatchLayout` owns item identity/version checks and ordered or unordered
  segment mutation.
- `RenderBatchPipelineState` owns the persistent GPU buffer, growth, dirty ranges
  and retired allocations.
- `RenderBatchSegment` owns packed instance slots and its raster draw description.
- `RenderBatchGraphPasses` contains the small graph-resource wiring shared by
  segment draws.

These are internal concrete owners, not additional public APIs or compatibility
facades. The producer-facing contract remains in `RenderBatchingContracts.cs`.
