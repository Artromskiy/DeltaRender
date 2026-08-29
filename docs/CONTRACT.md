# DeltaRender cross-project contract

This is the complete cross-project contract supplied by DeltaRender. The only
supported GPU execution model is the Vulkan RenderGraph declared by the flat
sources in `src/DeltaRender`. A window, an offscreen target and a compute-only
session use the same interfaces and the same Vulkan executor.

The contract assembly is `DeltaRender`; public CLR namespaces are
`Delta.Render` and `Delta.Render.RenderGraph`. Vulkan, SDL3 and MoltenVK objects
never cross this boundary.

## Ownership

- **Producer and implementer:** DeltaRender.
- **Direct consumers:** DeltaEngine render features and DeltaEditor
  composition.
- **Shader input:** final `DeltaShader.Contract` artifacts and `ShaderAbi`.
- **UI/text input:** consumer adapters translate `DeltaXAML.Contract` and
  `DeltaText.Contract` values into ordinary graph resources and passes.

The renderer owns resources, target acquisition, synchronization, command
recording, submission, readback staging and presentation. Engine owns frame
policy and all clocks. The contract contains no `DeltaTime`.

## Complete public surface

### Session and persistent resources

`IRenderFrameSession` is the sole renderer lifetime boundary:

```csharp
public interface IRenderFrameSession : IAsyncDisposable
{
    RenderDeviceCapabilities Capabilities { get; }
    RenderTargetHandle Target { get; }

    IRenderGraph CreateRenderGraph();

    RenderBufferHandle CreateBuffer(in RenderBufferDescription description);
    RenderTextureHandle CreateTexture(in RenderTextureDescription description);
    RenderSamplerHandle CreateSampler(in RenderSamplerDescription description);

    void Release(RenderBufferHandle buffer);
    void Release(RenderTextureHandle texture);
    void Release(RenderSamplerHandle sampler);

    void ResizeTarget(in PixelExtent extent);
}
```

`Target` has three meanings without three interfaces:

| Session | `Target` | Execute epilogue |
|---|---|---|
| Compute-only headless | invalid | submit only |
| Offscreen graphics | valid offscreen target | submit only |
| Windowed | valid presentable target | acquire, submit, present |

`ResizeTarget` is valid only for sessions with a target. Multi-window
composition uses one session per target; the first contract deliberately does
not expose an array of views or surfaces.

Persistent handles are session-owned and generation-checked. Graph-local
handles are compact build-local indices and expire at the next `Build`.

### Graph lifecycle

```csharp
public interface IRenderGraph
{
    void Build(ulong frameNumber, ReadOnlySpan<IRenderFeature> features);
    RenderGraphExecutionResult Execute();
    int CopyReadback(RenderGraphReadbackHandle readback, Span<byte> destination);
}

public interface IRenderFeature
{
    void AddPasses(IRenderGraphBuilder graph, ulong frameNumber);
}
```

Features own or borrow their submitted domain data. `Build` asks them to add
passes; it does not retain the feature span. `Execute` compiles and records the
declared graph, then returns `Submitted`, `NoWork`, `Failed` or `DeviceLost`
with shared `Delta.Diagnostics` values.

`CopyReadback` is the only operation that may wait for GPU completion. Normal
windowed and headless execution does not wait for a queue to become idle.
Readback handles belong to the last successful build and are invalidated by the
next build.

### Graph builder

`IRenderGraphBuilder` contains only primitives that cannot be reconstructed
safely outside Render:

- import the session target or persistent buffer/texture;
- create a transient buffer/texture;
- add raster, compute or transfer passes;
- declare attachment and resource access;
- request a buffer or texture readback.

Readback is explicit because CPU visibility, staging ownership and completion
cannot be expressed by ordinary GPU copy commands. All other batching remains
consumer composition: repeated `UploadBuffer` calls express dirty ranges, and
ordinary passes express fullscreen, UI, text, mesh and compute work.

### Pass recording

```text
IRasterPass   -> IRasterCommandContext
IComputePass  -> IComputeCommandContext
ITransferPass -> ITransferCommandContext
```

Raster commands set viewport/scissor, bind vertex/index/shader resources and
draw. Compute commands bind shader resources, write push constants and
dispatch. Transfer commands copy or upload buffers and textures. Resource use
is declared on the builder before `Record`, so Render can derive ordering,
lifetimes, layouts and Vulkan barriers before command recording.

Pass and pipeline descriptions consume only final DeltaShader artifacts:

```text
RasterPipelineDescription  -> IGraphicsShaderProgram
ComputePassDescription     -> IShaderArtifact(stage = Compute)
```

Pipeline objects and cache keys are renderer-owned. They are not public
handles and are never created by a feature.

### Depth, Z-test and stencil contract

Raster pipeline state uses `DepthTest`, `DepthWrite` and
`DepthCompareOperation` for Z testing. `RenderStencilState` enables stencil
testing and supplies independent front/back `RenderStencilFaceState` values:
compare operation, fail/depth-fail/pass operations, reference value and
read/write masks.

`UseDepthStencilAttachment` binds a `DepthStencilAttachmentDescription` to a
raster pass. Depth and stencil load/store operations are independent; the
shared `ClearDepthStencil` value supplies both clear values. A pipeline that
enables depth or stencil testing requires a compatible depth/stencil
attachment in the same raster pass. The Vulkan implementation must reject an
incompatible format, missing attachment or invalid state before recording
native commands.

## Compute coverage

The graph contract covers every supported former direct-compute operation:

```text
Create persistent storage       -> session.CreateBuffer
Upload one or many ranges        -> transfer pass + UploadBuffer calls
Bind descriptor resources       -> compute context BindBuffer/BindTexture
Push ABI bytes                   -> compute context PushConstants
Dispatch                         -> compute context Dispatch
GPU ordering/barriers            -> declared UseBuffer/UseTexture dependencies
Read result on CPU               -> builder.ReadbackBuffer + graph.CopyReadback
Query Vulkan limits              -> session.Capabilities
Apply dirty renderer records     -> consumer forms upload ranges
No-op dispatch                   -> graph execution status NoWork
```

Raw SPIR-V overloads do not belong here. DeltaShader owns construction of the
final artifact; DeltaRender consumes `IShaderArtifact` and does not publish a
second shader import model.

## Deliberately excluded

The contract has no direct frame packet, begin/end frame pair, standalone
compute device, public pipeline factory, fullscreen helper, text pipeline,
atlas device, UI render batch, render-record journal, surface handle, view
array or generic feature submission facade.

These shapes either duplicate graph composition or expose one adapter's
internal data. In particular:

- fullscreen is a raster pass followed by `Draw(3)`;
- UI, text and meshes are feature-owned pass data;
- dirty uploads are repeated transfer commands;
- compute is a compute pass plus optional transfer/readback passes;
- pipeline creation is derived from pass descriptions and cached internally;
- presentation is selected by the session target, not by a second API.

No consumer may recreate one of these removed paths as a public compatibility
facade. The migration is defined in [MIGRATION.md](MIGRATION.md).

## Lifetime rules

- Session handles remain valid until released or the session is disposed.
- Graph-local handles remain valid only until the next `Build`.
- Feature and pass objects may be reused; their borrowed payloads must remain
  valid through `Execute`.
- Upload spans are copied into renderer-owned staging during recording.
- Readback becomes available only after a successful `Execute`; copying it may
  wait for the producing submission.
- The session is the only disposer of Vulkan resources. A handle does not own a
  native resource by itself.
