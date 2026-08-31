using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe partial class VulkanRenderGraph : IRenderGraph, IRenderGraphBuilder, IAsyncDisposable
{
    private readonly VulkanRenderSession _session;
    private readonly List<GraphResource> _resources = new();
    private readonly List<GraphPass> _passes = new();
    private readonly List<GraphPass> _passPool = new();
    private readonly VulkanGraphReadback _readback;
    private ResourceState[] _states = [];
    private int[] _order = Array.Empty<int>();
    private VulkanRasterCommandContext? _rasterContext;
    private VulkanComputeCommandContext? _computeContext;
    private VulkanTransferCommandContext? _transferContext;
    private GraphResource? _depthAttachmentResource;
    private GraphResource? _targetResource;
    private DepthStencilAttachmentDescription _depthAttachment;
    private bool _hasDepthAttachment;
    private bool _built;
    private bool _disposed;

    internal VulkanRenderGraph(VulkanRenderSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _readback = new VulkanGraphReadback(this);
    }

    public void Build(ulong frameNumber, ReadOnlySpan<IRenderFeature> features)
    {
        ThrowIfDisposed();
        var profiler = _session.ProfilerState;
        var buildStart = profiler?.StartPhase() ?? 0;
        profiler?.BeginBuild(frameNumber);
        ResetBuild();
        _session.ReclaimDeferredTransientsForBuild();
        try
        {
            for (var index = 0; index < features.Length; index++)
            {
                ArgumentNullException.ThrowIfNull(features[index]);
                features[index].AddPasses(this, frameNumber);
            }

            ConfigureRenderPass();
            _order = _dependencyPlanner.Compile(_passes, _resources.Count);
            ValidateRasterSegment();
            ValidateRasterPipelines();
            _built = true;
        }
        catch
        {
            ResetBuild();
            throw;
        }
        finally
        {
            profiler?.EndBuild(buildStart);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Execute is the graph recovery boundary; it must abort the active Vulkan frame and return Failed for feature-recording failures.")]
    public RenderGraphExecutionResult Execute()
    {
        ThrowIfDisposed();
        if (!_built)
        {
            throw new InvalidOperationException("A render graph must be built before Execute.");
        }

        if (_order.Length == 0)
        {
            _built = false;
            _session.ProfilerState?.Complete(RenderGraphExecutionStatus.NoWork, _resources.Count);
            return new RenderGraphExecutionResult(RenderGraphExecutionStatus.NoWork, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);
        }

        var profiler = _session.ProfilerState;
        var acquireStart = profiler?.StartPhase() ?? 0;
        try
        {
            if (!_session.BeginGraphFrame())
            {
                profiler?.Complete(RenderGraphExecutionStatus.Failed, _resources.Count);
                return Failed();
            }
        }
        catch (VulkanOperationException exception)
        {
            profiler?.Complete(ClassifyFailure(exception), _resources.Count);
            return Failed(exception);
        }
        finally
        {
            profiler?.EndAcquire(acquireStart);
        }

        if (_states.Length < _resources.Count)
        {
            EnsureStateCapacity(_resources.Count);
        }

        Array.Clear(_states, 0, _resources.Count);
        var states = _states;
        var rasterActive = false;
        var recordStart = profiler?.StartPhase() ?? 0;
        try
        {
            profiler?.BeginGpuFrame(_session.CommandBuffer, _order.Length);
            for (var orderPosition = 0; orderPosition < _order.Length; orderPosition++)
            {
                var passIndex = _order.RefAt(orderPosition);
                var pass = _passes[passIndex];
                var passProfile = -1;
                var passStart = 0L;
                if (profiler is not null)
                {
                    passProfile = profiler.BeginPass(pass.Name, ToProfilePassKind(pass.Kind), _session.CommandBuffer, out passStart);
                }

                try
                {
                    EmitBarriers(pass, states);
                    if (pass.Kind == PassKind.Raster)
                    {
                        if (!rasterActive)
                        {
                            EmitRasterSegmentEntryBarriers(orderPosition, states);
                            BeginRaster(pass);
                            rasterActive = true;
                        }

                        var rasterPipeline = pass.Pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.");
                        rasterPipeline.BeginBindings();
                        if (pass.Raster is { } raster)
                        {
                            var context = _rasterContext;
                            if (context is null)
                            {
                                context = new VulkanRasterCommandContext(this, rasterPipeline);
                                _rasterContext = context;
                            }
                            else
                            {
                                context.Rebind(rasterPipeline);
                            }

                            raster.Record(context);
                        }
                    }
                    else
                    {
                        if (rasterActive)
                        {
                            CommandWriter.EndRenderPass();
                            rasterActive = false;
                        }

                        if (pass.Kind == PassKind.Compute)
                        {
                            var computePipeline = pass.Pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline.");
                            computePipeline.BeginBindings();
                            if (pass.Compute is { } compute)
                            {
                                var context = _computeContext;
                                if (context is null)
                                {
                                    context = new VulkanComputeCommandContext(this, computePipeline);
                                    _computeContext = context;
                                }
                                else
                                {
                                    context.Rebind(computePipeline);
                                }

                                compute.Record(context);
                            }
                        }
                        else
                        {
                            if (pass.Transfer is { } transfer)
                            {
                                var context = _transferContext ??= new VulkanTransferCommandContext(this);
                                transfer.Record(context);
                            }
                        }
                    }

                    UpdateStates(pass, states);
                }
                finally
                {
                    if (profiler is not null)
                    {
                        profiler.EndPass(passProfile, passStart, _session.CommandBuffer);
                    }
                }
            }

            if (rasterActive)
            {
                CommandWriter.EndRenderPass();
            }

            _readback.Record(states);
            profiler?.EndRecord(recordStart);
            var submitStart = profiler?.StartPhase() ?? 0;
            if (!_session.EndGraphFrame())
            {
                profiler?.EndSubmitAndPresent(submitStart);
                profiler?.Complete(RenderGraphExecutionStatus.Failed, _resources.Count);
                return Failed();
            }
            profiler?.EndSubmitAndPresent(submitStart);

            _built = false;
            profiler?.Complete(RenderGraphExecutionStatus.Submitted, _resources.Count);
            return new RenderGraphExecutionResult(RenderGraphExecutionStatus.Submitted, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);
        }
        catch (VulkanOperationException exception)
        {
            _session.AbortGraphFrame();
            profiler?.EndRecord(recordStart);
            profiler?.Complete(ClassifyFailure(exception), _resources.Count);
            return Failed(exception);
        }
        catch
        {
            _session.AbortGraphFrame();
            profiler?.EndRecord(recordStart);
            profiler?.Complete(RenderGraphExecutionStatus.Failed, _resources.Count);
            return Failed();
        }
    }

    private void EnsureStateCapacity(int required)
    {
        if (_states.Length >= required)
        {
            return;
        }

        var capacity = _states.Length == 0 ? 8 : checked(_states.Length * 2);
        _states = new ResourceState[Math.Max(capacity, required)];
    }

    public int CopyReadback(RenderGraphReadbackHandle readback, Span<byte> destination)
    {
        ThrowIfDisposed();
        return _readback.Copy(readback, destination);
    }

    public ValueTask DisposeAsync()
    {
        DisposeGraph();
        return ValueTask.CompletedTask;
    }

    internal void DisposeGraph()
    {
        if (_disposed)
        {
            return;
        }

        ResetBuild();
        _disposed = true;
    }

    public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target)
    {
        ThrowIfMutable();
        if (!_session.HasTarget || target != _session.Target)
        {
            throw new InvalidOperationException("The target handle does not belong to this session.");
        }

        if (_targetResource is null)
        {
            _targetResource = GraphResource.Target();
            AddResource(_targetResource);
        }

        return new RenderGraphTextureHandle(checked((uint)_targetResource.Index + 1u));
    }

    public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture)
    {
        ThrowIfMutable();
        if (!_session.TryGetTexture(texture, out var value))
        {
            throw new InvalidOperationException("The texture handle is unknown or stale.");
        }

        return new RenderGraphTextureHandle(AddResource(GraphResource.FromTexture(value)));
    }

    public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer)
    {
        ThrowIfMutable();
        if (!_session.TryGetBuffer(buffer, out var value))
        {
            throw new InvalidOperationException("The buffer handle is unknown or stale.");
        }

        return new RenderGraphBufferHandle(AddResource(GraphResource.FromBuffer(value)));
    }

    public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient texture description is invalid.", nameof(description));
        }

        return new RenderGraphTextureHandle(AddResource(GraphResource.OwnedTexture(_session.CreateTransientTexture(description), description)));
    }

    public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient buffer description is invalid.", nameof(description));
        }

        var allocation = _session.CreateTransientBuffer(description);
        return new RenderGraphBufferHandle(AddResource(GraphResource.OwnedBuffer(allocation, description)));
    }

    public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
    {
        ThrowIfMutable();
        ArgumentNullException.ThrowIfNull(pass);
        if (!_session.HasTarget)
        {
            throw new InvalidOperationException("Raster passes require a graphics target.");
        }

        var pipeline = _session.GetOrCreateRasterPipeline(description.Pipeline);

        var graphPass = RentPass(description.Name, PassKind.Raster, pipeline);
        graphPass.Raster = pass;
        graphPass.PipelineDescription = description.Pipeline;
        _passes.Add(graphPass);
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
    {
        ThrowIfMutable();
        ArgumentNullException.ThrowIfNull(pass);
        var pipeline = _session.GetOrCreateComputePipeline(description.Shader);

        var graphPass = RentPass(description.Name, PassKind.Compute, pipeline);
        graphPass.Compute = pass;
        _passes.Add(graphPass);
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
    {
        ThrowIfMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pass);
        var graphPass = RentPass(name, PassKind.Transfer, null);
        graphPass.Transfer = pass;
        _passes.Add(graphPass);
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    private GraphPass RentPass(string name, PassKind kind, VulkanGraphPipeline? pipeline)
    {
        if (_passPool.Count == 0)
        {
            return new GraphPass(name, kind, pipeline);
        }

        var poolIndex = _passPool.Count - 1;
        var pass = _passPool[poolIndex];
        _passPool.RemoveAt(poolIndex);
        pass.Reset(name, kind, pipeline);
        return pass;
    }

    public void UseColorAttachment(RenderGraphPassHandle pass, uint index, in ColorAttachmentDescription attachment)
    {
        var graphPass = GetPass(pass);
        if (graphPass.Kind != PassKind.Raster || index != 0)
        {
            throw new NotSupportedException("Only color attachment index zero is supported.");
        }

        var resource = GetTexture(attachment.Texture);
        if (!resource.IsTarget)
        {
            throw new NotSupportedException("Only the session-owned target can be a color attachment.");
        }

        graphPass.Color = attachment;
        AddUse(graphPass, resource, RenderResourceAccess.Write, RenderPipelineStages.ColorOutput);
    }

    public void UseDepthStencilAttachment(RenderGraphPassHandle pass, in DepthStencilAttachmentDescription attachment)
    {
        var graphPass = GetPass(pass);
        if (graphPass.Kind != PassKind.Raster)
        {
            throw new InvalidOperationException("Depth-stencil attachments can only be used by raster passes.");
        }

        var resource = GetTexture(attachment.Texture);
        var texture = resource.Texture ?? throw new InvalidOperationException("The depth-stencil attachment must reference a registered texture.");
        if (texture.Format is not (Format.D32Sfloat or Format.D24UnormS8Uint))
        {
            throw new ArgumentException("The attachment texture must use D32Float or D24UnormS8UInt.", nameof(attachment));
        }

        if (texture.Extent.Width != _session.GraphExtent.Width || texture.Extent.Height != _session.GraphExtent.Height)
        {
            throw new ArgumentException("The depth-stencil texture extent must match the render target.", nameof(attachment));
        }

        if (_hasDepthAttachment && (_depthAttachmentResource != resource || _depthAttachment != attachment))
        {
            throw new InvalidOperationException("All raster passes in a render segment must use the same depth-stencil attachment description.");
        }

        _depthAttachmentResource = resource;
        _depthAttachment = attachment;
        _hasDepthAttachment = true;
        graphPass.DepthStencil = attachment;
        graphPass.HasDepthStencil = true;
        AddUse(graphPass, resource, RenderResourceAccess.ReadWrite, RenderPipelineStages.DepthStencil);
    }

    public void UseTexture(RenderGraphPassHandle pass, RenderGraphTextureHandle texture, RenderResourceAccess access, RenderPipelineStages stages)
        => AddUse(GetPass(pass), GetTexture(texture), access, stages);

    public void UseBuffer(RenderGraphPassHandle pass, RenderGraphBufferHandle buffer, RenderResourceAccess access, RenderPipelineStages stages)
        => AddUse(GetPass(pass), GetBuffer(buffer), access, stages);

    public RenderGraphReadbackHandle ReadbackBuffer(RenderGraphBufferHandle buffer, in BufferRange range)
    {
        return _readback.AddBuffer(buffer, range);
    }

    public RenderGraphReadbackHandle ReadbackTexture(RenderGraphTextureHandle texture, in PixelRect region)
    {
        return _readback.AddTexture(texture, region);
    }

    internal GraphResource ResolveBuffer(RenderGraphBufferHandle handle) => GetBuffer(handle);
    internal GraphResource ResolveTexture(RenderGraphTextureHandle handle) => GetTexture(handle);
    internal BufferAllocation ResolveBufferAllocation(RenderGraphBufferHandle handle)
        => GetBuffer(handle).Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");

    internal ulong AllocateStaging(ReadOnlySpan<byte> data)
        => _session.AllocateStaging(data);

    internal void Bind(VulkanGraphPipeline pipeline) => pipeline.Bind(this);

    internal void BindBuffer(VulkanGraphPipeline pipeline, ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset, ulong sizeInBytes)
        => pipeline.BindBuffer(this, binding, buffer, offset, sizeInBytes);

    internal void BindTexture(VulkanGraphPipeline pipeline, ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler)
        => pipeline.BindTexture(this, binding, texture, sampler);

    internal void PushConstants(VulkanGraphPipeline pipeline, ReadOnlySpan<byte> data, uint offset)
    {
        if (data.Length == 0 || checked(offset + (uint)data.Length) > pipeline.PushConstantSize)
        {
            throw new ArgumentException("Push constants exceed the pipeline ABI.", nameof(data));
        }

        CommandWriter.PushConstants(pipeline.Layout, pipeline.StageFlags, data, offset);
    }

    internal BufferAllocation StagingBuffer => _session.StagingBuffer;
    internal VulkanRenderSession Session => _session;

    private void BeginRaster(GraphPass pass)
    {
        var clear = pass.Color.ClearValue;
        var clearValues = stackalloc ClearValue[2];
        clearValues[0] = new ClearValue(new ClearColorValue(clear.Red, clear.Green, clear.Blue, clear.Alpha));
        uint clearValueCount = 1;
        if (_hasDepthAttachment)
        {
            var depth = _depthAttachment.ClearValue;
            clearValues[1] = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue { Depth = depth.Depth, Stencil = depth.Stencil }
            };
            clearValueCount = 2;
        }

        CommandWriter.BeginRenderPass(_session.GraphRenderPass, _session.GraphFramebuffer, _session.GraphExtent, clearValues, clearValueCount);
    }

    private readonly VulkanGraphDependencyPlanner _dependencyPlanner = new();
    private readonly List<BufferMemoryBarrier> _barrierBuffers = new();
    private readonly List<ImageMemoryBarrier> _barrierImages = new();

    private void EmitBarriers(GraphPass pass, ResourceState[] states)
    {
        var buffers = _barrierBuffers;
        var images = _barrierImages;
        buffers.Clear();
        images.Clear();
        foreach (var use in pass.Uses)
        {
            var previous = states.RefAt(use.Resource.Index);
            var next = ResourceState.For(use.Access, use.Stages);
            if (use.Resource.IsBuffer)
            {
                if (previous == next)
                {
                    continue;
                }

                var allocation = use.Resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable while planning a barrier.");
                buffers.Add(new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = next.Access, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = allocation.Buffer, Offset = 0, Size = allocation.AllocationSize });
            }
            else if (use.Resource.Image.Handle != default && (previous.Layout != next.Layout || previous.Access != next.Access))
            {
                images.Add(new ImageMemoryBarrier { SType = StructureType.ImageMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = next.Access, OldLayout = previous.Layout, NewLayout = next.Layout, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = use.Resource.Image, SubresourceRange = new ImageSubresourceRange { AspectMask = use.Resource.AspectMask, LevelCount = 1, LayerCount = 1 } });
            }
        }

        if (buffers.Count != 0 || images.Count != 0)
        {
            CommandWriter.PipelineBarrier(PipelineStageFlags.TopOfPipeBit | PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, CollectionsMarshal.AsSpan(buffers), CollectionsMarshal.AsSpan(images));
        }
    }

    private void EmitRasterSegmentEntryBarriers(int firstRasterPosition, ResourceState[] states)
    {
        var firstPass = _passes[_order.RefAt(firstRasterPosition)];
        for (var orderPosition = firstRasterPosition + 1; orderPosition < _order.Length; orderPosition++)
        {
            var pass = _passes[_order.RefAt(orderPosition)];
            if (pass.Kind != PassKind.Raster)
            {
                break;
            }

            foreach (var use in pass.Uses)
            {
                if (!use.Resource.IsBuffer || UsesResource(firstPass, use.Resource))
                {
                    continue;
                }

                var previous = states.RefAt(use.Resource.Index);
                var next = ResourceState.For(use.Access, use.Stages);
                if (previous == next)
                {
                    continue;
                }

                var allocation = use.Resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable while planning a raster entry barrier.");
                var barrier = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = previous.Access,
                    DstAccessMask = next.Access,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = allocation.Buffer,
                    Offset = 0,
                    Size = allocation.AllocationSize
                };
                CommandWriter.PipelineBarrier(
                    PipelineStageFlags.TopOfPipeBit | PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.AllCommandsBit,
                    in barrier);
                states.RefAt(use.Resource.Index) = next;
            }
        }
    }

    private static bool UsesResource(GraphPass pass, GraphResource resource)
    {
        foreach (var use in pass.Uses)
        {
            if (ReferenceEquals(use.Resource, resource))
            {
                return true;
            }
        }

        return false;
    }

    private static void UpdateStates(GraphPass pass, ResourceState[] states)
    {
        foreach (var use in pass.Uses)
        {
            states.RefAt(use.Resource.Index) = ResourceState.For(use.Access, use.Stages);
        }
    }

    private void ValidateRasterSegment()
    {
        var closed = false;
        var seen = false;
        foreach (var index in _order)
        {
            if (_passes[index].Kind == PassKind.Raster)
            {
                if (closed)
                {
                    throw new InvalidOperationException("Raster passes must form one contiguous render-pass segment.");
                }

                seen = true;
            }
            else if (seen)
            {
                closed = true;
            }
        }
    }

    private void ResetBuild()
    {
        foreach (var resource in _resources)
        {
            resource.Dispose(_session);
        }

        for (var index = 0; index < _passes.Count; index++)
        {
            var pass = _passes[index];
            pass.ReleaseForPool();
            _passPool.Add(pass);
        }

        _resources.Clear();
        _passes.Clear();
        _readback.Reset();
        _order = Array.Empty<int>();
        _depthAttachmentResource = null;
        _targetResource = null;
        _depthAttachment = default;
        _hasDepthAttachment = false;
        _built = false;
    }

    private void ConfigureRenderPass()
    {
        _session.ConfigureDepthStencilAttachment(_hasDepthAttachment ? _depthAttachmentResource?.Texture : null, _depthAttachment);
        foreach (var pass in _passes)
        {
            if (pass.Kind == PassKind.Raster)
            {
                if (pass.PipelineDescription is not { } pipelineDescription)
                {
                    throw new InvalidOperationException($"Raster pass '{pass.Name}' has no pipeline description.");
                }

                pass.Pipeline = _session.GetOrCreateRasterPipeline(pipelineDescription);
            }
        }
    }

    private void ValidateRasterPipelines()
    {
        foreach (var pass in _passes)
        {
            if (pass.Kind != PassKind.Raster)
            {
                continue;
            }

            if (pass.PipelineDescription is not { } pipeline)
            {
                throw new InvalidOperationException($"Raster pass '{pass.Name}' has no pipeline description.");
            }

            if ((pipeline.DepthTest || pipeline.DepthWrite || pipeline.StencilState.Enabled) && !_hasDepthAttachment)
            {
                throw new InvalidOperationException($"Raster pass '{pass.Name}' enables depth or stencil testing without a depth-stencil attachment.");
            }
        }
    }

    private uint AddResource(GraphResource resource)
    {
        resource.Index = _resources.Count;
        _resources.Add(resource);
        return (uint)_resources.Count;
    }
    private GraphResource GetTexture(RenderGraphTextureHandle handle)
        => GetResource(handle.IsValid, handle.Value, expectedTexture: true, "texture", nameof(handle));

    private GraphResource GetBuffer(RenderGraphBufferHandle handle)
        => GetResource(handle.IsValid, handle.Value, expectedTexture: false, "buffer", nameof(handle));

    private GraphResource GetResource(bool isValid, uint value, bool expectedTexture, string resourceKind, string parameterName)
    {
        if (!isValid || value == 0 || value > (uint)_resources.Count)
        {
            throw new ArgumentException($"The graph {resourceKind} handle is invalid.", parameterName);
        }

        var resource = _resources[(int)value - 1];
        if (resource.IsTexture != expectedTexture)
        {
            throw new ArgumentException($"The graph {resourceKind} handle is invalid.", parameterName);
        }

        return resource;
    }

    private GraphPass GetPass(RenderGraphPassHandle handle)
    {
        if (!handle.IsValid || handle.Value > (uint)_passes.Count)
        {
            throw new ArgumentException("The graph pass handle is invalid.", nameof(handle));
        }
        return _passes[(int)handle.Value - 1];
    }

    private static void AddUse(GraphPass pass, GraphResource resource, RenderResourceAccess access, RenderPipelineStages stages)
    {
        if (access == RenderResourceAccess.None || stages == RenderPipelineStages.None)
        {
            throw new ArgumentException("A graph use requires non-empty access and stages.");
        }
        pass.Uses.Add(new GraphUse(resource, access, stages));
    }

    internal void ThrowIfMutable()
    {
        ThrowIfDisposed();
        if (_built)
        {
            throw new InvalidOperationException("Graph is not mutable after a successful Build.");
        }
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static BufferUsageFlags ToBufferUsage(RenderBufferUsage usage)
    {
        var result = BufferUsageFlags.None;
        if (usage.HasFlag(RenderBufferUsage.Vertex))
        {
            result |= BufferUsageFlags.VertexBufferBit;
        }
        if (usage.HasFlag(RenderBufferUsage.Index))
        {
            result |= BufferUsageFlags.IndexBufferBit;
        }
        if (usage.HasFlag(RenderBufferUsage.Uniform))
        {
            result |= BufferUsageFlags.UniformBufferBit;
        }
        if (usage.HasFlag(RenderBufferUsage.Storage))
        {
            result |= BufferUsageFlags.StorageBufferBit;
        }
        if (usage.HasFlag(RenderBufferUsage.Indirect))
        {
            result |= BufferUsageFlags.IndirectBufferBit;
        }
        if (usage.HasFlag(RenderBufferUsage.TransferSource))
        {
            result |= BufferUsageFlags.TransferSrcBit;
        }
        if (usage.HasFlag(RenderBufferUsage.TransferDestination))
        {
            result |= BufferUsageFlags.TransferDstBit;
        }
        return result;
    }
    private static RenderGraphExecutionResult Failed(VulkanOperationException? exception = null)
        => new(
            ClassifyFailure(exception),
            ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);

    internal static RenderGraphExecutionStatus ClassifyFailure(VulkanOperationException? exception)
        => exception?.Result == Result.ErrorDeviceLost ? RenderGraphExecutionStatus.DeviceLost : RenderGraphExecutionStatus.Failed;

    private static Delta.Render.RenderProfilePassKind ToProfilePassKind(PassKind kind)
        => kind switch
        {
            PassKind.Raster => Delta.Render.RenderProfilePassKind.Raster,
            PassKind.Compute => Delta.Render.RenderProfilePassKind.Compute,
            PassKind.Transfer => Delta.Render.RenderProfilePassKind.Transfer,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };


}
