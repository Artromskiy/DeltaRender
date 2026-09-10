# DeltaRender workflow

## Benchmark parameter policy

BenchmarkDotNet attributes may describe benchmark methods, categories and
lifecycle hooks, but they must not define workload or run parameters. Do not add
`[Params]`, `[ParamsSource]`, `[Arguments]`, `[ArgumentsSource]` or equivalent
parameter attributes. Parse every workload/configuration value from application
command-line arguments (or the invoking script) before BenchmarkDotNet starts,
and pass the resulting values into the benchmark runner. Keep BDN runner
switches such as `--filter` and `--job` separate from workload input. Existing
parameter attributes are migration debt: do not add new uses and replace them
when that benchmark is next modified.


## Repository layout gate

The repository must follow the shared first-party layout documented in the
Furnace project standard. Before restore/build or a structural handoff, run:

```bash
./eng/check-layout.sh
```

The gate checks the mandatory top-level directories, rejects unexpected
tracked top-level folders, requires src/DeltaRender/ as the primary source
project, and requires source siblings to use the src/DeltaRender.<Area>/ form.
samples/ contains runnable examples; probes/ contains bounded
headless/compiler/contract checks. Empty mandatory domains stay tracked with
.gitkeep.

Package and adapter boundaries can be checked without restore or build:

```bash
./eng/check-package-boundaries.sh
```

The gate keeps the four published packages version-aligned, rejects source
references and exact pins for those packages, and keeps the source-only text
and XAML adapters non-packable with explicit producer edges. `DeltaRender.UI`
is the consumer bundle: it packages those adapter assemblies and generated UI
and text shader assemblies without making the adapters independent packages.

## NuGet package details

Use the root [`dev` and `release` workflow`](../docs/NUGET_WORKFLOW.md); it is
the only supported pack, restore and publish entry point. It reads package
versions from the project files and stages the complete first-party dependency
graph, so this document intentionally contains no version literals or manual
push commands.

DeltaRender publishes the base, Vulkan, SDL3 and UI bundle in dependency order.
`DeltaRender.Text` and `DeltaRender.XAML` remain source-only implementation
projects; their runtime assemblies and generated shader producer outputs are
distributed through `DeltaRender.UI`. Run
`./eng/check-package-boundaries.sh` when changing those ownership boundaries.

The contract checkpoint can be checked independently while the Vulkan and
consumer migration in `docs/MIGRATION.md` is in progress:

```bash
dotnet build src/DeltaRender/DeltaRender.csproj -c Release \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal
```

After every migration phase restores its consumers, use the complete gate:

```bash
dotnet restore DeltaRender.slnx
dotnet build DeltaRender.slnx -c Release --no-restore \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal
dotnet test DeltaRender.slnx -c Release --no-build --no-restore \
  --disable-build-servers -m:1
```

Bounded vertical slices:

```bash
./tools/run-delta-shader-compute-smoke.sh

dotnet run --project samples/DeltaRender.Smoke/DeltaRender.Smoke.csproj \
  -c Release -r osx-arm64 -- --interactive
```

On macOS, restore/build/run the same explicit RID so `libMoltenVK.dylib` is
copied beside the executable. Treat skipped GPU tests separately from external
SPIR-V validation. Do not run benchmark measurements during ordinary review.

### Software Vulkan check

GitHub Actions builds the official SwiftShader software Vulkan implementation,
stages its ICD manifest and runs the test project and headless graphics
playground with SwiftShader selected explicitly through `VK_DRIVER_FILES`. The
check verifies the ICD manifest, its native library and the reported SwiftShader
device before running the RenderGraph path. It never falls back to MoltenVK or
a physical GPU. The Vulkan summary, test log and headless PPM are uploaded as
CI artifacts. Test cases are enumerated and run in isolated test-host
processes; this preserves every assertion while preventing a native
font/Vulkan teardown failure in one host from aborting unrelated cases.

After a Release build, run the same check locally with an installed SwiftShader
ICD:

```bash
SWIFTSHADER_ICD=/absolute/path/vk_swiftshader_icd.json \
  ./eng/run-software-vulkan.sh --driver swiftshader
```

For macOS CPU Vulkan coverage, use an explicit lavapipe manifest and loader
library; set `LAVAPIPE_ICD=/absolute/path/lvp_icd.json` and run
`./eng/run-software-vulkan.sh --driver lavapipe`. The script verifies the
selected device through `vulkaninfo` and reports `shaderFloat64`; Maths double
conformance requires that feature to be exposed by the installed lavapipe.

Add `--conformance` to either command to run the existing Maths compute
RenderGraph runner against the checked-out DeltaMaths bundle and DeltaShader
artifact catalog. Missing software ICDs are reported as errors; the command
does not silently use MoltenVK.

DeltaShader is the sole shader source and compilation owner. Render projects
consume the generated program/factory API, final `ShaderArtifact`/`ShaderAbi`
and typed packers from the producer's private `DeltaShader.Tool` NuGet
reference. Render does not invoke the shader CLI, parse sidecars or calculate
ABI layout. Do not copy generated outputs into `DeltaRender/artifacts`.
Tool-specific shader validation is documented in
[tools/DeltaRender.UIShaders/README.md](tools/DeltaRender.UIShaders/README.md).
For the CPU/GPU Maths smoke, generate a fresh temporary catalog with:

```bash
math_out="$(mktemp -d)"
trap 'rm -rf "$math_out"' EXIT
(cd ../DeltaShader && ./eng/prepare-maths-conformance-artifacts.sh "$math_out")
```
Run `./eng/check-shader-output-ownership.sh` to reject Render-local generated
shader binaries and sidecars.
Graph contract tests must cover session/resource ownership, graph-local handle
invalidation, deterministic pass ordering, read/write hazards, readback
lifetime and diagnostics without loading Vulkan. Vulkan tests then cover the
same graph executor in compute-only, offscreen and windowed modes. A skipped
native test is not a successful GPU path.

### Known macOS test-host limitation

On macOS ARM64 with the current .NET 10 test host, the complete
`TextRenderFeatureTests` class can terminate the host during the multi-page
atlas test after preceding text tests, while each test and bounded subsets pass
individually. The observed crash is a host-level SIGSEGV with no Vulkan or SDL
frames; it is not evidence of a DeltaRender production defect. Do not disable,
suppress or mark the test passed. Until the runtime/test-host issue is
resolved, CI must retain the test and record the interrupted class run as an
environmental failure, while individual test cases remain runnable for
diagnostics.

## Code metrics

### DeltaMaths usage gate

All DeltaRender C# under `src/`, `tools/`, `samples/` and `tests/` uses
`Delta` primitives through the canonical `Maths.*` facade instead of direct
`System.Math` or `MathF` calls. `global::Delta.Maths` is only the fully-qualified
`Maths` class alias for sibling-namespace resolution. Shader-authoring code may
use `Delta.maths`. Generated output and `bin/obj` are excluded; there are no
provider exceptions in this repository.

```bash
./eng/check-delta-maths-usage.sh
```

Run the shared Furnace wrappers from this repository before every commit; see
the [common workflow](../REVIEW_PLAYBOOK.md#shared-local-formatter-and-metrics-wrappers):

```bash
../eng/format.sh "$PWD"
FORMAT_CHECK=1 ../eng/format.sh "$PWD"
../eng/code-metrics.sh "$PWD" -v:q
```

Set `CODE_METRICS_ERROR_LOG` when a different SARIF destination is needed:

```bash
CODE_METRICS_ERROR_LOG=/tmp/deltarender-metrics.sarif \
  ../eng/code-metrics.sh "$PWD" -v:q
```

Inspect the SARIF and summary artifacts from the manual workflow. The rules
CA1501/CA1502/CA1505/CA1506 are report-only signals; do not refactor a method
for one isolated warning. Refactor when several metrics remain over their
limits, the issue persists across runs, or profiling identifies a hot path.
