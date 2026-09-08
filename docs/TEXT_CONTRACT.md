# DeltaRender.Text contract

Status: internal source-adapter integration contract, not a public NuGet API.
This document defines the boundary of the `DeltaRender.Text` project; it does
not replace or copy the producer-owned
[`DeltaText` public contract](../../DeltaText/PUBLIC_CONTRACT.md).

## Project boundary

`DeltaRender.Text` is the adapter layer between `Delta.Text.Contract` values
and the neutral `Delta.Render.RenderGraph` contract.

The reusable entry point is `Delta.Render.Text.TextRenderFeature`:

```csharp
using var text = new TextRenderFeature(
    session,
    textService,
    textShaderProgram,
    new PixelExtent(width, height));

text.AddRun(shapedText, originX, originY, color, clip);
graph.Build(frameNumber, features); // the feature is in features
graph.Execute();
text.Clear(); // after Execute, before queueing the next frame
```

`AddRun` accepts already shaped `ShapedText`; it does not accept source strings
and does not perform shaping. During graph build it synchronously consumes the
shaped value, copies glyph pixels into the adapter-owned atlas and encodes
compact instances. The shaped value and its glyph memory must remain valid
until `Execute` completes.

```text
Delta.Text.Contract
  ITextService / ShapedText / GlyphImage
          -> DeltaRender.Text
          -> IRenderFeature / ordinary graph passes
          -> Delta.Render.Vulkan
```

The project references `Delta.Render` and `Delta.Text` only. It must not add a
dependency from `Delta.Render` or `Delta.Render.Vulkan` back to `Delta.Text`.
It has no dependency on DeltaXAML, DeltaEngine or DeltaECS.

The feature adds no standalone public glyph DTO. Existing DeltaText values
remain the sole source of shaping, glyph identity, metrics and CPU image data.
The feature itself is the deliberately narrow adapter boundary and does not
copy the DeltaText contract into another model.

## Input

The adapter consumes producer-owned values:

- `ITextService` for font instances and glyph image generation;
- `ShapedText`, `ShapedRun` and `ShapedGlyph` for already shaped glyph order,
  positions and font identity;
- `GlyphImageRequest` and `GlyphImage` for the requested representation and
  tightly packed pixels;
- the existing Render UI/frame boundary for screen position, color, clip and
  draw order.

Strings, shaping requests, font handles, XAML elements and ECS storage do not
cross into the renderer graph. DeltaRender.Text never shapes text and never
retains a borrowed DeltaText memory owner after the handoff operation.

## Output

The adapter produces ordinary `IRenderFeature` work for one graph build. Its
GPU-facing work is represented by normal graph resources and passes:

- a renderer-owned atlas page image and sampled-image descriptor;
- a renderer-owned glyph-instance storage buffer;
- one bounded atlas upload when the page changes and one instance upload;
- raster draws grouped by adjacent compatible clip batches on the page.

UI rectangles and text remain in one frame submission. The adapter preserves
producer order; grouping must not reorder an `A-B-A` sequence merely to reduce
draw calls.

## Coordinates and atlas origin

Text origins, glyph plane positions, bounds and clips use the shared UI
convention: top-left origin, X right, Y down, depth `0..1`. Atlas UV `(0,0)` is
the top-left texel and `(1,1)` is the bottom-right texel. Atlas row zero is the
top row; the adapter does not apply a second row or Y flip. DPI scaling is not
part of this contract.

Shader bindings, member offsets, strides, push constants and stage visibility
come only from the final `DeltaShader.Contract` artifact. DeltaRender.Text
does not publish a second shader ABI or hard-code generated shader names.

## Ownership and lifetime

- DeltaText owns fonts, shaping state and the source `GlyphImage` payload.
- DeltaRender.Text copies pixels and metrics into renderer-owned atlas/cache
  storage before the producer borrow expires.
- The session creates and owns native image, image-view, sampler, descriptor,
  buffer and staging lifetimes. The feature owns valid session handles and
  releases them idempotently; it never exposes native handles.
- The first implementation has one bounded persistent page per feature. A glyph
  that does not fit fails deterministically and asks the caller to create a
  larger feature; it does not silently evict or grow a live image.
- A borrowed shaped value is used only during the documented build/execute
  lifetime and is not retained after `Clear` or dispose.
- Partial resource creation rolls back in reverse order without replacing the
  original exception.

## Required validation

The adapter rejects before graph recording:

- empty or invalid glyph image dimensions and byte lengths;
- unsupported image encodings or a mode/format mismatch;
- non-finite positions, metrics, colors or distance parameters;
- stale, foreign or disposed atlas/page references;
- a pipeline/artifact whose manifest has no unique read-only vertex storage
  buffer or fragment sampled/combined image resource;
- a clip or target that cannot be represented by the current pass.

Missing advanced features must be an explicit diagnostic, not a silent fallback
to a different shader or atlas format. The supplied shader manifest remains the
authority for descriptor bindings; the feature does not know generated wrapper
names or duplicate ShaderAbi declarations.

## Analytic text effects

The producer publishes deterministic SDF/MSDF graphics variants for analytic
text effects without changing shaping, glyph metrics or atlas encoding. The
generated program remains the sole source of bindings, push-constant layout
and typed packers. Every text fragment returns premultiplied RGBA, and every
matching raster pipeline uses `PremultipliedAlpha` blending.

Outer shadow and outer glow are ordered geometry layers, not shifted atlas
samples:

- the shadow vertex shader translates `GlyphInstance.PixelMin` and
  `GlyphInstance.PixelMax` by `OuterShadowOffset` in device pixels;
- the shadow fragment shader samples the original glyph UV and emits only
  premultiplied shadow output;
- the glow vertex shader expands the glyph paint quad by the typed glow radius;
- the glow fragment shader samples the original glyph UV and emits only
  premultiplied glow output;
- positive X moves the layer right and positive Y moves it down under the
  shared top-left UI convention;
- the base layer then draws fill and stroke with the unmodified glyph geometry;
- for one ordered text item, all shadow glyphs are recorded first, then all
  glow glyphs, then the base glyphs. Adjacent decorated items are not merged
  when that would change this `shadow -> glow -> base` ordering.

The offset changes placement only. It does not increase SDF/MSDF distance
range or atlas padding. Required analytic range is the maximum of stroke
width, outer-glow radius and shadow width + spread + blur radius. SDF/MSDF
samples encode `0.5 + signedDistance / (2 * DistanceRange)` and every variant
decodes with the matching `2 * DistanceRange` scale.

`TextRenderFeature` receives effect dimensions in device pixels and promotes
its persistent SDF/MSDF atlas monotonically through `4`, `8`, `16` and `32`
pixel distance-range tiers. DeltaText derives matching glyph-image padding and
expanded plane bounds from the selected range, so the packed destination quad
covers fill and local analytic paint while each atlas cell remains isolated.
A large translation alone remains on the same tier. A blur/spread reach above
the maximum automatic tier fails deterministically and requires a `CachedMask`
effect or an explicitly configured larger base range.

These are fixed producer artifacts, not runtime shader composition. The
`OuterGlowOnly` artifacts are companion programs for a separate glow pass;
they do not change shaped text, metrics, baseline, layout bounds or atlas
representation. A general ordered effect chain and cached-mask/backdrop-blur
paths remain explicit follow-up work; backdrop blur is intentionally not part
of the analytic path.

## Prepared variant matrix

The current producer catalog has 10 exact artifact identities:

- SDF: standard, stroke, outer glow, outer shadow and stroke + outer glow;
- MSDF: standard, stroke, outer glow, outer shadow and stroke + outer glow.

Each base/effect identity maps to its own generated graphics program and typed
instance and parameter packers. The SDF and MSDF `OuterGlowOnly` companions
use the same glyph-instance payload and are selected by layered registration.
`UiDisplayListResourceRegistry` accepts a text entry only when the
`TextShaderVariant` mode/path and the immutable effect capability set match
that identity. `TextShaderPacking` then selects the corresponding generated
packer; it does not duplicate ShaderAbi layout or infer a different variant.
An `OuterShadow` or `OuterGlow` effect plan combines its prepared layer with
the standard base layer. `Stroke | OuterShadow | OuterGlow` combines prepared
outer-shadow and outer-glow layers with the prepared stroke base; there is no
combined outer-shadow shader artifact. Text `CachedMask` has no prepared entry
and is intentionally rejected until a producer-owned text artifact and
matching atlas contract exist.

## Deliberate exclusions

This project does not own:

- Unicode, bidi, shaping, fallback or font loading;
- strings, XAML layout, hit testing, caret/selection or ECS records;
- Vulkan/SDL handles or window/event polling;
- a second frame packet, direct-submit API or standalone compute device;
- a duplicate `GlyphImage`, `ShapedGlyph` or ShaderAbi model;
- a complete borrowed `UiDisplayList` adapter or cross-producer visual/text
  ordering policy. A future XAML adapter must flatten its effective clips and
  call this feature synchronously without passing XAML types here.
