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
        UiVisualShaderPath.RoundedStrokeEffect));
registry.RegisterTextEffectResource(
    textEffectResource,
    new TextShaderVariant(
        preparedTextProgram,
        GlyphImageMode.Sdf,
        TextShaderPath.Stroke));
```

The registry does not own or dispose the program. During display-list
consumption, `DeltaRender.XAML` matches the complete immutable effect-resource
metadata (set identity, target, capabilities, quality, outsets and typed layer
parameters) before selecting a registered variant for batching. A resource
identity with different metadata is rejected; it cannot silently reuse another
prepared variant. The registry does not compose shader layers, inspect files,
or infer effect parameters from a CLR object.

Outer glow is available only through a separate `OuterGlowOnlyEffect` layer;
the registry does not accept a composite glow artifact. The generated base and
effect-layer packers remain the single ABI authority, and the canonical
`InnerGlow` capability is diagnosed until a matching generated artifact is
registered.

For a long visual outer shadow, register the generated shadow-only artifact
next to the generated base artifact:

```csharp
registry.RegisterVisualEffectResourceLayers(
    effectResource,
    new UiVisualShaderVariant(
        preparedShadowProgram,
        UiVisualKind.RoundedRectangle,
        UiVisualShaderPath.OuterShadowOnlyEffect),
    new UiVisualShaderVariant(
        preparedBaseProgram,
        UiVisualKind.RoundedRectangle,
        UiVisualShaderPath.Standard));
```

The adapter records the shadow and base as ordered raster layers. The
shadow-only vertex artifact expands its raster quad by the typed offset,
spread and blur extent, while its fragment artifact emits only shadow color;
the base artifact then draws the original visual geometry. Both layers use
the same renderer-owned instance buffer with separate packed ranges, so this
path does not require a cached mask or a second UI tree. A `Stroke` plus
`OuterShadow` resource uses `RoundedStrokeEffect` as its base variant. The
effect outsets remain paint/damage bounds and never change layout size.

Register an outer glow the same way when it must be an independent paint layer:

```csharp
registry.RegisterVisualEffectResourceGlowLayers(
    effectResource,
    new UiVisualShaderVariant(
        preparedGlowProgram,
        UiVisualKind.RoundedRectangle,
        UiVisualShaderPath.OuterGlowOnlyEffect),
    new UiVisualShaderVariant(
        preparedBaseProgram,
        UiVisualKind.RoundedRectangle,
        UiVisualShaderPath.Standard));
```

The visual glow pass is recorded before the base pass and uses an expanded
paint quad; the base pass keeps the original geometry. Text uses the equivalent
`RegisterTextEffectResourceGlowLayers` registration with
`TextShaderPath.OuterGlowOnly`. Its glyph quad is expanded by the typed glow
radius, while shaping, font metrics, baseline and layout bounds stay unchanged.
For an effect set containing stroke, shadow and glow, use the three-argument
`RegisterTextEffectResourceLayers` overload to register shadow, glow-only and
stroke-base variants in that order. There is no combined text stroke/glow
artifact.

A cached-mask visual uses the separate generated
`CachedMaskRoundedRectangle` artifact. Register its session-owned texture,
sampler and normalized UV rectangle first:

```csharp
registry.RegisterMask(maskResource, maskTexture, maskSampler, uvRect);
registry.RegisterVisualEffectResource(cachedMaskResource, cachedMaskVariant);
```

The adapter binds the sampled mask according to the generated `ShaderAbi` and
uses the generated instance packer. Missing mask registration is rejected; no
solid fallback is used. A registered visual effect program must
therefore expose the matching prepared ABI before it can be submitted; a
different ABI is rejected with a diagnostic until its generated packer/adapter
is available. The generated text glow-only artifact is registered separately
from the text base artifact. A text variant is accepted only when its
mode and resolved bindings, stride and
push-constant range match the configured `TextRenderFeature`; adjacent runs
with different variants become separate raster passes. Missing or incompatible
variants are reported rather than silently falling back to the no-effect path.

Visual registration accepts `UiVisualShaderVariant` together with a typed
`UiEffectResource`. Registration validates the complete program against the
selected generated ABI and packer before it reaches graph submission. An
application-owned render loop can update effect values without recreating the
prepared program or pipeline:

```csharp
registry.UpdateVisualEffectResource(updatedEffectResource);
feature.Consume(displayList);
```

The updated resource must retain its `UiResourceId`, target, capabilities and
quality. Layer values and outsets may change; the matching updated effect set
must be present in the display list. The next `Consume` repacks and uploads
only visuals that reference the updated resource; it retains their prepared
pipeline and instance layout. A future effect shape must provide a
producer-generated variant and packer, not a Render-local layout or runtime
delegate.

The current prepared catalog contains 12 visual entries: base and effect-layer
entries for solid, rounded, gradient and image rendering, plus the
`solid.outer-shadow.shadow-only` and
`rounded.outer-shadow.shadow-only` layer artifacts. It contains 12 text
entries:
`text.sdf`, `text.msdf`, `text.sdf.stroke`, `text.msdf.stroke`,
`text.sdf.outer-glow`, `text.msdf.outer-glow`, `text.sdf.outer-shadow` and
`text.msdf.outer-shadow`.
The generated SDF/MSDF `OuterGlowOnly` programs are companion layer artifacts
selected by layered registration; they do not create a runtime shader
composition path or a second text representation.
The catalog is metadata for exact lookup, not a request to compose effects at
runtime. Text `CachedMask` is not in the prepared catalog and remains an
explicit unsupported text path.

For every prepared entry, the registry resolves one producer-owned program;
the runtime validates its entry points and exact `VertexAbi`/`FragmentAbi`,
then calls the generated instance/frame packers selected by that entry. Render
does not probe files, reflect over payloads or recalculate offsets and
strides. A missing identity, ABI or packer is rejected diagnostically.

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

Raster pipelines support these renderer-owned fixed-function blend modes. For
custom fixed-function behavior, pass a `RenderBlendState` to
`RasterPipelineDescription`; it specifies source/destination factors and
operations independently for color and alpha.

- `Opaque`: blending disabled.
- `Alpha`: `src * srcAlpha + dst * (1 - srcAlpha)`.
- `PremultipliedAlpha`: `src + dst * (1 - srcAlpha)`; this is the default for premultiplied UI/text/effect output.
- `Additive`: existing straight-alpha additive behavior, `src * srcAlpha + dst`. It is not a premultiplied-additive mode and must not be used for premultiplied glow without an explicitly approved future mode.
- `Multiply`: `src * dst + dst * (1 - srcAlpha)`.

Effect layers inherit the source paint's blend state. The default UI glow path
therefore uses ordinary premultiplied alpha blending; additive or other custom
fixed-function behavior must be selected explicitly by the caller.

The blend state is part of `RasterPipelineDescription`, so changing it selects
an independent cached Vulkan pipeline. Blend modes do not require a shader
variant or a new payload ABI.

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
