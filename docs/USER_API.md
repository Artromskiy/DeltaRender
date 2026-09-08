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

## Vulkan validation

`VulkanRendererOptions.EnableValidation` enables validation diagnostics when
the runtime exposes `VK_LAYER_KHRONOS_validation` and the debug-utils
extension. The renderer checks both capabilities before creating the Vulkan
instance. If the layer is not installed, initialization continues without it
and reports a diagnostic warning; applications must not assume that validation
is available on every machine.

Features add ordinary transfer, compute or raster passes. A feature owns its
producer-side state and implements the pass recording; the graph owns ordering,
resource hazards and execution.

## Prepared UI effect programs

`UiDisplayListResourceRegistry` keeps visual and text effect resources in separate
registries. Each resource contains the complete typed `UiEffectResource`
payload. Visual resources reference a prepared `IGraphicsShaderProgram`; text
resources reference a typed `TextShaderVariant` descriptor that selects the existing
generated packer mode:

```csharp
registry.RegisterVisualEffectResource(
    visualEffectResource,
    new UiVisualShaderVariant(
        preparedVisualProgram,
        UiVisualKind.RoundedRectangle,
        UiVisualShaderPath.AnalyticEffect));
registry.RegisterTextEffectResource(
    textEffectResource,
    new TextShaderVariant(preparedTextProgram, GlyphImageMode.Sdf));
```

The registry does not own or dispose the program. During display-list
consumption, `DeltaRender.XAML` matches the complete immutable effect-resource
metadata (set identity, target, capabilities, quality, outsets and typed layer
parameters) before selecting a registered variant for batching. A resource
identity with different metadata is rejected; it cannot silently reuse another
prepared variant. The registry does not compose shader layers, inspect files,
or infer effect parameters from a CLR object.

The generated analytic rounded artifact accepts typed stroke/outline,
outer-shadow and glow layers through its producer-owned packer. Inset-shadow
and cached-mask resources are rejected because this ABI does not expose those
fields. No local CLR ABI or fallback packer is used. A registered visual effect
program must therefore expose the matching prepared ABI before it can be
submitted; a different ABI is rejected with a diagnostic until its generated
packer/adapter is available. A text variant is accepted only when its mode and
resolved bindings, stride and
push-constant range match the configured `TextRenderFeature`; adjacent runs
with different variants become separate raster passes. Missing or incompatible
variants are reported rather than silently falling back to the no-effect path.

Visual registration accepts `UiVisualShaderVariant` together with a typed
`UiEffectResource`. Registration validates the complete program against the
selected generated ABI and packer before it reaches graph submission. A future effect shape must provide a
producer-generated variant and packer, not a Render-local layout or runtime
delegate.

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
