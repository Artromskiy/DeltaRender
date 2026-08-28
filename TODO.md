# DeltaRender TODO

The authoritative public surface is [CONTRACT.md](CONTRACT.md). The ordered
implementation/removal plan is [MIGRATION.md](MIGRATION.md). Do not redesign
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
