# DeltaRender internal implementation

This document is for DeltaRender maintainers. It is not a public API and must
not become a second contract.

## Ownership

`VulkanRenderSession` is the single owner for compute-only, offscreen and
windowed execution. It owns the device, queues, command pools, persistent
resources, pipeline cache, staging/readback storage and graph executor.

Opaque handles are resolved only inside the session. Vulkan and SDL/MoltenVK
objects never cross the renderer boundary. Resource release is generation
checked and idempotent; failed construction rolls back in reverse order.

## Graph execution

`VulkanRenderGraph` reuses frame-local storage for passes, resource uses,
dependencies and readbacks. A build validates declarations, derives a
deterministic order, plans transient reuse and records the required barriers.
Execute then acquires a presentable target when needed, records the compiled
passes, submits once and presents when applicable.

Normal execution must not wait for queue idle. `CopyReadback` waits only for the
submission that produced the requested data.

## Coordinate normalization

Feature and adapter code keeps `PixelRect` bounds, clips and glyph positions in
top-left UI coordinates. `SetViewport` uses a positive height. The only place
allowed to reconcile a backend rectangle representation is the Vulkan command
writer's `SetScissor` boundary; callers do not pre-flip scissor coordinates and
no second flip is permitted in a feature, shader input or file encoder.

UI artifacts must perform the canonical pixel-to-clip conversion with
`ndcY = 2*y/height - 1`. A generated artifact that still emits
`1 - 2*y/height` is a producer-side stale artifact, not a reason to add a
second Render-side inversion. Such an artifact is not evidence of canonical UI
orientation until regenerated and validated.

Texture/atlas upload and readback ownership records row zero explicitly as the
top row. A native or file-format conversion, if required, is a single bounded
operation and is verified with a top/bottom color probe.

## Synchronization and caching

Resource declarations drive transitions such as staging-to-transfer,
transfer-to-shader, shader-to-vertex/fragment and transfer-to-host readback.
Pipeline and transient-resource caches are session-owned and reused between
frames; their keys include the ABI, formats and relevant fixed state.

Zero-work graphs do not submit. Native errors preserve a diagnostic and are
reported as failure or device loss rather than being treated as an empty
successful frame.

## Integration boundary

Text, XAML, mesh and conformance code prepare neutral data and implement graph
features. They do not own Vulkan submission, input polling or shader ABI
packing. The final renderer edge is always an `IRenderFeature` plus ordinary
graph passes.
