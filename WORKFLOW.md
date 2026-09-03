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

The gate keeps the three published packages version-aligned, rejects source
references and exact pins for those packages, and keeps the source-only text
and XAML adapters non-packable with explicit producer edges.

## NuGet release protocol for 0.0.15

Run the package boundary gate first. The `DeltaRender` package must be produced
before its Vulkan and SDL3 dependents so their restore resolves the same local
`0.0.15` base package. The shader dependency is supplied by the selected
DeltaShader producer package feed. Set `DELTASHADER_PACKAGE_DIR` to that feed
before packing; do not pin a historical staging version here.

```bash
set -euo pipefail

./eng/check-package-boundaries.sh

package_dir="$PWD/artifacts/packages/0.0.15"
shader_package_dir="${DELTASHADER_PACKAGE_DIR:?Set DELTASHADER_PACKAGE_DIR to the selected DeltaShader package feed}"
mkdir -p "$package_dir"

package_sources=(
  --source "$package_dir"
  --source "$PWD/../DeltaDiagnostics/artifacts"
  --source "$PWD/../DeltaMaths/artifacts"
  --source "$shader_package_dir"
  --source https://api.nuget.org/v3/index.json
)
pack_options=(
  -c Release
  -o "$package_dir"
  --disable-build-servers
  -m:1
  /p:UseSharedCompilation=false
    -v:minimal
)

dotnet pack src/DeltaRender/DeltaRender.csproj \
  "${pack_options[@]}" "${package_sources[@]}"
dotnet pack src/DeltaRender.Vulkan/DeltaRender.Vulkan.csproj \
  "${pack_options[@]}" "${package_sources[@]}"
dotnet pack src/DeltaRender.Platform.SDL3/DeltaRender.Platform.SDL3.csproj \
  "${pack_options[@]}" "${package_sources[@]}"
```

Validate all three archives before publishing. `unzip -t` checks archive
integrity; the nuspec output must show package version `0.0.15`, the current
repository commit, and the resolved matching `DeltaShader.Contract` version for
the base/Vulkan packages,
and `DeltaRender 0.0.15` for the Vulkan/SDL3 packages.

```bash
packages=(
  "$package_dir/DeltaRender.0.0.15.nupkg"
  "$package_dir/DeltaRender.Vulkan.0.0.15.nupkg"
  "$package_dir/DeltaRender.Platform.SDL3.0.0.15.nupkg"
)

for package in "${packages[@]}"; do
  unzip -t "$package"
  unzip -l "$package"
  unzip -p "$package" '*.nuspec' | \
    rg '<id>|<version>|<repository|<dependency'
done

shasum -a 256 "${packages[@]}"
git diff --check
```

The canonical public release destination is NuGet.org. GitHub Packages may be
configured separately as a private feed for producer dependencies, but it is
not the release destination for these packages. Provision `NUGET_API_KEY`
outside the repository and shell history. Never put the credential in this
file, a command literal, a log or an artifact. Push in dependency order and
keep `--skip-duplicate` so a retry cannot create an ambiguous release step.

```bash
: "${NUGET_API_KEY:?NUGET_API_KEY must be supplied by the release environment}"
nuget_source='https://api.nuget.org/v3/index.json'

for package in "${packages[@]}"; do
  dotnet nuget push "$package" \
    --source "$nuget_source" \
    --api-key "$NUGET_API_KEY" \
    --skip-duplicate
done
```

Do not push adapter assemblies: `DeltaRender.Text` and `DeltaRender.XAML` are
source-only internal projects until their generated shader producer assemblies
have publishable runtime packages.

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

All DeltaRender C# under `src/`, `tools/`, `samples/`, `tests/` and
`benchmarks/` uses `Delta.Maths` primitives instead of direct `System.Math`
or `MathF` calls. Generated output and `bin/obj` are excluded; there are no
provider exceptions in this repository.

```bash
./eng/check-delta-maths-usage.sh
```

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
