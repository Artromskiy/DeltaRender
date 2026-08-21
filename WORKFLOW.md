# DeltaRender workflow

```bash
dotnet restore Delta.Render.slnx
dotnet build Delta.Render.slnx -c Release --no-restore \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false -v:minimal
dotnet test Delta.Render.slnx -c Release --no-build --no-restore \
  --disable-build-servers -m:1
```

Bounded vertical slices:

```bash
./tools/run-delta-shader-compute-smoke.sh

dotnet run --project samples/Delta.Render.Smoke/Delta.Render.Smoke.csproj \
  -c Release -r osx-arm64 -- --interactive
```

On macOS, restore/build/run the same explicit RID so `libMoltenVK.dylib` is
copied beside the executable. Treat skipped GPU tests separately from external
SPIR-V validation. Do not run benchmark measurements during ordinary review.
Tool-specific shader regeneration is documented in
[tools/Delta.Render.UiShaders/README.md](tools/Delta.Render.UiShaders/README.md).

## Code metrics

Run the manual GitHub Actions `Code metrics` workflow before committing a
substantial change, then inspect its SARIF and summary artifacts. The rules
CA1501/CA1502/CA1505/CA1506 are report-only signals; do not refactor a method
for one isolated warning. Refactor when several metrics remain over their
limits, the issue persists across runs, or profiling identifies a hot path.
