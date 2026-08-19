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
- Shader source: C# shader -> Delta.Shader-generated GLSL -> SPIR-V; checked-in
  SPIR-V/GLSL fixtures remain available only for low-level fallback tests.
- Math types: Delta.Maths where runtime/layout contracts allow it.

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

## Delta.Shader contract

Delta.Render consumes `ShaderArtifact` from `Delta.Shader.Abstractions`. The
artifact contains SPIR-V bytes and a versioned reflection manifest; Delta.Render
does not parse C# shader source or reference Roslyn/Compiler projects. The
canonical shared structure ABI is `std430` with explicit offset, size, alignment,
array-stride, and optional matrix-stride metadata; SSBO is the supported resource
kind for this delivery. The Vulkan consumer validates artifact format/version,
compute stage/local sizes, descriptor set/binding/access, storage-buffer kind,
and the available std430 ABI fields before creating a pipeline. Multiple SSBO
resources are bound by exact manifest set/binding keys; declared buffer access
and minimum size/array stride are checked before dispatch. `UploadRanges` uses a
reusable host-visible staging buffer and coalesces adjacent or overlapping
ranges, while `Upload` remains the simple full-upload fallback. `Readback`
reuses a host-visible download staging buffer. `scalarBlockLayout`
is not supported or requested. If a future UBO path is added,
`uniformBufferStandardLayout` must be requested explicitly at that time; it is
not an implicit dependency of the current ABI.

The manifest keeps the source entry point (`SourceEntryPointName`) separate from
the emitted Vulkan entry point (`EntryPointName`, currently `main`). Delta.Render
checks the emitted name against the SPIR-V `OpEntryPoint` before creating the
Vulkan pipeline. The compute smoke uses the reproducible external chain in
`tools/run-delta-shader-compute-smoke.sh`: `delta-shader build` emits GLSL,
SPIR-V, and `.shader.json` from the checked-in C# shader project; the Delta.Render
sample loads the generated SPIR-V and manifest into `ShaderArtifact`, then runs
the dispatch/readback oracle. The checked-in compute SPIR-V fixture remains only
for the raw low-level fallback tests.

## Graphics vertical slice

`IRenderWindowFrameSession.CreateGraphicsPipeline` accepts a paired
`GraphicsShaderProgram`, and `DrawFullscreenTriangle` records one vertexless
fullscreen draw between `BeginFrame` and `EndFrame`. The Vulkan implementation
uses the swapchain render pass, dynamic viewport/scissor, alpha blending, and a
single push-constant block: `resolution.xy` at offset 0, `time` at offset 8,
and four reserved bytes at offset 12. The fragment fixture renders an animated,
analytically anti-aliased rounded rectangle with `fwidth`/`smoothstep`.

`GraphicsShaderProgram` accepts the same versioned `ShaderArtifact` used by the
compute path. Vertex/fragment stage identity, emitted entry-point names,
interfaces, and push-constant layout come from `Delta.Shader.Abstractions`;
Delta.Render does not define a second shader manifest. The checked-in graphics
fixtures are reproducible outputs of the C# `FullscreenUi` shader and retain
their `.shader.json` manifests next to the validated SPIR-V.

The sample's default path opens the SDL3 window and presents the rounded
rectangle; `--clear` retains the swapchain-only fallback and `--frames N`
drives a bounded animation. SDL event/input pumping remains outside the
renderer and the sample calls the explicit `Sdl3WindowFactory.PumpEvents()`
host hook once before teardown; it does not add an input subsystem.

The next bounded UI contract is: instanced quad batches with per-instance
transform/color, explicit clip rectangles mapped to scissor regions, a
texture/font atlas binding, SDF shape and glyph batches, and stable batch keys
for material/clip/texture changes. It does not yet implement retained UI,
text shaping, atlas uploads, or hierarchy.

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
10. Text shaping/rasterization, docking, multiple windows, and Delta.Shader integration.

The first delivery ends at step 4. It must include a macOS arm64 run through
MoltenVK and platform code structured so Windows/Linux do not inherit Apple-only
requirements.

## Non-goals for the first delivery

- replacing Vulkan with a multi-backend RHI;
- using SDL_GPU or SDL_Renderer;
- complete WPF/Avalonia XAML compatibility;
- a general web/HTML/CSS engine;
- ray tracing, virtual texturing, or a production material system;
- modifying DeltaECS or Delta.Shader internals.


## CI, tests, and benchmarks

The GitHub Actions workflow is [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
Pull requests and pushes to `main` build in Release, run correctness tests, and
perform BenchmarkDotNet discovery only; they do not record performance numbers.
Measured benchmarks run only from **Actions → Build, tests and benchmarks → Run
workflow** with `run_benchmarks=true`. Results are uploaded from
`artifacts/benchmarks` for 30 days.

Repository conventions:

- correctness projects are named `*.Tests.csproj`; projects using
  `Microsoft.NET.Test.Sdk` run through `dotnet test`, while custom executable
  harnesses must be listed explicitly in the workflow and return a non-zero exit
  code on failure;
- BenchmarkDotNet projects are named `*.Benchmarks.csproj`; this filename is how
  the workflow discovers them;
- their entry point must forward CLI arguments with
  `BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args)`;
- mark the Delta implementation with `[Benchmark(Baseline = true)]` within every
  comparable benchmark category; use exactly one baseline per category;
- add sibling repositories to the checkout steps whenever a
  `ProjectReference` escapes this repository.

A benchmark added without the naming convention or without CLI argument
forwarding is not registered and must not be treated as CI coverage. Shared
GitHub runners are suitable for comparisons within one run, not for small
cross-run regression claims.
