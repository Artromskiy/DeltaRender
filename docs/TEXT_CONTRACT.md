# DeltaRender.Text contract

Status: renderer integration contract. This document defines the boundary of
the `DeltaRender.Text` project; it does not replace or copy the producer-owned
[`DeltaText` public contract](../../DeltaText/PUBLIC_CONTRACT.md).

## Project boundary

`DeltaRender.Text` is the adapter layer between `Delta.Text.Contract` values
and the neutral `Delta.Render.RenderGraph` contract.

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

The current project scaffold adds no standalone public glyph DTO. Existing
DeltaText values remain the sole source of shaping, glyph identity, metrics and
CPU image data. Any future public entry point must be a deliberately approved
adapter boundary and must not copy the DeltaText contract into another model.

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
- transfer uploads for new or dirty atlas/instance ranges;
- raster draws grouped by compatible pipeline, atlas page and clip.

UI rectangles and text remain in one frame submission. The adapter preserves
producer order; grouping must not reorder an `A-B-A` sequence merely to reduce
draw calls.

Shader bindings, member offsets, strides, push constants and stage visibility
come only from the final `DeltaShader.Contract` artifact. DeltaRender.Text
does not publish a second shader ABI or hard-code generated shader names.

## Ownership and lifetime

- DeltaText owns fonts, shaping state and the source `GlyphImage` payload.
- DeltaRender.Text copies pixels and metrics into renderer-owned atlas/cache
  storage before the producer borrow expires.
- The Vulkan session owns image, image-view, sampler, descriptor, buffer and
  staging lifetimes. The adapter never exposes native handles.
- Atlas/page and GPU-instance references are generation-checked; a recycled
  page invalidates old references.
- A borrowed frame/display-list view is used only during its documented frame
  lifetime and is not retained across `Prepare`, cache mutation or dispose.
- Disposal is idempotent. Partial page, descriptor or staging creation must
  roll back in reverse order without replacing the original exception.

## Required validation

The adapter rejects before graph recording:

- empty or invalid glyph image dimensions and byte lengths;
- unsupported image encodings or a mode/format mismatch;
- non-finite positions, metrics, colors or distance parameters;
- stale, foreign or disposed atlas/page references;
- a pipeline/artifact whose manifest does not match the required text stages,
  descriptors or instance layout;
- a clip or target that cannot be represented by the current pass.

Missing advanced features must be an explicit diagnostic, not a silent fallback
to a different shader or atlas format.

## Deliberate exclusions

This project does not own:

- Unicode, bidi, shaping, fallback or font loading;
- strings, XAML layout, hit testing, caret/selection or ECS records;
- Vulkan/SDL handles or window/event polling;
- a second frame packet, direct-submit API or standalone compute device;
- a duplicate `GlyphImage`, `ShapedGlyph` or ShaderAbi model.
