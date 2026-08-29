using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanRenderGraph : IRenderGraph, IRenderGraphBuilder, IAsyncDisposable
{
    private readonly VulkanRenderSession _session;
    private readonly List<GraphResource> _resources = new();
    private readonly List<GraphPass> _passes = new();
    private readonly List<ReadbackRequest> _readbacks = new();
    private ResourceState[] _states = [];
    private int[] _order = Array.Empty<int>();
    private GraphResource? _depthAttachmentResource;
    private DepthStencilAttachmentDescription _depthAttachment;
    private bool _hasDepthAttachment;
    private bool _built;
    private bool _disposed;

    internal VulkanRenderGraph(VulkanRenderSession session) => _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Build(ulong frameNumber, ReadOnlySpan<IRenderFeature> features)
    {
        ThrowIfDisposed();
        ResetBuild();
        try
        {
            for (var index = 0; index < features.Length; index++)
            {
                ArgumentNullException.ThrowIfNull(features[index]);
                features[index].AddPasses(this, frameNumber);
            }

            ConfigureRenderPass();
            _order = CompileOrder();
            ValidateRasterSegment();
            ValidateRasterPipelines();
            _built = true;
        }
        catch
        {
            ResetBuild();
            throw;
        }
    }

    public RenderGraphExecutionResult Execute()
    {
        ThrowIfDisposed();
        if (!_built) throw new InvalidOperationException("A render graph must be built before Execute.");
        if (_order.Length == 0)
        {
            _built = false;
            return new RenderGraphExecutionResult(RenderGraphExecutionStatus.NoWork, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);
        }

        if (!_session.BeginGraphFrame()) return Failed();
        if (_states.Length < _resources.Count) _states = new ResourceState[_resources.Count];
        Array.Clear(_states, 0, _resources.Count);
        var states = _states;
        var rasterActive = false;
        try
        {
            foreach (var passIndex in _order)
            {
                var pass = _passes[passIndex];
                EmitBarriers(pass, states);
                if (pass.Kind == PassKind.Raster)
                {
                    if (!rasterActive)
                    {
                        BeginRaster(pass);
                        rasterActive = true;
                    }

                    var rasterPipeline = pass.Pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.");
                    rasterPipeline.BeginBindings();
                    pass.Raster?.Record(new VulkanRasterCommandContext(this, rasterPipeline));
                }
                else
                {
                    if (rasterActive)
                    {
                        _session.Api.CmdEndRenderPass(_session.CommandBuffer);
                        rasterActive = false;
                    }

                    if (pass.Kind == PassKind.Compute)
                    {
                        var computePipeline = pass.Pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline.");
                        computePipeline.BeginBindings();
                        pass.Compute?.Record(new VulkanComputeCommandContext(this, computePipeline));
                    }
                    else
                    {
                        pass.Transfer?.Record(new VulkanTransferCommandContext(this));
                    }
                }

                UpdateStates(pass, states);
            }

            if (rasterActive) _session.Api.CmdEndRenderPass(_session.CommandBuffer);
            RecordReadbacks(states);
            if (!_session.EndGraphFrame()) return Failed();
            _built = false;
            return new RenderGraphExecutionResult(RenderGraphExecutionStatus.Submitted, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);
        }
        catch
        {
            _session.AbortGraphFrame();
            return Failed();
        }
    }

    public int CopyReadback(RenderGraphReadbackHandle readback, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (!readback.IsValid || readback.Value > (uint)_readbacks.Count) throw new ArgumentException("The readback handle is invalid.", nameof(readback));
        var request = _readbacks[(int)readback.Value - 1];
        if (!request.Submitted) throw new InvalidOperationException("The readback is not associated with a submitted graph.");
        if (destination.Length < request.Size) throw new ArgumentException($"The destination requires at least {request.Size} bytes.", nameof(destination));
        _session.WaitForReadback();
        var staging = _session.StagingBuffer;
        void* pointer = null;
        var mapped = _session.Api.MapMemory(_session.Device, staging.Memory, request.StagingOffset, (ulong)request.Size, 0, &pointer);
        if (mapped != Result.Success) throw new InvalidOperationException($"MapMemory(readback) failed: {mapped}");
        try
        {
            new ReadOnlySpan<byte>(pointer, request.Size).CopyTo(destination);
            if (!staging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                var range = new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = staging.Memory, Offset = 0, Size = (nuint)staging.AllocationSize };
                Ensure(_session.Api.InvalidateMappedMemoryRanges(_session.Device, 1, &range), "InvalidateMappedMemoryRanges");
                new ReadOnlySpan<byte>(pointer, request.Size).CopyTo(destination);
            }
        }
        finally
        {
            _session.Api.UnmapMemory(_session.Device, staging.Memory);
        }

        return request.Size;
    }

    public ValueTask DisposeAsync()
    {
        DisposeGraph();
        return ValueTask.CompletedTask;
    }

    internal void DisposeGraph()
    {
        if (_disposed) return;
        ResetBuild();
        _disposed = true;
    }

    public RenderGraphTextureHandle ImportTarget(RenderTargetHandle target)
    {
        ThrowIfMutable();
        if (!_session.HasTarget || target != _session.Target) throw new InvalidOperationException("The target handle does not belong to this session.");
        return AddResource(GraphResource.Target());
    }

    public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture)
    {
        ThrowIfMutable();
        if (!_session.TryGetTexture(texture, out var value)) throw new InvalidOperationException("The texture handle is unknown or stale.");
        return AddResource(GraphResource.FromTexture(value));
    }

    public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer)
    {
        ThrowIfMutable();
        if (!_session.TryGetBuffer(buffer, out var value)) throw new InvalidOperationException("The buffer handle is unknown or stale.");
        return AddBuffer(GraphResource.FromBuffer(value));
    }

    public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid) throw new ArgumentException("The transient texture description is invalid.", nameof(description));
        return AddResource(GraphResource.OwnedTexture(_session.CreateTransientTexture(description)));
    }

    public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ThrowIfMutable();
        if (!description.IsValid) throw new ArgumentException("The transient buffer description is invalid.", nameof(description));
        var allocation = _session.CreateNativeBuffer(description.SizeInBytes, ToBufferUsage(description.Usage), MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        return AddBuffer(GraphResource.OwnedBuffer(allocation, description));
    }

    public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
    {
        ThrowIfMutable();
        ArgumentNullException.ThrowIfNull(pass);
        if (!_session.HasTarget) throw new InvalidOperationException("Raster passes require a graphics target.");
        var pipeline = _session.GetOrCreateRasterPipeline(description.Pipeline);

        _passes.Add(new GraphPass(description.Name, PassKind.Raster, pipeline)
        {
            Raster = pass,
            PipelineDescription = description.Pipeline
        });
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
    {
        ThrowIfMutable();
        ArgumentNullException.ThrowIfNull(pass);
        var pipeline = _session.GetOrCreateComputePipeline(description.Shader);

        _passes.Add(new GraphPass(description.Name, PassKind.Compute, pipeline) { Compute = pass });
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddTransferPass(string name, ITransferPass pass)
    {
        ThrowIfMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pass);
        _passes.Add(new GraphPass(name, PassKind.Transfer, null) { Transfer = pass });
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public void UseColorAttachment(RenderGraphPassHandle pass, uint index, in ColorAttachmentDescription attachment)
    {
        var graphPass = GetPass(pass);
        if (graphPass.Kind != PassKind.Raster || index != 0) throw new NotSupportedException("Only color attachment index zero is supported.");
        var resource = GetTexture(attachment.Texture);
        if (!resource.IsTarget) throw new NotSupportedException("Only the session-owned target can be a color attachment.");
        graphPass.Color = attachment;
        AddUse(graphPass, resource, RenderResourceAccess.Write, RenderPipelineStages.ColorOutput);
    }

    public void UseDepthStencilAttachment(RenderGraphPassHandle pass, in DepthStencilAttachmentDescription attachment)
    {
        var graphPass = GetPass(pass);
        if (graphPass.Kind != PassKind.Raster) throw new InvalidOperationException("Depth-stencil attachments can only be used by raster passes.");

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
        ThrowIfMutable();
        var resource = GetBuffer(buffer);
        var allocation = resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");
        if (range.IsEmpty || range.Offset > allocation.AllocationSize || range.SizeInBytes > allocation.AllocationSize - range.Offset || range.SizeInBytes > int.MaxValue) throw new ArgumentException("The readback range is outside the buffer.", nameof(range));
        _readbacks.Add(new ReadbackRequest(resource, checked((int)range.SizeInBytes), range.Offset));
        return new RenderGraphReadbackHandle((uint)_readbacks.Count);
    }

    public RenderGraphReadbackHandle ReadbackTexture(RenderGraphTextureHandle texture, in PixelRect region)
    {
        ThrowIfMutable();
        if (region.IsEmpty || region.X < 0 || region.Y < 0)
        {
            throw new ArgumentException("The texture readback region is invalid.", nameof(region));
        }

        var resource = GetTexture(texture);
        if (_session.IsWindowed)
        {
            throw new NotSupportedException("Windowed texture readback is not supported by the current graph session.");
        }

        var size = checked(region.Width * region.Height * 4);
        _readbacks.Add(new ReadbackRequest(resource, size, 0, region, true));
        return new RenderGraphReadbackHandle((uint)_readbacks.Count);
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
        if (data.Length == 0 || checked(offset + (uint)data.Length) > pipeline.PushConstantSize) throw new ArgumentException("Push constants exceed the pipeline ABI.", nameof(data));
        fixed (byte* pointer = data) _session.Api.CmdPushConstants(_session.CommandBuffer, pipeline.Layout, pipeline.StageFlags, offset, (uint)data.Length, pointer);
    }

    internal BufferAllocation StagingBuffer => _session.StagingBuffer;
    internal VulkanRenderSession Session => _session;

    private void RecordReadbacks(ReadOnlySpan<ResourceState> states)
    {
        foreach (var request in _readbacks)
        {
            var offset = _session.ReserveStaging(request.Size);
            var staging = _session.StagingBuffer;
            request.StagingOffset = offset;
            request.Submitted = true;
            if (request.IsTexture)
            {
                var image = request.Resource.IsTarget ? _session.GraphImage : request.Resource.Image;
                if (image.Handle == default)
                {
                    throw new InvalidOperationException("The texture readback image is unavailable.");
                }

                var imageBarrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = states[request.Resource.Index].Access,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = states[request.Resource.Index].Layout,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
                };
                var previousStages = states[request.Resource.Index].Stages;
                _session.Api.CmdPipelineBarrier(_session.CommandBuffer, previousStages == 0 ? PipelineStageFlags.TopOfPipeBit : previousStages, PipelineStageFlags.TransferBit, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, ReadOnlySpan<BufferMemoryBarrier>.Empty, new[] { imageBarrier });
                var copy = new BufferImageCopy
                {
                    BufferOffset = offset,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                    ImageOffset = new Offset3D(request.Region.X, request.Region.Y, 0),
                    ImageExtent = new Extent3D((uint)request.Region.Width, (uint)request.Region.Height, 1)
                };
                _session.Api.CmdCopyImageToBuffer(_session.CommandBuffer, image, ImageLayout.TransferSrcOptimal, staging.Buffer, 1, &copy);
            }
            else
            {
                var buffer = request.Resource.Buffer?.Allocation ?? throw new InvalidOperationException("The buffer readback resource is unavailable.");
                var previous = states[request.Resource.Index];
                var sourceBarrier = new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = AccessFlags.TransferReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = buffer.Buffer, Offset = request.SourceOffset, Size = (ulong)request.Size };
                _session.Api.CmdPipelineBarrier(_session.CommandBuffer, previous.Stages == 0 ? PipelineStageFlags.TopOfPipeBit : previous.Stages, PipelineStageFlags.TransferBit, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, new[] { sourceBarrier }, ReadOnlySpan<ImageMemoryBarrier>.Empty);
                var copy = new BufferCopy { SrcOffset = request.SourceOffset, DstOffset = offset, Size = (ulong)request.Size };
                _session.Api.CmdCopyBuffer(_session.CommandBuffer, buffer.Buffer, staging.Buffer, 1, &copy);
            }

            var hostBarrier = new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = staging.Buffer, Offset = offset, Size = (ulong)request.Size };
            _session.Api.CmdPipelineBarrier(_session.CommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, new[] { hostBarrier }, ReadOnlySpan<ImageMemoryBarrier>.Empty);
        }
    }

    private void BeginRaster(GraphPass pass)
    {
        var clear = pass.Color.ClearValue;
        var clearValues = stackalloc ClearValue[2];
        clearValues[0] = new ClearValue(new ClearColorValue(clear.Red, clear.Green, clear.Blue, clear.Alpha));
        uint clearValueCount = 1;
        if (_hasDepthAttachment)
        {
            var depth = _depthAttachment.ClearValue;
            clearValues[1] = new ClearValue(new ClearDepthStencilValue { Depth = depth.Depth, Stencil = depth.Stencil });
            clearValueCount = 2;
        }

        var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _session.GraphRenderPass, Framebuffer = _session.GraphFramebuffer, RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _session.GraphExtent }, ClearValueCount = clearValueCount, PClearValues = clearValues };
        _session.Api.CmdBeginRenderPass(_session.CommandBuffer, &begin, SubpassContents.Inline);
    }

    private void EmitBarriers(GraphPass pass, ResourceState[] states)
    {
        var buffers = new List<BufferMemoryBarrier>();
        var images = new List<ImageMemoryBarrier>();
        foreach (var use in pass.Uses)
        {
            var previous = states[use.Resource.Index];
            var next = ResourceState.For(use.Access, use.Stages);
            if (use.Resource.IsBuffer)
            {
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
            _session.Api.CmdPipelineBarrier(_session.CommandBuffer, PipelineStageFlags.TopOfPipeBit | PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.None, ReadOnlySpan<MemoryBarrier>.Empty, CollectionsMarshal.AsSpan(buffers), CollectionsMarshal.AsSpan(images));
        }
    }

    private static void UpdateStates(GraphPass pass, ResourceState[] states)
    {
        foreach (var use in pass.Uses) states[use.Resource.Index] = ResourceState.For(use.Access, use.Stages);
    }

    private int[] CompileOrder()
    {
        var edges = new HashSet<int>[_passes.Count];
        var indegree = new int[_passes.Count];
        var lastWriter = new int[_resources.Count];
        var readers = new List<int>[_resources.Count];
        Array.Fill(lastWriter, -1);
        for (var i = 0; i < edges.Length; i++) edges[i] = new HashSet<int>();
        for (var i = 0; i < readers.Length; i++) readers[i] = new List<int>();
        for (var passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            foreach (var use in _passes[passIndex].Uses)
            {
                var resource = use.Resource.Index;
                if (lastWriter[resource] >= 0) AddEdge(lastWriter[resource], passIndex, edges, indegree);
                if (use.Access.HasFlag(RenderResourceAccess.Write))
                {
                    foreach (var reader in readers[resource]) AddEdge(reader, passIndex, edges, indegree);
                    readers[resource].Clear();
                    lastWriter[resource] = passIndex;
                }
                else if (use.Access.HasFlag(RenderResourceAccess.Read)) readers[resource].Add(passIndex);
            }
        }

        var ready = new Queue<int>();
        for (var i = 0; i < indegree.Length; i++) if (indegree[i] == 0) ready.Enqueue(i);
        var order = new int[_passes.Count];
        var count = 0;
        while (ready.Count != 0)
        {
            var current = ready.Dequeue();
            order[count++] = current;
            foreach (var next in edges[current]) if (--indegree[next] == 0) ready.Enqueue(next);
        }

        if (count != order.Length) throw new InvalidOperationException("Render graph contains a dependency cycle.");
        return order;
    }

    private void ValidateRasterSegment()
    {
        var closed = false;
        var seen = false;
        foreach (var index in _order)
        {
            if (_passes[index].Kind == PassKind.Raster)
            {
                if (closed) throw new InvalidOperationException("Raster passes must form one contiguous render-pass segment.");
                seen = true;
            }
            else if (seen) closed = true;
        }
    }

    private void ResetBuild()
    {
        foreach (var resource in _resources) resource.Dispose(_session);
        _resources.Clear();
        _passes.Clear();
        _readbacks.Clear();
        _order = Array.Empty<int>();
        _depthAttachmentResource = null;
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
                pass.Pipeline = _session.GetOrCreateRasterPipeline(pass.PipelineDescription);
            }
        }
    }

    private void ValidateRasterPipelines()
    {
        foreach (var pass in _passes)
        {
            if (pass.Kind != PassKind.Raster) continue;
            var pipeline = pass.PipelineDescription;
            if ((pipeline.DepthTest || pipeline.DepthWrite || pipeline.StencilState.Enabled) && !_hasDepthAttachment)
            {
                throw new InvalidOperationException($"Raster pass '{pass.Name}' enables depth or stencil testing without a depth-stencil attachment.");
            }
        }
    }

    private RenderGraphTextureHandle AddResource(GraphResource resource) { resource.Index = _resources.Count; _resources.Add(resource); return new RenderGraphTextureHandle((uint)_resources.Count); }
    private RenderGraphBufferHandle AddBuffer(GraphResource resource) { resource.Index = _resources.Count; _resources.Add(resource); return new RenderGraphBufferHandle((uint)_resources.Count); }
    private GraphResource GetTexture(RenderGraphTextureHandle handle) { if (!handle.IsValid || handle.Value > (uint)_resources.Count || !_resources[(int)handle.Value - 1].IsTexture) throw new ArgumentException("The graph texture handle is invalid.", nameof(handle)); return _resources[(int)handle.Value - 1]; }
    private GraphResource GetBuffer(RenderGraphBufferHandle handle) { if (!handle.IsValid || handle.Value > (uint)_resources.Count || !_resources[(int)handle.Value - 1].IsBuffer) throw new ArgumentException("The graph buffer handle is invalid.", nameof(handle)); return _resources[(int)handle.Value - 1]; }
    private GraphPass GetPass(RenderGraphPassHandle handle) { if (!handle.IsValid || handle.Value > (uint)_passes.Count) throw new ArgumentException("The graph pass handle is invalid.", nameof(handle)); return _passes[(int)handle.Value - 1]; }
    private static void AddUse(GraphPass pass, GraphResource resource, RenderResourceAccess access, RenderPipelineStages stages) { if (access == RenderResourceAccess.None || stages == RenderPipelineStages.None) throw new ArgumentException("A graph use requires non-empty access and stages."); pass.Uses.Add(new GraphUse(resource, access, stages)); }
    private void ThrowIfMutable() { ThrowIfDisposed(); if (_built) throw new InvalidOperationException("Graph is not mutable after a successful Build."); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void AddEdge(int from, int to, HashSet<int>[] edges, int[] indegree) { if (from != to && edges[from].Add(to)) indegree[to]++; }
    private static BufferUsageFlags ToBufferUsage(RenderBufferUsage usage) { var result = BufferUsageFlags.None; if (usage.HasFlag(RenderBufferUsage.Vertex)) result |= BufferUsageFlags.VertexBufferBit; if (usage.HasFlag(RenderBufferUsage.Index)) result |= BufferUsageFlags.IndexBufferBit; if (usage.HasFlag(RenderBufferUsage.Uniform)) result |= BufferUsageFlags.UniformBufferBit; if (usage.HasFlag(RenderBufferUsage.Storage)) result |= BufferUsageFlags.StorageBufferBit; if (usage.HasFlag(RenderBufferUsage.Indirect)) result |= BufferUsageFlags.IndirectBufferBit; if (usage.HasFlag(RenderBufferUsage.TransferSource)) result |= BufferUsageFlags.TransferSrcBit; if (usage.HasFlag(RenderBufferUsage.TransferDestination)) result |= BufferUsageFlags.TransferDstBit; return result; }
    private static void Ensure(Result result, string operation) { if (result != Result.Success) throw new InvalidOperationException($"{operation} failed: {result}"); }
    private static RenderGraphExecutionResult Failed()
        => new(RenderGraphExecutionStatus.Failed, ReadOnlyMemory<Delta.Diagnostics.Diagnostic>.Empty);

    private enum PassKind : byte { Raster, Compute, Transfer }
    private sealed class GraphPass(string name, PassKind kind, VulkanGraphPipeline? pipeline)
    {
        internal string Name = name;
        internal PassKind Kind = kind;
        internal VulkanGraphPipeline? Pipeline = pipeline;
        internal RasterPipelineDescription PipelineDescription;
        internal IRasterPass? Raster;
        internal IComputePass? Compute;
        internal ITransferPass? Transfer;
        internal ColorAttachmentDescription Color;
        internal DepthStencilAttachmentDescription DepthStencil;
        internal bool HasDepthStencil;
        internal readonly List<GraphUse> Uses = new();
    }

    internal sealed class GraphResource
    {
        internal int Index;
        internal bool IsTexture;
        internal bool IsBuffer;
        internal bool IsTarget;
        internal bool Owns;
        internal Image Image;
        internal PersistentBuffer? Buffer;
        internal PersistentTexture? Texture;
        internal ImageAspectFlags AspectMask => IsTarget || Texture is null ? ImageAspectFlags.ColorBit : Texture.Format == Format.D24UnormS8Uint ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : Texture.Format == Format.D32Sfloat ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        internal static GraphResource Target() => new() { IsTexture = true, IsTarget = true };
        internal static GraphResource FromTexture(PersistentTexture texture) => new() { IsTexture = true, Texture = texture, Image = texture.Image };
        internal static GraphResource OwnedTexture(PersistentTexture texture) => new() { IsTexture = true, Texture = texture, Image = texture.Image, Owns = true };
        internal static GraphResource FromBuffer(PersistentBuffer buffer) => new() { IsBuffer = true, Buffer = buffer };
        internal static GraphResource OwnedBuffer(BufferAllocation allocation, RenderBufferDescription description) => new() { IsBuffer = true, Buffer = new PersistentBuffer(allocation, description, 0), Owns = true };
        internal void Dispose(VulkanRenderSession session)
        {
            if (!Owns) return;
            if (IsBuffer && Buffer is not null) session.DeferTransient(Buffer.Allocation);
            if (IsTexture && Texture is not null) session.DeferTransient(Texture);
            Buffer = null;
            Texture = null;
            Image = default;
        }
    }

    private readonly record struct GraphUse(GraphResource Resource, RenderResourceAccess Access, RenderPipelineStages Stages);
    private readonly record struct ResourceState(PipelineStageFlags Stages, AccessFlags Access, ImageLayout Layout)
    {
        internal static ResourceState For(RenderResourceAccess access, RenderPipelineStages stages)
        {
            var stage = PipelineStageFlags.TopOfPipeBit;
            if (stages.HasFlag(RenderPipelineStages.Transfer)) stage |= PipelineStageFlags.TransferBit;
            if (stages.HasFlag(RenderPipelineStages.Vertex)) stage |= PipelineStageFlags.VertexShaderBit;
            if (stages.HasFlag(RenderPipelineStages.Fragment)) stage |= PipelineStageFlags.FragmentShaderBit;
            if (stages.HasFlag(RenderPipelineStages.Compute)) stage |= PipelineStageFlags.ComputeShaderBit;
            if (stages.HasFlag(RenderPipelineStages.ColorOutput)) stage |= PipelineStageFlags.ColorAttachmentOutputBit;
            var hasTransfer = stages.HasFlag(RenderPipelineStages.Transfer);
            var hasShader = stages.HasFlag(RenderPipelineStages.Vertex) || stages.HasFlag(RenderPipelineStages.Fragment) || stages.HasFlag(RenderPipelineStages.Compute);
            var hasColor = stages.HasFlag(RenderPipelineStages.ColorOutput);
            var hasDepth = stages.HasFlag(RenderPipelineStages.DepthStencil);
            if (hasDepth) stage |= PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;

            var accessFlags = AccessFlags.None;
            if (access.HasFlag(RenderResourceAccess.Read))
            {
                if (hasTransfer) accessFlags |= AccessFlags.TransferReadBit;
                if (hasShader) accessFlags |= AccessFlags.ShaderReadBit;
                if (hasDepth) accessFlags |= AccessFlags.DepthStencilAttachmentReadBit;
            }
            if (access.HasFlag(RenderResourceAccess.Write))
            {
                if (hasTransfer) accessFlags |= AccessFlags.TransferWriteBit;
                if (hasShader) accessFlags |= AccessFlags.ShaderWriteBit;
                if (hasDepth) accessFlags |= AccessFlags.DepthStencilAttachmentWriteBit;
                if (hasColor) accessFlags |= AccessFlags.ColorAttachmentWriteBit;
            }

            var layout = hasColor ? ImageLayout.ColorAttachmentOptimal : hasDepth ? ImageLayout.DepthStencilAttachmentOptimal : hasTransfer ? (access.HasFlag(RenderResourceAccess.Write) ? ImageLayout.TransferDstOptimal : ImageLayout.TransferSrcOptimal) : access.HasFlag(RenderResourceAccess.Write) ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal;
            return new ResourceState(stage, accessFlags, layout);
        }
    }

    private sealed class ReadbackRequest(GraphResource resource, int size, ulong sourceOffset, PixelRect region = default, bool isTexture = false)
    {
        internal GraphResource Resource = resource;
        internal int Size = size;
        internal ulong SourceOffset = sourceOffset;
        internal ulong StagingOffset;
        internal bool Submitted;
        internal PixelRect Region = region;
        internal bool IsTexture = isTexture;
    }

    internal sealed unsafe class VulkanGraphPipeline
    {
        private readonly DescriptorSetLayout[] _layouts;
        private readonly DescriptorPool _pool;
        private readonly DescriptorSet[] _descriptorSets;
        private readonly GraphBinding[] _bindings;
        private readonly bool[] _bound;
        private bool _disposed;
        internal Pipeline Pipeline { get; }
        internal PipelineLayout Layout { get; }
        internal PipelineBindPoint BindPoint { get; }
        internal ShaderStageFlags StageFlags { get; }
        internal uint PushConstantSize { get; }

        private VulkanGraphPipeline(Pipeline pipeline, PipelineLayout layout, PipelineBindPoint bindPoint, DescriptorSetLayout[] layouts, DescriptorPool pool, DescriptorSet[] descriptorSets, GraphBinding[] bindings, ShaderStageFlags stageFlags, uint pushConstantSize)
        {
            Pipeline = pipeline;
            Layout = layout;
            BindPoint = bindPoint;
            _layouts = layouts;
            _pool = pool;
            _descriptorSets = descriptorSets;
            _bindings = bindings;
            _bound = new bool[bindings.Length];
            StageFlags = stageFlags;
            PushConstantSize = pushConstantSize;
        }

        internal void BeginBindings() => Array.Clear(_bound);

        internal void BindBuffer(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphBufferHandle handle, ulong offset, ulong sizeInBytes)
        {
            var index = FindBinding(binding);
            var declaration = _bindings[index];
            if (declaration.DescriptorType is not DescriptorType.StorageBuffer and not DescriptorType.UniformBuffer)
            {
                throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not a buffer.", nameof(binding));
            }

            var resource = graph.ResolveBuffer(handle);
            var allocation = resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");
            if (offset > allocation.AllocationSize)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            var range = sizeInBytes == 0 ? allocation.AllocationSize - offset : sizeInBytes;
            if (range == 0 || range > allocation.AllocationSize - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
            }

            var descriptor = new DescriptorBufferInfo { Buffer = allocation.Buffer, Offset = offset, Range = range };
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSets[binding.Set], DstBinding = binding.Binding, DescriptorCount = 1, DescriptorType = declaration.DescriptorType, PBufferInfo = &descriptor };
            graph.Session.Api.UpdateDescriptorSets(graph.Session.Device, 1, &write, 0, null);
            _bound[index] = true;
        }

        internal void BindTexture(VulkanRenderGraph graph, ShaderBinding binding, RenderGraphTextureHandle handle, RenderSamplerHandle sampler)
        {
            var index = FindBinding(binding);
            var declaration = _bindings[index];
            if (declaration.DescriptorType != DescriptorType.CombinedImageSampler)
            {
                throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not a sampled texture.", nameof(binding));
            }

            var resource = graph.ResolveTexture(handle);
            var texture = resource.Texture ?? throw new InvalidOperationException("The graph texture is unavailable for sampling.");
            if (!graph.Session.TryGetSampler(sampler, out var samplerValue) || samplerValue is null)
            {
                throw new InvalidOperationException("The sampler handle is unknown or stale.");
            }

            var descriptor = new DescriptorImageInfo { Sampler = samplerValue.Sampler, ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSets[binding.Set], DstBinding = binding.Binding, DescriptorCount = 1, DescriptorType = declaration.DescriptorType, PImageInfo = &descriptor };
            graph.Session.Api.UpdateDescriptorSets(graph.Session.Device, 1, &write, 0, null);
            _bound[index] = true;
        }

        internal void Bind(VulkanRenderGraph graph)
        {
            for (var index = 0; index < _bound.Length; index++)
            {
                if (!_bound[index])
                {
                    throw new InvalidOperationException($"Shader binding set {_bindings[index].Binding.Set}, binding {_bindings[index].Binding.Binding} was not provided.");
                }
            }

            graph.Session.Api.CmdBindPipeline(graph.Session.CommandBuffer, BindPoint, Pipeline);
            if (_descriptorSets.Length == 0)
            {
                return;
            }

            fixed (DescriptorSet* descriptorSetPointer = _descriptorSets)
            {
                graph.Session.Api.CmdBindDescriptorSets(graph.Session.CommandBuffer, BindPoint, Layout, 0, (uint)_descriptorSets.Length, descriptorSetPointer, 0, null);
            }
        }

        private int FindBinding(ShaderBinding binding)
        {
            for (var index = 0; index < _bindings.Length; index++)
            {
                if (_bindings[index].Binding == binding)
                {
                    return index;
                }
            }

            throw new ArgumentException($"Shader binding set {binding.Set}, binding {binding.Binding} is not declared by the pipeline.", nameof(binding));
        }

        internal static VulkanGraphPipeline CreateCompute(VulkanRenderSession session, IShaderArtifact artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (artifact.Stage != ShaderStage.Compute) throw new ArgumentException("A compute pipeline requires a compute artifact.", nameof(artifact));
            return CreateComputeCore(session, artifact);
        }

        internal static VulkanGraphPipeline CreateRaster(VulkanRenderSession session, RasterPipelineDescription description)
        {
            var program = description.ShaderProgram;
            return CreateRasterCore(session, program, description);
        }

        private static VulkanGraphPipeline CreateComputeCore(VulkanRenderSession session, IShaderArtifact artifact)
        {
            var bindings = BuildBindings(artifact.Abi.Resources, out var maxSet);
            CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var descriptorSets);
            PipelineLayout pipelineLayout = default;
            Pipeline pipeline = default;
            ShaderModule module = default;
            try
            {
                module = CreateShaderModule(session, artifact.Spirv);
                var ranges = NativePushRanges(artifact.Abi.PushConstants, ShaderStageFlags.ComputeBit, out var size);
                fixed (DescriptorSetLayout* layoutPointer = layouts)
                fixed (PushConstantRange* pushPointer = ranges)
                {
                    var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)layouts.Length, PSetLayouts = layoutPointer, PushConstantRangeCount = (uint)ranges.Length, PPushConstantRanges = pushPointer };
                    Ensure(session.Api.CreatePipelineLayout(session.Device, info, null, out pipelineLayout), "CreatePipelineLayout(compute)");
                }

                var name = Encoding.UTF8.GetBytes(artifact.EntryPoint + "\0");
                fixed (byte* namePointer = name)
                {
                    var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = namePointer };
                    var info = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
                    var output = stackalloc Pipeline[1];
                    Ensure(session.Api.CreateComputePipelines(session.Device, default, 1, &info, null, output), "CreateComputePipelines");
                    pipeline = output[0];
                }

                return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Compute, layouts, pool, descriptorSets, bindings, ShaderStageFlags.ComputeBit, size);
            }
            catch
            {
                if (module.Handle != default) session.Api.DestroyShaderModule(session.Device, module, null);
                if (pipeline.Handle != default) session.Api.DestroyPipeline(session.Device, pipeline, null);
                if (pipelineLayout.Handle != default) session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
                DestroyDescriptorState(session, layouts, pool);
                throw;
            }
            finally
            {
                if (module.Handle != default) session.Api.DestroyShaderModule(session.Device, module, null);
            }
        }

        private static VulkanGraphPipeline CreateRasterCore(VulkanRenderSession session, IGraphicsShaderProgram program, RasterPipelineDescription description)
        {
            var resources = new List<ShaderResourceBinding>(program.Vertex.Abi.Resources);
            foreach (var resource in program.Fragment.Abi.Resources)
            {
                if (!resources.Any(item => item.Binding == resource.Binding)) resources.Add(resource);
            }

            var bindings = BuildBindings(resources, out var maxSet);
            CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var descriptorSets);
            PipelineLayout pipelineLayout = default;
            Pipeline pipeline = default;
            ShaderModule vertex = default;
            ShaderModule fragment = default;
            try
            {
                vertex = CreateShaderModule(session, program.Vertex.Spirv);
                fragment = CreateShaderModule(session, program.Fragment.Spirv);
                var pushRanges = program.Vertex.Abi.PushConstants.Count > 0 ? NativePushRanges(program.Vertex.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out var pushSize) : NativePushRanges(program.Fragment.Abi.PushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out pushSize);
                fixed (DescriptorSetLayout* layoutPointer = layouts)
                fixed (PushConstantRange* pushPointer = pushRanges)
                {
                    var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)layouts.Length, PSetLayouts = layoutPointer, PushConstantRangeCount = (uint)pushRanges.Length, PPushConstantRanges = pushPointer };
                    Ensure(session.Api.CreatePipelineLayout(session.Device, layoutInfo, null, out pipelineLayout), "CreatePipelineLayout(raster)");
                }

                var vertexName = Encoding.UTF8.GetBytes(program.Vertex.EntryPoint + "\0");
                var fragmentName = Encoding.UTF8.GetBytes(program.Fragment.EntryPoint + "\0");
                fixed (byte* vertexPointer = vertexName)
                fixed (byte* fragmentPointer = fragmentName)
                {
                    var stages = stackalloc PipelineShaderStageCreateInfo[2];
                    stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = vertexPointer };
                    stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = fragmentPointer };
                    var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                    var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = ToTopology(description.Topology) };
                    var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                    var rasterization = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = ToCullMode(description.CullMode), FrontFace = description.FrontFace == RasterFrontFace.Clockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise, LineWidth = 1 };
                    var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                    var blend = new PipelineColorBlendAttachmentState { BlendEnable = description.BlendMode != RenderBlendMode.Opaque, SrcColorBlendFactor = description.BlendMode == RenderBlendMode.PremultipliedAlpha ? BlendFactor.One : BlendFactor.SrcAlpha, DstColorBlendFactor = description.BlendMode == RenderBlendMode.Additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
                    var blendState = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
                    var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                    var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
                    var depth = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = description.DepthTest,
                        DepthWriteEnable = description.DepthWrite,
                        DepthCompareOp = ToCompareOp(description.DepthCompareOperation),
                        StencilTestEnable = description.StencilState.Enabled,
                        Front = ToStencilOpState(description.StencilState.Front),
                        Back = ToStencilOpState(description.StencilState.Back),
                    };
                    var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &rasterization, PMultisampleState = &multisample, PDepthStencilState = &depth, PColorBlendState = &blendState, PDynamicState = &dynamic, Layout = pipelineLayout, RenderPass = session.GraphRenderPass, Subpass = 0 };
                    var output = stackalloc Pipeline[1];
                    Ensure(session.Api.CreateGraphicsPipelines(session.Device, default, 1, &info, null, output), "CreateGraphicsPipelines");
                    pipeline = output[0];
                }

                return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Graphics, layouts, pool, descriptorSets, bindings, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Math.Max(GetPushSize(program.Vertex.Abi.PushConstants), GetPushSize(program.Fragment.Abi.PushConstants)));
            }
            catch
            {
                if (vertex.Handle != default) session.Api.DestroyShaderModule(session.Device, vertex, null);
                if (fragment.Handle != default) session.Api.DestroyShaderModule(session.Device, fragment, null);
                if (pipeline.Handle != default) session.Api.DestroyPipeline(session.Device, pipeline, null);
                if (pipelineLayout.Handle != default) session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
                DestroyDescriptorState(session, layouts, pool);
                throw;
            }
            finally
            {
                if (vertex.Handle != default) session.Api.DestroyShaderModule(session.Device, vertex, null);
                if (fragment.Handle != default) session.Api.DestroyShaderModule(session.Device, fragment, null);
            }
        }

        internal void Dispose(VulkanRenderSession session)
        {
            if (_disposed) return;
            _disposed = true;
            if (Pipeline.Handle != default) session.Api.DestroyPipeline(session.Device, Pipeline, null);
            if (Layout.Handle != default) session.Api.DestroyPipelineLayout(session.Device, Layout, null);
            DestroyDescriptorState(session, _layouts, _pool);
        }

        private static ShaderModule CreateShaderModule(VulkanRenderSession session, ReadOnlySpan<byte> bytes)
        {
            var words = MemoryMarshal.Cast<byte, uint>(bytes);
            fixed (uint* pointer = words)
            {
                var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)(words.Length * sizeof(uint)), PCode = pointer };
                Ensure(session.Api.CreateShaderModule(session.Device, info, null, out var module), "CreateShaderModule");
                return module;
            }
        }

        private static GraphBinding[] BuildBindings(IReadOnlyList<ShaderResourceBinding> resources, out int maxSet)
        {
            maxSet = -1;
            var result = new List<GraphBinding>(resources.Count);
            foreach (var resource in resources)
            {
                if (resource.DescriptorCount != 1) throw new ArgumentException("The graph supports one descriptor per binding.");
                maxSet = Math.Max(maxSet, checked((int)resource.Binding.Set));
                if (result.Any(item => item.Binding == resource.Binding)) continue;
                result.Add(new GraphBinding(resource.Binding, ToDescriptorType(resource.Kind), ToStageFlags(resource.Stages)));
            }

            return result.ToArray();
        }

        private static void CreateDescriptorState(VulkanRenderSession session, GraphBinding[] bindings, int maxSet, out DescriptorSetLayout[] layouts, out DescriptorPool pool, out DescriptorSet[] descriptorSets)
        {
            var setCount = maxSet + 1;
            if (setCount > session.MaxBoundDescriptorSets) throw new InvalidOperationException("Shader descriptor sets exceed device limits.");
            layouts = new DescriptorSetLayout[setCount];
            pool = default;
            descriptorSets = Array.Empty<DescriptorSet>();
            try
            {
                for (var set = 0; set < setCount; set++)
                {
                    var setBindings = bindings.Where(item => item.Binding.Set == (uint)set).Select(item => new DescriptorSetLayoutBinding { Binding = item.Binding.Binding, DescriptorCount = 1, DescriptorType = item.DescriptorType, StageFlags = item.StageFlags }).ToArray();
                    fixed (DescriptorSetLayoutBinding* pointer = setBindings)
                    {
                        var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)setBindings.Length, PBindings = pointer };
                        Ensure(session.Api.CreateDescriptorSetLayout(session.Device, info, null, out layouts[set]), "CreateDescriptorSetLayout");
                    }
                }

                if (bindings.Length != 0)
                {
                    var sizes = bindings.GroupBy(item => item.DescriptorType).Select(group => new DescriptorPoolSize { Type = group.Key, DescriptorCount = (uint)group.Count() }).ToArray();
                    fixed (DescriptorPoolSize* pointer = sizes)
                    {
                        var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = (uint)sizes.Length, PPoolSizes = pointer };
                        Ensure(session.Api.CreateDescriptorPool(session.Device, info, null, out pool), "CreateDescriptorPool");
                    }

                    descriptorSets = new DescriptorSet[setCount];
                    fixed (DescriptorSetLayout* layoutPointer = layouts)
                    fixed (DescriptorSet* descriptorSetPointer = descriptorSets)
                    {
                        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = (uint)setCount, PSetLayouts = layoutPointer };
                        Ensure(session.Api.AllocateDescriptorSets(session.Device, allocateInfo, descriptorSetPointer), "AllocateDescriptorSets");
                    }
                }
            }
            catch
            {
                DestroyDescriptorState(session, layouts, pool);
                throw;
            }
        }

        private static void DestroyDescriptorState(VulkanRenderSession session, DescriptorSetLayout[] layouts, DescriptorPool pool)
        {
            if (pool.Handle != default) session.Api.DestroyDescriptorPool(session.Device, pool, null);
            for (var i = layouts.Length - 1; i >= 0; i--) if (layouts[i].Handle != default) session.Api.DestroyDescriptorSetLayout(session.Device, layouts[i], null);
        }

        private static PushConstantRange[] NativePushRanges(IReadOnlyList<ShaderPushConstantRange> ranges, ShaderStageFlags flags, out uint size)
        {
            size = GetPushSize(ranges);
            return ranges.Select(range => new PushConstantRange { Offset = range.Offset, Size = range.Size, StageFlags = flags }).ToArray();
        }

        private static uint GetPushSize(IReadOnlyList<ShaderPushConstantRange> ranges) => ranges.Count == 0 ? 0 : ranges.Max(range => checked(range.Offset + range.Size));
        private static ShaderStageFlags ToStageFlags(ShaderStageMask stages) { var result = ShaderStageFlags.None; if (stages.HasFlag(ShaderStageMask.Compute)) result |= ShaderStageFlags.ComputeBit; if (stages.HasFlag(ShaderStageMask.Vertex)) result |= ShaderStageFlags.VertexBit; if (stages.HasFlag(ShaderStageMask.Fragment)) result |= ShaderStageFlags.FragmentBit; return result; }
        private static DescriptorType ToDescriptorType(ShaderResourceKind kind) => kind switch { ShaderResourceKind.StorageBuffer => DescriptorType.StorageBuffer, ShaderResourceKind.UniformBuffer => DescriptorType.UniformBuffer, ShaderResourceKind.SampledTexture or ShaderResourceKind.CombinedTextureSampler => DescriptorType.CombinedImageSampler, _ => throw new ArgumentException("Unsupported shader resource kind.") };
        private static CompareOp ToCompareOp(RenderCompareOperation operation) => operation switch
        {
            RenderCompareOperation.Never => CompareOp.Never,
            RenderCompareOperation.Less => CompareOp.Less,
            RenderCompareOperation.Equal => CompareOp.Equal,
            RenderCompareOperation.LessOrEqual => CompareOp.LessOrEqual,
            RenderCompareOperation.Greater => CompareOp.Greater,
            RenderCompareOperation.NotEqual => CompareOp.NotEqual,
            RenderCompareOperation.GreaterOrEqual => CompareOp.GreaterOrEqual,
            RenderCompareOperation.Always => CompareOp.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        private static StencilOp ToStencilOp(RenderStencilOperation operation) => operation switch
        {
            RenderStencilOperation.Keep => StencilOp.Keep,
            RenderStencilOperation.Zero => StencilOp.Zero,
            RenderStencilOperation.Replace => StencilOp.Replace,
            RenderStencilOperation.IncrementClamp => StencilOp.IncrementAndClamp,
            RenderStencilOperation.DecrementClamp => StencilOp.DecrementAndClamp,
            RenderStencilOperation.Invert => StencilOp.Invert,
            RenderStencilOperation.IncrementWrap => StencilOp.IncrementAndWrap,
            RenderStencilOperation.DecrementWrap => StencilOp.DecrementAndWrap,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        private static StencilOpState ToStencilOpState(RenderStencilFaceState state) => new()
        {
            FailOp = ToStencilOp(state.FailOperation),
            PassOp = ToStencilOp(state.PassOperation),
            DepthFailOp = ToStencilOp(state.DepthFailOperation),
            CompareOp = ToCompareOp(state.CompareOperation),
            CompareMask = state.CompareMask,
            WriteMask = state.WriteMask,
            Reference = state.Reference,
        };
        private static Silk.NET.Vulkan.PrimitiveTopology ToTopology(Delta.Render.RenderGraph.PrimitiveTopology topology) => topology switch { Delta.Render.RenderGraph.PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, Delta.Render.RenderGraph.PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList, Delta.Render.RenderGraph.PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList, _ => Silk.NET.Vulkan.PrimitiveTopology.TriangleList };
        private static CullModeFlags ToCullMode(RasterCullMode mode) => mode switch { RasterCullMode.Front => CullModeFlags.FrontBit, RasterCullMode.Back => CullModeFlags.BackBit, _ => CullModeFlags.None };
        private static void Ensure(Result result, string operation) { if (result != Result.Success) throw new InvalidOperationException($"{operation} failed: {result}"); }
        private readonly record struct GraphBinding(ShaderBinding Binding, DescriptorType DescriptorType, ShaderStageFlags StageFlags);
    }

    internal sealed unsafe class VulkanRasterCommandContext(VulkanRenderGraph graph, VulkanGraphPipeline? pipeline) : IRasterCommandContext
    {
        private readonly VulkanGraphPipeline _pipeline = pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.");
        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0) => graph.BindBuffer(_pipeline, binding, buffer, offset, sizeInBytes);
        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler) => graph.BindTexture(_pipeline, binding, texture, sampler);
        public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => graph.PushConstants(_pipeline, data, offset);
        public void SetViewport(in RenderViewport viewport) { if (!viewport.IsValid) throw new ArgumentException("Viewport is invalid.", nameof(viewport)); var value = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth); graph.Session.Api.CmdSetViewport(graph.Session.CommandBuffer, 0, 1, &value); }
        public void SetScissor(in PixelRect scissor) { if (scissor.IsEmpty) throw new ArgumentException("Scissor is empty.", nameof(scissor)); var value = new Rect2D { Offset = new Offset2D(scissor.X, scissor.Y), Extent = new Extent2D((uint)scissor.Width, (uint)scissor.Height) }; graph.Session.Api.CmdSetScissor(graph.Session.CommandBuffer, 0, 1, &value); }
        public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0) { var native = graph.ResolveBufferAllocation(buffer).Buffer; graph.Session.Api.CmdBindVertexBuffers(graph.Session.CommandBuffer, binding, 1, &native, &offset); }
        public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0) => graph.Session.Api.CmdBindIndexBuffer(graph.Session.CommandBuffer, graph.ResolveBufferAllocation(buffer).Buffer, offset, format == IndexElementFormat.UnsignedShort ? IndexType.Uint16 : IndexType.Uint32);
        public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) { graph.Bind(_pipeline); graph.Session.Api.CmdDraw(graph.Session.CommandBuffer, vertexCount, instanceCount, firstVertex, firstInstance); }
        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0) { graph.Bind(_pipeline); graph.Session.Api.CmdDrawIndexed(graph.Session.CommandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance); }
    }

    internal sealed unsafe class VulkanComputeCommandContext(VulkanRenderGraph graph, VulkanGraphPipeline? pipeline) : IComputeCommandContext
    {
        private readonly VulkanGraphPipeline _pipeline = pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline.");
        public void BindBuffer(ShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0) => graph.BindBuffer(_pipeline, binding, buffer, offset, sizeInBytes);
        public void BindTexture(ShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler) => graph.BindTexture(_pipeline, binding, texture, sampler);
        public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => graph.PushConstants(_pipeline, data, offset);
        public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1) { if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0) return; graph.Bind(_pipeline); graph.Session.Api.CmdDispatch(graph.Session.CommandBuffer, groupCountX, groupCountY, groupCountZ); }
    }

    internal sealed unsafe class VulkanTransferCommandContext(VulkanRenderGraph graph) : ITransferCommandContext
    {
        public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0) { var src = graph.ResolveBufferAllocation(source).Buffer; var dst = graph.ResolveBufferAllocation(destination).Buffer; var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = sizeInBytes }; graph.Session.Api.CmdCopyBuffer(graph.Session.CommandBuffer, src, dst, 1, &copy); }
        public void CopyTexture(RenderGraphTextureHandle source, in PixelRect sourceRegion, RenderGraphTextureHandle destination, in PixelRect destinationRegion)
        {
            var sourceResource = graph.ResolveTexture(source);
            var destinationResource = graph.ResolveTexture(destination);
            var sourceImage = sourceResource.IsTarget ? graph.Session.GraphImage : sourceResource.Image;
            var destinationImage = destinationResource.IsTarget ? graph.Session.GraphImage : destinationResource.Image;
            if (sourceImage.Handle == default || destinationImage.Handle == default)
            {
                throw new InvalidOperationException("Texture copy resource is unavailable.");
            }

            if (sourceRegion.X < 0 || sourceRegion.Y < 0 || destinationRegion.X < 0 || destinationRegion.Y < 0 ||
                sourceRegion.Width <= 0 || sourceRegion.Height <= 0 ||
                sourceRegion.Width != destinationRegion.Width || sourceRegion.Height != destinationRegion.Height)
            {
                throw new ArgumentException("Texture copy regions must be positive and have equal dimensions.");
            }

            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
                SrcOffset = new Offset3D(sourceRegion.X, sourceRegion.Y, 0),
                DstSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
                DstOffset = new Offset3D(destinationRegion.X, destinationRegion.Y, 0),
                Extent = new Extent3D((uint)sourceRegion.Width, (uint)sourceRegion.Height, 1)
            };
            graph.Session.Api.CmdCopyImage(
                graph.Session.CommandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copy);
        }
        public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0) { var sourceOffset = graph.AllocateStaging(data); var src = graph.StagingBuffer.Buffer; var dst = graph.ResolveBufferAllocation(destination).Buffer; var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = (ulong)data.Length }; graph.Session.Api.CmdCopyBuffer(graph.Session.CommandBuffer, src, dst, 1, &copy); }
        public void UploadTexture(RenderGraphTextureHandle destination, in PixelRect destinationRegion, ReadOnlySpan<byte> data, uint sourceRowPitch)
        {
            var resource = graph.ResolveTexture(destination);
            if (resource.Texture is not { } texture)
            {
                throw new InvalidOperationException("Texture upload requires a registered or graph-owned texture.");
            }

            if (destinationRegion.X < 0 || destinationRegion.Y < 0 || destinationRegion.Width <= 0 || destinationRegion.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationRegion), "Texture upload region must have positive dimensions and non-negative coordinates.");
            }

            var right = checked((uint)destinationRegion.X + (uint)destinationRegion.Width);
            var bottom = checked((uint)destinationRegion.Y + (uint)destinationRegion.Height);
            if (right > texture.Extent.Width || bottom > texture.Extent.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationRegion), "Texture upload region is outside the destination texture.");
            }

            var bytesPerPixel = texture.Format switch
            {
                Format.R8Unorm => 1u,
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4u,
                _ => throw new NotSupportedException($"Texture uploads do not support Vulkan format {texture.Format}.")
            };
            var minimumRowPitch = checked((uint)destinationRegion.Width * bytesPerPixel);
            if (sourceRowPitch < minimumRowPitch || sourceRowPitch % bytesPerPixel != 0)
            {
                throw new ArgumentException("Texture source row pitch is smaller than the region or misaligned to its pixel format.", nameof(sourceRowPitch));
            }

            var requiredBytes = checked((ulong)sourceRowPitch * (uint)destinationRegion.Height);
            if ((ulong)data.Length < requiredBytes)
            {
                throw new ArgumentException("Texture upload data is shorter than sourceRowPitch multiplied by region height.", nameof(data));
            }

            var sourceOffset = graph.AllocateStaging(data[..checked((int)requiredBytes)]);
            var copy = new BufferImageCopy
            {
                BufferOffset = sourceOffset,
                BufferRowLength = sourceRowPitch / bytesPerPixel,
                BufferImageHeight = (uint)destinationRegion.Height,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = new Offset3D(destinationRegion.X, destinationRegion.Y, 0),
                ImageExtent = new Extent3D((uint)destinationRegion.Width, (uint)destinationRegion.Height, 1)
            };
            graph.Session.Api.CmdCopyBufferToImage(graph.Session.CommandBuffer, graph.StagingBuffer.Buffer, texture.Image, ImageLayout.TransferDstOptimal, 1, &copy);
        }
    }
}
