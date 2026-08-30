# DeltaRender user API

This is the user-facing guide to the frozen RenderGraph contract. DeltaRender
does not expose a direct frame API, a standalone compute device or raw Vulkan
objects.

## One frame path

All session modes use the same sequence:

```csharp
IRenderGraph graph = session.CreateRenderGraph();
graph.Build(frameNumber, features);
RenderGraphExecutionResult result = graph.Execute();
```

The session may be compute-only, headless graphics or windowed. Only a
presentable session acquires and presents a window target.

Features add ordinary transfer, compute or raster passes. A feature owns its
producer-side state and implements the pass recording; the graph owns ordering,
resource hazards and execution.

## Resources

- Create persistent buffers, textures and samplers through the session.
- Import those opaque handles into each graph build.
- Create transient resources through the graph builder when they are needed by
  one graph only.
- Release persistent handles explicitly; disposing the session releases what
  remains.

Graph handles are temporary and expire at the next build. Persistent handles
are generation-checked and cannot be used after release.

## Coordinates and textures

UI bounds, clips and glyph positions use a top-left origin, X to the right and
Y down. Vulkan rendering uses a positive-height viewport with the same
top-left origin, so callers pass `PixelRect` values directly and do not apply a
Y flip. UI shaders use `ndcX = 2*x/width - 1` and
`ndcY = 2*y/height - 1`; an artifact that applies an additional Y inversion is
not a canonical UI artifact.

Atlas and texture row zero is the top row, and UV `(0,0)` is top-left while
`(1,1)` is bottom-right. Any backend conversion is performed once at the
resource/readback boundary. File encoders must verify the chosen row origin
with a top/bottom probe. DPI is not implied by these coordinates.

## Raster and compute

Raster pass descriptions use canonical `DeltaShader.Contract` artifacts. Render
validates their ABI and creates/caches the Vulkan pipeline internally. Producers
provide generated packed data; Render does not compile shaders or duplicate
shader layout declarations.

Depth and stencil are declared through `RasterPipelineDescription` and
`DepthStencilAttachmentDescription`. The graph validates the matching target
and attachment usage.

Compute is expressed with graph primitives:

```text
upload -> compute bind/push/dispatch -> optional readback -> execute
```

`CopyReadback` is the explicit synchronization point for CPU-visible output.
Ordinary `Execute` does not wait for the whole queue.

## Producer boundaries

DeltaRender consumes neutral records from Engine, DeltaXAML and DeltaText. It
does not poll input, own ECS state, shape strings or accept application-specific
storage. Integrations should convert their data into graph features and passes.
