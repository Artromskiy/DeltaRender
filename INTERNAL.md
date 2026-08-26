# DeltaRender internal notes

This file is internal to Render maintainers and is not a cross-project user
contract. The public boundary is [CONTRACT.md](CONTRACT.md); this document
only records implementation ownership and migration notes.

## Ownership

`VulkanWindowSession` owns session-local Vulkan objects only after the
acquisition ledger is committed. Surface creation is represented by one
source-backed lease; the factory owns it before transfer and the session owns it
after transfer. Rollback is reverse-order and preserves the original failure.
Partial swapchain view/framebuffer creation cleans every already-created item.

`VulkanTextAtlasService` owns atlas images, views, samplers, descriptors and
staging. `TextAtlasCache` owns CPU page packing and copies DeltaText image bytes
before the producer image lifetime ends. Page borrows expire after cache
mutation or disposal. The instance buffer uses a reusable grow-only allocation;
replacement is failure-atomic and never leaves a destroyed handle published.

## Shader consumption

The only shader ABI authority is `DeltaShader.Contract`. Core and Vulkan read
`IShaderArtifact`, `IGraphicsShaderProgram` and `ShaderAbi` directly. The
renderer validates stage, entry point, descriptor set/binding, access, layout
offsets/strides and push constants before native pipeline creation. It never
reconstructs a producer manifest or invokes a compiler.

Compute has the same boundary: the canonical artifact overload is the normal
path, while the raw SPIR-V import is a low-level escape hatch that takes the
canonical `ShaderAbi` directly. No `ComputeShaderMetadata` or other local ABI
projection remains in Core, Vulkan, samples or tests.

Text GPU packing is an internal 48-byte std430 `TextGlyphGpu` record with
`PixelMin` at 0, `PixelMax` at 8, `UvRect` at 16 and `Color` at 32. Uploads use
reusable staging and one draw per pipeline/page/clip group. Compute uses the
same std430 rule and explicit transfer/compute/host barriers.

## Frame path

`RenderFramePacket` is the sole frame submission shape. The session receives a
borrowed packet after the caller has begun the frame; the convenience extension
only sequences one begin and one end. UI/text ordering is retained in the
caller-owned draw lists. Render owns no event pump, input translation, ECS
storage, XAML object or game-loop clock.

## Producer gap

The current DeltaText checkout exposes immutable `GlyphImage` values but its
constructor is internal and no public test/fixture factory is present in the
observed API. Render therefore does not add reflection, a duplicate image DTO,
or a native-font workaround. Atlas insertion remains wired to the canonical
`GlyphImage` input and its headless insertion tests are blocked until DeltaText
publishes the agreed factory/producer fixture.
