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

The producer also publishes deterministic graphics variants for analytic text
effects without changing shaping, glyph metrics or atlas encoding:

- `sdf-text-outline-glow` uses the existing SDF alpha channel;
- `msdf-text-outline-glow` uses the existing MSDF median-of-RGB distance;
- both variants expose `TextEffectParameters` as one shared 96-byte push
  constant root and retain the glyph storage buffer at set `0`, binding `0`;
- `GlowColor`, `GlowRadius` and `GlowIntensity` use the same distance-field
  units as `DistanceRange` and `OutlineWidth`;
- the generated program exposes the corresponding typed root packers and
  `VertexAbi`/`FragmentAbi`; consumers must use those generated members rather
  than recreate the layout.

These are fixed producer artifacts, not runtime shader composition. Outer shadow,
backdrop blur and a general ordered effect chain remain explicit follow-up work;
backdrop blur is intentionally not part of the analytic text path.

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
