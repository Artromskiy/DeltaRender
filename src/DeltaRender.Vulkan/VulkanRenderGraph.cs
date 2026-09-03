using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Delta;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;
using Maths = global::Delta.Maths;

internal sealed unsafe partial class VulkanRenderGraph : IRenderGraph, IRenderGraphBuilder, IAsyncDisposable
{
    private static readonly bool EnableTopologyCache =
        string.Equals(Environment.GetEnvironmentVariable("DELTA_RENDER_TOPOLOGY_CACHE"), "1", StringComparison.Ordinal);
    private readonly VulkanRenderSession _session;
    private readonly List<GraphResource> _resources = [];
    private readonly List<GraphResource> _resourcePool = [];
    private readonly List<GraphPass> _passes = [];
    private readonly List<GraphPass> _passPool = [];
    private readonly VulkanGraphReadback _readback;
    private ResourceState[] _states = [];
    private int[] _order = [];
    private int _orderCount;
    private TopologyToken[] _topologyTokens = [];
    private TopologyToken[] _candidateTopologyTokens = [];
    private int _topologyTokenCount;
    private int _compiledOrderCount;
    private bool _hasCompiledTopology;
    private VulkanRasterCommandContext? _rasterContext;
    private VulkanComputeCommandContext? _computeContext;
    private VulkanTransferCommandContext? _transferContext;
    private GraphResource? _depthAttachmentResource;
    private GraphResource? _targetResource;
    private DepthStencilAttachmentDescription _depthAttachment;
    private bool _hasDepthAttachment;
    private bool _built;
    private bool _disposed;
    private readonly VulkanGraphDependencyPlanner _dependencyPlanner = new();
    private readonly VulkanGraphBarrierPlanner _barrierPlanner;

    internal VulkanRenderGraph(VulkanRenderSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _readback = new VulkanGraphReadback(this);
        _barrierPlanner = new VulkanGraphBarrierPlanner(this);
    }

    public void Build(ulong frameNumber, ReadOnlySpan<IRenderFeature> features)
    {
        ThrowIfDisposed();
        var profiler = _session.ProfilerState;
        long buildStart = profiler is null ? 0L : VulkanRenderProfiler.StartPhase();
        profiler?.BeginBuild(frameNumber);
        ResetBuild();
        _session.ReclaimDeferredTransientsForBuild();
        try
        {
            for (int index = 0; index < features.Length; index++)
            {
                ArgumentNullException.ThrowIfNull(features[index]);
                features[index].AddPasses(this, frameNumber);
            }

            ConfigureRenderPass();
            var topologyUnchanged = false;
            var topologyTokenCount = 0;
            if (EnableTopologyCache)
            {
                topologyUnchanged = TryCaptureTopology(out topologyTokenCount);
            }

            if (topologyUnchanged)
            {
                _orderCount = _compiledOrderCount;
            }
            else
            {
                EnsureOrderCapacity(_passes.Count);
                var compiledOrderCount = _dependencyPlanner.Compile(_passes, _resources.Count, _order);
                ValidateRasterSegment();
                if (EnableTopologyCache)
                {
                    CommitTopology(topologyTokenCount, compiledOrderCount);
                }

                _orderCount = compiledOrderCount;
            }

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

        if (_orderCount == 0)
        {
            _built = false;
            _session.ProfilerState?.Complete(RenderGraphExecutionStatus.NoWork, _resources.Count);
            return new RenderGraphExecutionResult(RenderGraphExecutionStatus.NoWork, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);
        }

        var profiler = _session.ProfilerState;
        long acquireStart = profiler is null ? 0L : VulkanRenderProfiler.StartPhase();
        var firstRole = QueueRoleFor(_passes[_order.RefAt(0)].Kind);
        try
        {
            if (!_session.BeginGraphFrame(firstRole))
            {
                profiler?.Complete(RenderGraphExecutionStatus.Failed, _resources.Count);
                return Failed();
            }
        }
        catch (VulkanOperationException exception)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("DELTA_RENDER_DEBUG"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(exception);
            }

            profiler?.Complete(ClassifyFailure(exception), _resources.Count);
            return Failed(exception);
        }
        finally
        {
            profiler?.EndAcquire(acquireStart);
        }

        CommandWriter.ResetState();
        if (_resources.Count != 0)
        {
            if (_states.Length < _resources.Count)
            {
                EnsureStateCapacity(_resources.Count);
            }

            Array.Clear(_states, 0, _resources.Count);
        }

        var states = _states;
        bool rasterActive = false;
        var activeRole = firstRole;
        long recordStart = profiler is null ? 0L : VulkanRenderProfiler.StartPhase();
        try
        {
            profiler?.BeginGpuFrame(_session.CommandBuffer, _orderCount);
            for (int orderPosition = 0; orderPosition < _orderCount; orderPosition++)
            {
                int passIndex = _order.RefAt(orderPosition);
                var pass = _passes[passIndex];
                var passRole = QueueRoleFor(pass.Kind);
                if (passRole != activeRole)
                {
                    if (!_session.UsesSameQueue(activeRole, passRole))
                    {
                        if (rasterActive)
                        {
                            CommandWriter.EndRenderPass();
                            rasterActive = false;
                        }

                        profiler?.DisableGpuTimestampsForCurrentFrame();
                        _session.SwitchGraphQueue(passRole);
                        CommandWriter.ResetState();
                    }

                    activeRole = passRole;
                }
                int passProfile = -1;
                long passStart = 0L;
                if (profiler is not null)
                {
                    passProfile = profiler.BeginPass(pass.Name, ToProfilePassKind(pass.Kind), _session.CommandBuffer, out passStart);
                }

                try
                {
                    if (pass.Uses.Count != 0)
                    {
                        _barrierPlanner.Emit(pass, states);
                    }
                    if (pass.Kind == PassKind.Raster)
                    {
                        if (!rasterActive)
                        {
                            _barrierPlanner.EmitRasterSegmentEntry(orderPosition, states);
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

                    if (pass.Uses.Count != 0)
                    {
                        VulkanGraphBarrierPlanner.UpdateStates(pass, states);
                    }

                }
                finally
                {
                    profiler?.EndPass(passProfile, passStart, _session.CommandBuffer);
                }
            }

            if (rasterActive)
            {
                CommandWriter.EndRenderPass();
            }

            if (_readback.HasRequests && activeRole != VulkanQueueRole.Transfer)
            {
                if (!_session.UsesSameQueue(activeRole, VulkanQueueRole.Transfer))
                {
                    if (rasterActive)
                    {
                        CommandWriter.EndRenderPass();
                        rasterActive = false;
                    }

                    profiler?.DisableGpuTimestampsForCurrentFrame();
                    _session.SwitchGraphQueue(VulkanQueueRole.Transfer);
                    CommandWriter.ResetState();
                }

                activeRole = VulkanQueueRole.Transfer;
            }

            _readback.Record(states);
            profiler?.EndRecord(recordStart);
            long submitStart = profiler is null ? 0L : VulkanRenderProfiler.StartPhase();
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
            if (string.Equals(Environment.GetEnvironmentVariable("DELTA_RENDER_DEBUG"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(exception);
            }

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

        int capacity = _states.Length == 0 ? 8 : checked(_states.Length * 2);
        _states = new ResourceState[Maths.Max(capacity, required)];
    }

    private static VulkanQueueRole QueueRoleFor(PassKind kind) => kind switch
    {
        PassKind.Raster => VulkanQueueRole.Graphics,
        PassKind.Compute => VulkanQueueRole.Compute,
        PassKind.Transfer => VulkanQueueRole.Transfer,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown graph pass kind."),
    };

    private void EnsureOrderCapacity(int required)
    {
        if (_order.Length >= required)
        {
            return;
        }

        int capacity = _order.Length == 0 ? 8 : checked(_order.Length * 2);
        _order = new int[Maths.Max(capacity, required)];
    }

    private bool TryCaptureTopology(out int tokenCount)
    {
        tokenCount = 0;
        EnsureTopologyCapacity(checked(_resources.Count + _passes.Count + CountUses()));

        for (int index = 0; index < _resources.Count; index++)
        {
            var resource = _resources[index];
            ulong flags = 0;
            if (resource.IsTexture)
            {
                flags |= 1;
            }

            if (resource.IsBuffer)
            {
                flags |= 2;
            }

            if (resource.IsTarget)
            {
                flags |= 4;
            }

            _candidateTopologyTokens[tokenCount++] = new TopologyToken(1, index, -1, flags, null);
        }

        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            var pass = _passes[passIndex];
            _candidateTopologyTokens[tokenCount++] = new TopologyToken(
                2,
                passIndex,
                -1,
                (ulong)pass.Kind,
                pass.Pipeline);

            foreach (var use in pass.Uses)
            {
                ulong flags = ((ulong)(uint)use.Access << 32) | (uint)use.Stages;
                _candidateTopologyTokens[tokenCount++] = new TopologyToken(
                    3,
                    passIndex,
                    use.Resource.Index,
                    flags,
                    null);
            }
        }

        if (!_hasCompiledTopology || _topologyTokenCount != tokenCount)
        {
            return false;
        }

        for (int index = 0; index < tokenCount; index++)
        {
            if (!_candidateTopologyTokens[index].Equals(_topologyTokens[index]))
            {
                return false;
            }
        }

        return true;
    }

    private int CountUses()
    {
        var count = 0;
        foreach (var pass in _passes)
        {
            count = checked(count + pass.Uses.Count);
        }

        return count;
    }

    private void CommitTopology(int tokenCount, int orderCount)
    {
        (_topologyTokens, _candidateTopologyTokens) = (_candidateTopologyTokens, _topologyTokens);
        _topologyTokenCount = tokenCount;
        _compiledOrderCount = orderCount;
        _hasCompiledTopology = true;
    }

    private void EnsureTopologyCapacity(int required)
    {
        if (_topologyTokens.Length >= required && _candidateTopologyTokens.Length >= required)
        {
            return;
        }

        int capacity = _topologyTokens.Length == 0 ? 8 : checked(_topologyTokens.Length * 2);
        capacity = Maths.Max(capacity, required);
        if (_topologyTokens.Length < capacity)
        {
            _topologyTokens = new TopologyToken[capacity];
        }

        if (_candidateTopologyTokens.Length < capacity)
        {
            _candidateTopologyTokens = new TopologyToken[capacity];
        }
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
            _targetResource = RentResource();
            _targetResource.SetTarget();
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

        var resource = RentResource();
        resource.SetTexture(value);
        return new RenderGraphTextureHandle(AddResource(resource));
    }

    public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer)
    {
        ThrowIfMutable();
        if (!_session.TryGetBuffer(buffer, out var value))
        {
            throw new InvalidOperationException("The buffer handle is unknown or stale.");
        }

        var resource = RentResource();
        resource.SetBuffer(value);
        return new RenderGraphBufferHandle(AddResource(resource));
    }

    public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient texture description is invalid.", nameof(description));
        }

        var resource = RentResource();
        resource.SetOwnedTexture(_session.CreateTransientTexture(description), description);
        return new RenderGraphTextureHandle(AddResource(resource));
    }

    public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient buffer description is invalid.", nameof(description));
        }

        var allocation = _session.CreateTransientBuffer(description);
        var resource = RentResource();
        resource.SetOwnedBuffer(allocation, description);
        return new RenderGraphBufferHandle(AddResource(resource));
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

        int poolIndex = _passPool.Count - 1;
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

    public RenderGraphReadbackHandle ReadbackBuffer(RenderGraphBufferHandle buffer, in BufferRange range) => _readback.AddBuffer(buffer, range);

    public RenderGraphReadbackHandle ReadbackTexture(RenderGraphTextureHandle texture, in PixelRect region) => _readback.AddTexture(texture, region);

    internal GraphResource ResolveBuffer(RenderGraphBufferHandle handle) => GetBuffer(handle);
    internal GraphResource ResolveTexture(RenderGraphTextureHandle handle) => GetTexture(handle);
    internal BufferAllocation ResolveBufferAllocation(RenderGraphBufferHandle handle)
        => GetBuffer(handle).Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");

    internal ulong AllocateStaging(ReadOnlySpan<byte> data)
    {
        ulong offset = _session.AllocateStaging(data);
        _session.ProfilerState?.RecordUploadBytes((ulong)data.Length);
        return offset;
    }

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
    internal int OrderCount => _orderCount;

    internal GraphPass OrderedPassAt(int position) => _passes[_order.RefAt(position)];

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

    private void ValidateRasterSegment()
    {
        bool closed = false;
        bool seen = false;
        for (int orderPosition = 0; orderPosition < _orderCount; orderPosition++)
        {
            int index = _order.RefAt(orderPosition);
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
            resource.ReleaseForPool();
            _resourcePool.Add(resource);
        }

        for (int index = 0; index < _passes.Count; index++)
        {
            var pass = _passes[index];
            pass.ReleaseForPool();
            _passPool.Add(pass);
        }

        _resources.Clear();
        _passes.Clear();
        _readback.Reset();
        _orderCount = 0;
        _depthAttachmentResource = null;
        _targetResource = null;
        _depthAttachment = default;
        _hasDepthAttachment = false;
        _built = false;
    }

    private void ConfigureRenderPass() => _session.ConfigureDepthStencilAttachment(_hasDepthAttachment ? _depthAttachmentResource?.Texture : null, _depthAttachment);

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

    private GraphResource RentResource()
    {
        if (_resourcePool.Count == 0)
        {
            return new GraphResource();
        }

        int poolIndex = _resourcePool.Count - 1;
        var resource = _resourcePool[poolIndex];
        _resourcePool.RemoveAt(poolIndex);
        resource.ReleaseForPool();
        return resource;
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
        pass.AddUse(resource, access, stages);
    }

    private readonly struct TopologyToken
    {
        private readonly byte _kind;
        private readonly int _index;
        private readonly int _resourceIndex;
        private readonly ulong _flags;
        private readonly VulkanGraphPipeline? _pipeline;

        internal TopologyToken(
            byte kind,
            int index,
            int resourceIndex,
            ulong flags,
            VulkanGraphPipeline? pipeline)
        {
            _kind = kind;
            _index = index;
            _resourceIndex = resourceIndex;
            _flags = flags;
            _pipeline = pipeline;
        }

        internal bool Equals(TopologyToken other)
            => _kind == other._kind &&
               _index == other._index &&
               _resourceIndex == other._resourceIndex &&
               _flags == other._flags &&
               ReferenceEquals(_pipeline, other._pipeline);
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
