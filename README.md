# Delta.Render

Delta.Render is the standalone Vulkan renderer and platform presentation layer
for DeltaEngine. The same renderer must draw the game, editor chrome, editor
viewports, and runtime UI. Avalonia and browser rendering are not part of the
new architecture.

## Fixed decisions

- Graphics API: Vulkan only.
- C# bindings: stable Silk.NET 2.x packages pinned centrally.
- Windowing/input: SDL3-CS behind a small Delta-owned abstraction.
- macOS: MoltenVK translates Vulkan/SPIR-V to Metal.
- Windows and Linux: native Vulkan loader and drivers.
- UI authoring: a Delta-owned XAML dialect and retained UI tree.
- Shader source: GLSH-generated SPIR-V, with checked-in SPIR-V/GLSL fixtures
  permitted until GLSH is ready.
- Math types: KibiHex.Maths where runtime/layout contracts allow it.

SDL3-CS and Silk.NET have separate responsibilities. SDL owns windows, displays,
input, DPI, clipboard, cursors, and creation of a Vulkan surface. The engine or
platform integration owns SDL event/input pumping; `IRenderWindow` exposes only
rendering-facing lifecycle, metrics, and surface access. Silk owns the Vulkan
API. Do not wait for Silk.NET 3 windowing and do not route rendering through
SDL_Renderer or SDL_GPU.

## Platform matrix

The initial supported desktop matrix is:

| Platform | Window/surface | Vulkan implementation |
| --- | --- | --- |
| Windows x64/arm64 | SDL3-CS | system Vulkan loader/driver |
| Linux x64/arm64 | SDL3-CS, X11/Wayland selected by SDL | system Vulkan loader/driver |
| macOS arm64/x64 | SDL3-CS Cocoa surface | MoltenVK over Metal |

Android, iOS, consoles, and Web are later platform projects and must not distort
the desktop API. Platform capability discovery is explicit; no code assumes all
desktop Vulkan extensions exist.

On macOS the instance path must account for portability enumeration, the
portability-subset device extension, SDL-provided surface extensions, Apple
silicon packaging, native library signing, and high-DPI pixel sizes. MoltenVK is
a deployment dependency, not a second renderer backend.

## Project boundaries

Suggested solution layout:

```text
Delta.Render/
  src/
    Delta.Render.Core/           handles, descriptions, frame/render graph
    Delta.Render.Vulkan/         Vulkan implementation and resource lifetime
    Delta.Render.Platform.SDL3/  windows, input, surfaces, DPI
    Delta.Render.UI/             retained tree, layout, styling, draw extraction
    Delta.Render.UI.Xaml/        XAML parser/compiler and diagnostics
  tests/
    Delta.Render.Tests/
    Delta.Render.Vulkan.Tests/
    Delta.Render.UI.Tests/
  samples/
```

The public core API must not expose raw SDL handles. Raw Vulkan handles remain
inside the Vulkan implementation except for narrow diagnostics/interop points.
DeltaEngine consumes Delta.Render; Delta.Render does not depend on DeltaEngine or
DeltaECS.

All own source namespaces use the `Delta.Render` root (`Delta.Render.Core`,
`Delta.Render.Platform.SDL3`, `Delta.Render.Vulkan`, and corresponding test/sample
namespaces). Project, assembly, package, solution, and directory names use the
same `Delta.Render.*` identity.

## Rendering architecture

Use explicit GPU ownership and lifetime tracking, command pools per queue/thread,
deferred destruction by completed timeline value, and no finalizer-based Vulkan
cleanup. Start with graphics and transfer; compute queues are added only where
they improve a measured workload.

The frame path is:

```text
SDL event/input snapshot
  -> engine/editor update
  -> render extraction
  -> render graph compilation
  -> Vulkan command recording
  -> swapchain present
```

Editor viewports are ordinary render-graph images composited by the UI pass on
the GPU. They must not be copied to CPU memory. Multiple windows and swapchains
are designed into ownership and event routing, even if the first sample opens
one window.

The renderer needs bindless/descriptor-indexing only after a capability/fallback
design exists. A conservative descriptor-set path is acceptable for the first
vertical slice.

Delivery 1 frame contract additions:

- `IRenderWindow` now carries a typed `Handle`, `WindowMetrics`, and `IRenderWindowSurfaceSource` for Vulkan session creation.
- `IRenderWindowFrameSession` is the minimal renderer-facing session API: `BeginFrame()`, `EndFrame(in RenderFrameState, ReadOnlySpan<RenderRecordChange>)`, and `Resize(WindowMetrics)`.
- `RenderRecordChange` is the explicit dirty-asset envelope from glue code; the renderer accepts pre-collected records and does not own ECS iteration.

## XAML UI

XAML describes a retained component tree; it is not Avalonia XAML compatibility
and does not instantiate Avalonia controls. The first subset contains:

- panels, borders, images, text, buttons, scroll views, and viewport hosts;
- properties, resources, templates, styles, bindings, and named elements;
- flex/stack/grid-like layout primitives with explicit measure/arrange rules;
- hover, focus, pressed, disabled, checked, and selected states;
- clipping, transforms, opacity, z-order, DPI scaling, and text shaping hooks;
- compiled diagnostics and optional Roslyn-generated bindings.

Keep parsing, object construction, layout, input routing, and draw extraction
separate. UI draw extraction produces compact batches for rectangles/SDF shapes,
glyphs, images, and clipping. Game UI and editor UI use the same controls and
shaders.

Do not implement the full WPF/Avalonia property system in the first milestone.
Document every supported XAML construct and reject unknown constructs with a
source location.

## GLSH contract

Delta.Render consumes SPIR-V plus a versioned GLSH reflection manifest. It does
not parse C# shader source or reproduce GLSH layout rules. The canonical shared
structure ABI is `std430` with explicit member `offset` and `stride` metadata;
SSBO is the supported resource kind for this delivery. The manifest reader
rejects missing or ambiguous ABI metadata, overlapping members, unsupported
layout features, and UBO resources. `scalarBlockLayout` is not supported or
requested. If a future UBO path is added, `uniformBufferStandardLayout` must be
requested explicitly at that time; it is not an implicit dependency of the
current ABI. Pipeline creation validates descriptor sets, push constants,
specialization constants, vertex inputs, and required capabilities against the
manifest and selected physical device.

Until GLSH produces graphics shaders, use minimal checked-in Vulkan GLSL/SPIR-V
fixtures plus a checked-in manifest fixture. Keep resource layouts identical to
the manifest contract and validate the manifest before any future GPU upload.

## Validation and performance

- Vulkan validation must be clean in debug tests.
- Headless/offscreen tests cover resource creation, upload, barriers, rendering,
  readback, and deterministic pixel/hash results.
- Window tests cover resize, minimize/restore, DPI, fullscreen, device/surface
  loss where meaningful, and multi-window shutdown.
- macOS tests run through MoltenVK; Windows and Linux tests use native Vulkan.
- UI tests snapshot layout geometry independently from pixels, then use a small
  set of golden images for raster results.
- Per-frame managed allocations are zero after warm-up for renderer and UI hot
  paths.
- CPU and GPU frame timings, batch counts, descriptor updates, upload bytes, and
  pipeline switches are observable.

## Implementation order

1. Standalone solution and platform-neutral render descriptions/handles.
2. SDL3-CS window, event loop, Vulkan extension query, and surface creation.
3. Vulkan instance/device/queues with validation and macOS portability support.
4. Swapchain clear/present on macOS, Windows, and Linux.
5. Buffers, images, uploads, synchronization, deferred destruction.
6. Minimal graphics pipeline and triangle/offscreen test.
7. Render extraction and a small render graph.
8. Retained UI tree, layout, input routing, and rectangle/image batches.
9. XAML subset parser/compiler and one editor-style sample.
10. Text shaping/rasterization, docking, multiple windows, and GLSH integration.

The first delivery ends at step 4. It must include a macOS arm64 run through
MoltenVK and platform code structured so Windows/Linux do not inherit Apple-only
requirements.

## Non-goals for the first delivery

- replacing Vulkan with a multi-backend RHI;
- using SDL_GPU or SDL_Renderer;
- complete WPF/Avalonia XAML compatibility;
- a general web/HTML/CSS engine;
- ray tracing, virtual texturing, or a production material system;
- modifying DeltaECS or GLSH internals.
