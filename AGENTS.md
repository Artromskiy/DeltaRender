# DeltaRender agent router

Scope: renderer-neutral draw/resource contracts, the Vulkan implementation and
SDL3 surface/window integration. DeltaEngine owns event polling and frame
policy. Vulkan is the only supported renderer backend.

## Map — open only as needed

- ../CODE_STYLE.md — technical ownership, lifetime, batching and low-level evidence rules.
- ../CONTRACTS.md — canonical cross-project ownership; open only for a boundary task.
- IDEAS.md — renderer research/options only when requested.
- WORKFLOW.md — builds, contract checks and bounded native smokes.
- docs/CONTRACT.md — complete frozen renderer contract; declarations live in src/DeltaRender.
- docs/USER_API.md — user-facing renderer usage; open only for public API/documentation work.
- docs/INTERNAL.md and docs/MIGRATION.md — Vulkan implementation and legacy-removal order.
- src/DeltaRender and src/DeltaRender.* — production contract and implementation siblings.
- tests, samples, tools — verification, runnable examples and developer tools.

Do not poll input, parse XAML/C#, shape strings or define a second shader ABI.
Use gpu-memory-model, shader-dev, abi-and-calling-conventions,
performance-speedup, memory-hierarchy-and-caches, apple-silicon and lldb only
for the corresponding bounded area.
