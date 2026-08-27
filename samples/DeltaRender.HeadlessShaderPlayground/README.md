# Delta.Render headless shader playground

This executable runs a generated Delta.Shader vertex/fragment artifact pair
through the real Vulkan render graph without SDL, a window, input polling or a
swapchain. The default pair is the checked-in fullscreen artifact used by the
renderer smoke. The generated `FullscreenUiGraphicsShaderProgram` remains the
ABI authority; this project does not deserialize or duplicate shader metadata.

Run from the DeltaRender directory:

```bash
dotnet run --project samples/DeltaRender.HeadlessShaderPlayground/DeltaRender.HeadlessShaderPlayground.csproj \
  -c Release -- --shader-dir samples/DeltaRender.Smoke/shaders --frames 3
```

The final offscreen frame is written to
`artifacts/headless-shader-playground/output.ppm`. Override it with
`--output /path/to/frame.ppm`.

Use `--vertex` and `--fragment` to select another generated SPIR-V pair with
the same fullscreen push-constant ABI. The producer must generate and validate
the files before launch; runtime compilation is intentionally not part of the
renderer or this playground.
