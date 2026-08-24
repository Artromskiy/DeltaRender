# Render graph contract

This folder is the public, Vulkan-only submission boundary for meshes, text,
UI, fullscreen effects, compute and transfers. It contains contracts and value
types only; graph scheduling, Vulkan allocation, barriers and command recording
belong to `Delta.Render.Vulkan`.

## Ownership and flow

```text
Engine/extraction
  -> feature.Submit(packet)
  -> feature.AddPasses(graph, context)
  -> graph derives dependencies and Vulkan synchronization
  -> pass.Record(commandContext)
  -> Vulkan submission/present
```

- `Submit` replaces the previous feature data. It does not mean immediate GPU
  work and supersedes ambiguous names such as `SetFrame`.
- `RenderGraphFrame` and `IRenderFeatureContext` contain frame identity and
  views, never `DeltaTime`. Time-dependent features receive an explicitly
  selected Engine clock value inside their own submission packet.
- Every `RenderView` owns its target `RenderSurfaceHandle`, `RenderViewport`
  and pixel-space `PixelRect` scissor. A frame may therefore target multiple
  surfaces without a separate single-surface field.
- Imported handles refer to renderer-owned persistent resources. Graph handles
  refer to imported or transient resources and expire after the graph build.
- Features declare resource use before recording. The Vulkan implementation
  derives pass order, lifetimes and barriers from those declarations.
- Raster and compute descriptions consume the canonical DeltaShader
  `GraphicsShaderProgram` and `ShaderArtifact`. DeltaRender validates and
  caches Vulkan pipelines; it never compiles C# or creates a second shader ABI.
- `IRasterCommandContext` is sufficient for fullscreen triangles, instanced
  text/UI and indexed meshes. Compute and transfer operations use their own
  narrow contexts.

## Minimal feature shape

```csharp
public readonly record struct MeshSubmission(
    RenderGraphBufferHandle Vertices,
    RenderGraphBufferHandle Indices,
    uint IndexCount);

public sealed class MeshFeature : IRenderFeature<MeshSubmission>, IRasterPass
{
    public void Submit(in MeshSubmission submission) { /* borrow/copy policy */ }

    public void AddPasses(IRenderGraphBuilder graph, IRenderFeatureContext context)
    {
        // Import/create resources, add a raster pass and declare its accesses.
    }

    public void Record(IRasterCommandContext commands)
    {
        // Bind declared resources and issue DrawIndexed.
    }
}
```

The example intentionally leaves packet storage policy to the feature. A
borrowed submission must stay alive through graph execution; owned submissions
must make that copy explicit in the feature implementation.
