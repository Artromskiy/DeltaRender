# RenderGraph-only migration

The contract checkpoint removes every supported GPU execution mechanism except
`IRenderFrameSession` plus `IRenderGraph`. This plan migrates the implementation
and consumers without reintroducing facades over the removed APIs.

## Phase 1 - make the Vulkan session satisfy the contract

1. Rename/generalize `VulkanWindowSession` to one internal session used by
   windowed, offscreen and compute-only factories.
2. Expose `Capabilities`, the optional `Target`, persistent resource creation,
   generation-checked release and target resize.
3. Make `CreateRenderGraph` return a reusable graph sharing the session's
   registry, pipeline cache, staging arena, readback arena and queues.
4. Replace `RenderSurfaceHandle` and per-frame view arrays with the one session
   `RenderTargetHandle`; viewport and `PixelRect` stay raster commands.

## Phase 2 - complete the graph executor

1. Update `VulkanRenderGraph` to the new build signature and feature callback.
2. Implement persistent imports, transient allocation/lifetime analysis,
   deterministic dependencies and Vulkan barriers.
3. Implement sampler creation and cached raster/compute pipelines from final
   DeltaShader artifacts.
4. Implement buffer/texture readback requests and `CopyReadback` without
   queue-idle on graphs that do not request CPU data.
5. Return canonical execution status and `Delta.Diagnostics` diagnostics.

## Phase 3 - migrate former compute consumers

1. Rewrite `DeltaRender.MathConformance` as a compute-only graph:
   transfer upload, compute dispatch with ABI push constants, buffer readback.
2. Replace `VulkanComputeDevice`, its storage buffers and pipeline wrappers
   with registry resources and cached graph pipelines.
3. Express dirty records as caller-produced `UploadBuffer` ranges; do not move
   record/entity semantics into RenderGraph.
4. Remove direct compute tests after equivalent graph tests cover upload,
   dispatch, push constants, no-op, limits, readback and disposal.

## Phase 4 - migrate graphics, UI and text

1. Rewrite fullscreen samples as target import + raster pass + `Draw(3)`.
2. Convert mesh submission into persistent buffer imports and raster passes.
3. Convert DeltaRender.XAML output into renderer-owned feature state and
   raster/transfer passes.
4. Convert text atlas creation, dirty uploads and instanced glyph draws into
   persistent textures/samplers/buffers plus transfer/raster passes.
5. Keep XAML display-list and DeltaText glyph values at their producer-owned
   boundaries; do not copy their public models into the Render contract.

## Phase 5 - remove legacy code

Delete, rather than wrap:

- `IComputeDevice`, `IComputePipeline`, `IComputeStorageBuffer` and
  `VulkanComputeDevice`;
- direct frame state/packet and begin/end/submit entry points;
- public graphics/text pipeline factories and standalone atlas device;
- public UI/text renderer packets, dirty-record journal and batching helpers;
- old surface/view/frame graph inputs;
- legacy samples, tests and documentation that name those paths.

If a consumer cannot migrate in the same change, the old symbol must be
`[Obsolete(..., error: true)]` with the exact remaining consumer and removal
phase. It is not part of the frozen contract and new code may not call it.

## Completion

The migration is complete when:

- a compute-only graph performs upload, push constants, dispatch and readback;
- the same executor renders offscreen and presents to a window target;
- fullscreen, mesh, XAML UI and text use graph passes;
- no active source, test, sample or documentation refers to a direct frame or
  standalone compute submission API;
- ordinary frames submit without queue-idle and allocate no per-pass managed
  collections in steady state.
