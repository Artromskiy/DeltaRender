# ADR-0001: Vulkan + SDL3 + MoltenVK stack choice for Delta.Render delivery 1

## Status
Accepted.

## Context
Delivery 1 requires one Vulkan renderer stack on three desktop platforms. The renderer must use SDL3 for window/input/event boundaries and Vulkan as the rendering API.

## Decision
- Use `SDL3-CS` managed wrapper for SDL surfaces/input integration.
- Use `Silk.NET.Vulkan` for all Vulkan API usage.
- Keep `Delta.Render.Platform.SDL3` and `Delta.Render.Vulkan` as separate projects with a narrow cross-project interop surface.
- Keep raw Vulkan handles inside `Delta.Render.Vulkan`; only raw extension/hook interop points are exposed through `IVulkanWindowSurfaceSource`.
- Use `Delta.Render` as the root namespace for all own source, test, and sample code. Use the matching `Delta.Render.*` identity for project, assembly, package, solution, and directory names.
- On macOS, request portability enumeration when the loader advertises it and enable the device portability subset when the selected device advertises it; rely on MoltenVK packages for the `VK_EXT_METAL` backend.
- Add explicit headless probes so tests and smoke sample can print deterministic diagnostics if loader/display is unavailable.

## Package/License pins
- `SDL3-CS` (managed): **3.4.12.6** — **Zlib**
- `SDL3-CS.Windows` / `SDL3-CS.Linux` / `SDL3-CS.MacOS`: **3.4.12.6** — **Zlib**
- `Silk.NET.Vulkan`: **2.23.0** — **Apache-2.0**
- `Silk.NET.MoltenVK.Native`: **2.23.0** — **Apache-2.0 / upstream BSD-3-Clause for MoltenVK artifacts where applicable**
- DeltaShader is not modified in this delivery; renderer uses minimal checked-in shader fixtures only as placeholders.

## Portability policy
- **Windows/Linux:** native `SDL3-CS.<Platform>` + native Vulkan loader
- **macOS arm64/x64:** native `SDL3-CS.MacOS` + `VK_KHR_portability_enumeration` where available + MoltenVK runtime package (`Silk.NET.MoltenVK.Native`) and explicit diagnostics for portability path in initialization.

## Delivery scope completed in this phase
1. Platform-neutral contracts and diagnostics in `Delta.Render.Core`.
2. SDL3 windowing module in `Delta.Render.Platform.SDL3` with Vulkan extension query and surface creation.
3. Vulkan instance/device queue bootstrap and swapchain clear/present code path in `Delta.Render.Vulkan`.
4. Headless probe tests and macOS smoke execution sample.
