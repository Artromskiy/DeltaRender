# DeltaRender

Standalone Vulkan renderer for game output, editor chrome, editor viewports and
runtime UI. SDL3-CS owns windows/input/surfaces; Silk.NET owns Vulkan; macOS uses
MoltenVK. Avalonia, SDL_Renderer and SDL_GPU are outside the target architecture.

## Boundaries

- `Delta.Render.Core`: renderer-facing contracts and artifact-independent data.
- `Delta.Render.Vulkan`: Vulkan resources, pipelines, dispatch and presentation.
- `Delta.Render.Platform.SDL3`: windows, metrics, event pump and surfaces.
- DeltaEngine owns frame timing, SDL polling and input translation.
- DeltaXAML owns retained UI, layout and XAML parsing.
- DeltaRender consumes `Delta.Shader.Abstractions.ShaderArtifact`; it never
  parses C# or owns a second shader manifest.

Desktop targets are Windows and Linux with native Vulkan, and macOS arm64/x64
through MoltenVK portability enumeration. Always build macOS native samples with
an explicit RID so the loader is copied beside the executable.

## Current vertical slices

Compute supports validated ShaderArtifacts, multiple SSBO bindings, upload,
dispatch and readback. `ComputeDispatcher<TResource>` implements the neutral
DeltaShader dispatch contract.

Graphics supports paired vertex/fragment artifacts, swapchain presentation,
dynamic viewport/scissor, alpha blending, fullscreen triangles and push
constants. Vulkan verifies stage and SPIR-V entry-point metadata before pipeline
creation.

UI currently exposes a consumer-owned `UiDrawList` over `ReadOnlySpan<UiQuad>`.
Multiple quads are submitted in one frame and viewport/scissor derive from the
current swapchain extent. DeltaXAML will lower its renderer-neutral draw list
into this boundary. Instancing, clip batches, texture/font atlases and text are
the next bounded renderer steps.

The sample's `PanelUiAdapter` is intentionally renderer-only smoke data. It
proves the generated UI ShaderArtifacts, clip/scissor recording and MoltenVK
present path, but is not cross-project editor evidence. The P1 acceptance proof
remains the bounded `Delta.Editor.App` path: `EditorShell.xaml` -> DeltaXAML
adapter -> neutral engine draw list -> Delta.Render -> Vulkan present.

Raw checked-in GLSL/SPIR-V is retained only for low-level fallback tests. Normal
authoring is C# → DeltaShader → ShaderArtifact.

## Frame ownership

```text
SDL event snapshot (Engine)
  -> simulation/editor update
  -> DeltaXAML layout and draw extraction
  -> DeltaRender frame submission
  -> Vulkan present
```

Renderer contracts do not poll input. GPU images used by editor viewports stay
on the GPU and are composited by the UI pass.

## Build and test

```bash
dotnet build Delta.Render.slnx -c Release --no-restore \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal
dotnet test Delta.Render.slnx -c Release --no-build --no-restore \
  --disable-build-servers -m:1
```

Window/UI smoke on Apple silicon:

```bash
dotnet run --project samples/Delta.Render.Smoke/Delta.Render.Smoke.csproj \
  -c Release -r osx-arm64 -- --interactive
```

Real C# compute shader smoke:

```bash
./tools/run-delta-shader-compute-smoke.sh
```

The compute script is bounded and performs shader-only compilation, GLSL/SPIR-V
validation, RID-specific runtime restore/build, MoltenVK dispatch and readback.
Troubleshooting and shared build rules are in `../README.md`. Benchmarks are
manual; do not publish timings from smoke or shared-runner cold starts.
