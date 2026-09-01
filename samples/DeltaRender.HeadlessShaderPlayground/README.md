# Delta.Render headless shader playground

This executable runs a generated Delta.Shader vertex/fragment artifact pair
through the real Vulkan render graph without SDL, a window, input polling or a
swapchain. The default pair is a sample-owned six-vertex square: two triangles
covering a centered region of the offscreen target. The generated shader
manifest remains the ABI authority; this project only adapts its fixture sidecars
to the canonical Delta.Shader contract.

Build the headless project to build the C# shader producer and publish the pair
automatically:

```bash
dotnet build samples/DeltaRender.HeadlessShaderPlayground/DeltaRender.HeadlessShaderPlayground.csproj -c Release
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
pair and draw count. The producer source is declared with
`<DeltaShaderSource Include="Shaders/**/*.cs" />`; its build target invokes
`DeltaShader.Tool` and publishes to `tools/DeltaRender.SquareShaders/bin/<Configuration>/net10.0/DeltaShader`.
The runner only consumes those final artifacts; runtime compilation is
intentionally not part of the renderer or this playground.
