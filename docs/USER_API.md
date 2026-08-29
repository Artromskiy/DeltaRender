# DeltaRender user API

The user-facing renderer API is the same thin RenderGraph contract used by
Engine features. There is no direct frame, packet or standalone compute path.

## Session

A Vulkan implementation creates one `IRenderFrameSession` in one of three
modes:

- compute-only: `Target.IsValid == false`;
- headless graphics: `Target` is an offscreen image;
- windowed: `Target` is presentable.

The creation mechanism is implementation/platform setup, not a second
cross-project rendering API. Once created, every mode uses the same calls:

```csharp
IRenderGraph graph = session.CreateRenderGraph();
graph.Build(frameNumber, features);
RenderGraphExecutionResult result = graph.Execute();
```

## Features and passes

A feature stores its current submission and declares ordinary graph passes:

```csharp
public sealed class FullscreenFeature : IRenderFeature, IRasterPass
{
    private readonly RenderTargetHandle _target;
    private readonly RasterPassDescription _pass;

    public FullscreenFeature(
        RenderTargetHandle target,
        RasterPassDescription pass)
    {
        _target = target;
        _pass = pass;
    }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        RenderGraphTextureHandle target = graph.ImportTarget(_target);
        RenderGraphPassHandle pass = graph.AddRasterPass(_pass, this);
        graph.UseColorAttachment(
            pass,
            0,
            new ColorAttachmentDescription(
                target,
                AttachmentLoadOperation.Clear,
                AttachmentStoreOperation.Store));
    }

    public void Record(IRasterCommandContext commands)
    {
        commands.Draw(3);
    }
}
```

Text, UI and mesh integrations follow the same pattern. Their source records
belong to DeltaText, DeltaXAML or Engine; only GPU resources and pass commands
belong to Render.

Raster pipelines can opt into depth and stencil testing without exposing
Vulkan state. Set `DepthTest`, `DepthWrite`, `DepthCompareOperation` and
`StencilState` on `RasterPipelineDescription`, then declare the matching
`DepthStencilAttachmentDescription` with `UseDepthStencilAttachment`.

## Compute and readback

A compute feature composes the former standalone workflow from graph
primitives:

```text
transfer pass: UploadBuffer(input)
compute pass:  BindBuffer + PushConstants + Dispatch
readback:      ReadbackBuffer(output range)
execute
CopyReadback(result, destination)
```

Repeated `UploadBuffer` commands are the range/batch API. The contract does not
also define `UploadRanges` or an application-specific dirty-record method.
Only `CopyReadback` may wait for the GPU; ordinary execution remains
asynchronous with respect to CPU work.

## Persistent and transient resources

Use session creation methods for data that survives more than one graph build.
Import those handles in each build. Use builder creation methods for transient
data whose lifetime is confined to the current graph; Render may alias their
memory when lifetimes do not overlap.

Release persistent handles explicitly. Disposing the session releases every
remaining resource. Graph-local handles and readback handles expire at the
next build.

## Shaders

Raster and compute pass descriptions accept canonical
`DeltaShader.Contract` artifacts. Render validates `ShaderAbi`, constructs and
caches Vulkan pipelines internally, and binds resources by the producer-owned
`ShaderBinding`. User code never creates a public pipeline object.
