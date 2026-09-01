# DeltaRender workflow

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

## Code metrics

Run the same analyzer/code-metrics build locally and in the manual GitHub
Actions workflow through the repository wrapper:

```bash
./eng/code-metrics.sh -v:q
```

`eng/code-metrics.sh` converts `CODE_METRICS_ERROR_LOG` (default:
`artifacts/code-metrics/diagnostics.sarif`) to an absolute path before
MSBuild starts, so multi-project builds write one repository-level SARIF
instead of resolving a missing directory relative to each project. An
explicit destination is supported:

```bash
CODE_METRICS_ERROR_LOG=/tmp/code-metrics.sarif ./eng/code-metrics.sh -v:q
```

Inspect the SARIF and summary artifacts from the manual workflow. The rules
CA1501/CA1502/CA1505/CA1506 are report-only signals; do not refactor a method
for one isolated warning. Refactor when several metrics remain over their
limits, the issue persists across runs, or profiling identifies a hot path.

For local application run `./eng/format.sh`; for a non-mutating check use
`FORMAT_CHECK=1 ./eng/format.sh`. The script uses `dotnet format whitespace
--folder` to avoid the MSBuild/Roslyn workspace load that can hang on macOS
with .NET 10. It checks/applies whitespace only; analyzer/style diagnostics
remain covered by the build and SARIF metrics workflow.
