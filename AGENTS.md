# DeltaRender agent guide

Scope: renderer-neutral draw/resource contracts, Vulkan implementation and
SDL3 surface/window integration. Engine owns event polling and frame policy.

- [docs/README.md](docs/README.md) — stable renderer boundaries and supported paths.
- [docs/CONTRACT.md](docs/CONTRACT.md) — complete cross-project renderer
  contract; declarations are part of the `Delta.Render` assembly in
  `src/DeltaRender`.
- [docs/MIGRATION.md](docs/MIGRATION.md) — mandatory removal order for every legacy GPU
  submission path; do not replace those paths with compatibility facades.
- [TODO.md](TODO.md) — selected renderer work.
- [IDEAS.md](IDEAS.md) — deferred renderer hypotheses.
- [WORKFLOW.md](WORKFLOW.md) — builds, contract tests and native smokes.
- Read [docs/adr/0001-vulkan-sdl3-moltenvk-stack.md](docs/adr/0001-vulkan-sdl3-moltenvk-stack.md)
  for platform decisions and tool-local READMEs only when changing those tools.
- Read [../CONTRACTS.md](../CONTRACTS.md) for canonical contract ownership, and
  [../DeltaShader/AGENTS.md](../DeltaShader/AGENTS.md) before changing artifact
  consumption.

Do not poll input, parse XAML/C#, shape strings or define a second shader ABI.

Skills: `gpu-memory-model` for Vulkan synchronization/resources,
`shader-dev` for pipeline/stage integration, `abi-and-calling-conventions` for
binary ABI validation and migration, `performance-speedup` and
`memory-hierarchy-and-caches` for measured batching/upload work,
`apple-silicon` for MoltenVK setup, and `lldb` for native macOS crashes.
