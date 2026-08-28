# DeltaRender Vulkan implementation

This document describes how `DeltaRender.Vulkan` implements the frozen
[RenderGraph contract](CONTRACT.md). It is not public API. The implementation
must be rewritten around this design; it must not wrap old direct submission
objects or retain a second execution path.

## One owner, one executor

`VulkanRenderSession` is the common owner for compute-only, offscreen and
windowed modes. Mode changes only the target provider and the execution
epilogue:

```text
compute-only       target = none       submit
headless graphics  target = image      submit
windowed           target = swapchain  acquire -> submit -> present
```

Device selection, queues, command pools, resource registry, pipeline cache,
staging, readback and graph executor are shared. Do not fork three session
implementations and do not delegate compute to a standalone device.

## Session-owned storage

### VulkanResourceRegistry

The registry owns every persistent buffer, texture, sampler and target. Public
handles contain a compact slot plus generation; lookup validates type,
generation and owning session before exposing an internal Vulkan allocation.
Release destroys the resource or schedules destruction after its last GPU use,
then increments the generation.

Targets are registry entries with an internal target kind:

- absent for compute-only;
- owned offscreen image for headless graphics;
- swapchain image provider for windowed presentation.

The target kind and raw `VkSurfaceKHR`/swapchain handles remain internal.

### Pipeline cache

Raster and compute pipelines are created lazily from pass descriptions and the
canonical `IShaderArtifact`/`IGraphicsShaderProgram`. Cache keys are computed
by Render from SPIR-V bytes, `ShaderAbi`, attachment formats and fixed pipeline
state. A graph build never creates and destroys the same pipeline per frame.

### Staging and readback

Use a session-owned, frame-local staging arena with aligned suballocation.
`UploadBuffer` and `UploadTexture` copy caller bytes into this arena during
recording; the caller's span is never retained.

Readback uses a separate reusable arena allocated only when the graph contains
a readback request. Each request records byte offset, tightly packed output
size and the submission completion token. `CopyReadback` waits only for that
submission, copies into the caller span and never forces queue-idle on ordinary
frames.

## Graph build storage

`VulkanRenderGraph` reuses dense arrays for pass nodes, resources, uses,
dependencies, execution order and readback requests. Reset counts between
builds; grow geometrically outside recording. Avoid per-pass dictionaries,
LINQ, closures and delegate allocation in the warm frame path.

Each resource node stores:

```text
kind: target / imported buffer / imported texture / transient buffer / transient texture
description
first use / last use
current Vulkan layout, access and stage
resolved internal allocation
```

Each pass node stores its kind, description, recorder reference and a range
into the shared resource-use array. Handles are one-based indices into these
arrays and are invalidated when the build generation changes.

## Compilation

Compile the graph once after all features return:

1. Validate handles, descriptions, attachment indices, ABI bindings and
   target usage.
2. Add ordering edges from read/write hazards while preserving declaration
   order for otherwise independent passes.
3. Topologically sort with deterministic insertion-order tie breaking.
4. Compute transient lifetimes and reuse compatible allocations whose ranges
   do not overlap.
5. Convert resource transitions into Vulkan image/buffer barriers.
6. Append readback copies after the last writer of each requested resource.

For the first implementation use one graphics-capable queue. Broader stage and
access masks are valid fallbacks; avoid `AllCommands` when the declared pass
kind and stage provide the exact dependency. Queue-family ownership transfers
are unnecessary until a separately measured multi-queue design is approved.

## Recording and execution

The execution order is explicit:

```text
reset frame storage
acquire target only when presentable
resolve persistent and transient resources
begin one primary command buffer
for each compiled pass:
    emit pending barriers
    bind cached pipeline
    create pass command context
    pass.Record(context)
end command buffer
submit once
present only when target is presentable
associate readbacks and deferred releases with completion
```

Command contexts are short-lived wrappers over the active graph and command
buffer. They resolve one-based graph handles directly through arrays. They
must not allocate, search resources by semantic ID or expose raw Vulkan
handles.

A zero-sized dispatch is omitted during recording. If the complete graph then
contains no effective commands, `Execute` returns `NoWork` without submitting.
Native failure returns `Failed` or `DeviceLost` with shared diagnostics; it is
never converted into a successful empty frame.

## Synchronization

Resource declarations are the synchronization authority. Track the last
stage/access/layout per graph resource and emit a barrier when a following use
has a write hazard or requires an image layout transition.

Typical paths are:

```text
host staging -> transfer read -> shader read/write
compute write -> vertex/fragment read
color attachment write -> sampled read
shader/transfer write -> transfer readback -> host read
```

Host visibility is established only for requested readbacks. Normal windowed
frames do not wait for host visibility. Vulkan validation, descriptor and ABI
checks occur before native recording whenever possible.

## Integration ownership

- DeltaRender.Text owns atlas packing and produces reusable text feature/pass
  data, but uses only the graph contract for GPU work.
- DeltaRender.XAML converts `DeltaXAML.Contract` display data into reusable
  UI/text features; it does not add a frame packet.
- Engine features own mesh/fullscreen submissions and pass the session target
  explicitly in their feature state.
- The Maths conformance runner uses a compute-only session, transfer pass,
  compute pass and graph readback. It does not create a compute device.

Specialized adapters may own convenient domain APIs, but their final GPU edge
must be an `IRenderFeature` and ordinary graph passes.
