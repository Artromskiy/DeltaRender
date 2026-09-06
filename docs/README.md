# DeltaRender documentation

The [project README](../README.md) is the public entry point. This page is a
navigation index for the Vulkan render-graph contract and its implementation.

## Contracts and user-facing API

- [Render-graph contract](CONTRACT.md)
- [User API](USER_API.md)
- [Text integration contract](TEXT_CONTRACT.md)
- [DeltaRender.UI guide](DeltaRender.UI.md)

## Implementation and migration

- [Internal implementation](INTERNAL.md)
- [Text implementation](TEXT_INTERNAL.md)
- [Migration notes](MIGRATION.md)
- [Render batching](RENDER_BATCHING.md)
- [Vulkan/SDL3 decision](adr/0001-vulkan-sdl3-moltenvk-stack.md)

## Samples

- [Headless shader playground](../samples/DeltaRender.HeadlessShaderPlayground/README.md)
- [Windowed shader sandbox](../samples/DeltaRender.ShaderSandbox/README.md)

The graph is the only supported Vulkan submission path. Samples consume
producer-generated shader artifacts; they do not compile shaders themselves.
