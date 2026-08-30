# Delta.Render headless shader playground

This executable runs a generated Delta.Shader vertex/fragment artifact pair
through the real Vulkan render graph without SDL, a window, input polling or a
swapchain. The default pair is a sample-owned six-vertex square: two triangles
covering a centered region of the offscreen target. The generated shader
manifest remains the ABI authority; this project only adapts its fixture sidecars
to the canonical Delta.Shader contract.

Generate the square pair from the C# shader producer:

```bash
dotnet build tools/DeltaRender.SquareShaders/DeltaRender.SquareShaders.csproj -c Release
dotnet run --project /Users/rum/GitProjects/TheFurnace/DeltaShader/src/DeltaShader.Tool/DeltaShader.Tool.csproj \
  -c Release --no-build --no-restore -- build tools/DeltaRender.SquareShaders/DeltaRender.SquareShaders.csproj \
  --backend spirv --profile vulkan1.2 --spirv 1.5 --glsl 460 \
  --out ../DeltaShader/src/DeltaShader/CompiledShaders
```

Run from the DeltaRender directory:

```bash
dotnet run --project samples/DeltaRender.HeadlessShaderPlayground/DeltaRender.HeadlessShaderPlayground.csproj \
  -c Release -- --frames 3 --width 1024 --height 1024
```

The final offscreen frame is written to
`artifacts/headless-shader-playground/square.ppm`. Override it with
`--output /path/to/frame.ppm`.

Use `--vertex`, `--fragment` and `--vertices` to select another generated SPIR-V
pair and draw count. The producer must generate and validate the files before
launch; runtime compilation is intentionally not part of the renderer or this
playground.
