using System.Runtime.InteropServices;
using System.Text;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;
using RenderShaderBinding = Delta.Render.RenderGraph.ShaderBinding;

namespace Delta.Render.Vulkan;

/// <summary>
/// Internal Vulkan executor for the canonical render-graph contract.
/// Handles are frame-local; transient native resources are owned by this graph.
/// </summary>
internal sealed unsafe class VulkanRenderGraph : IRenderGraph, IRenderGraphBuilder, IAsyncDisposable
{
    private readonly VulkanWindowSession _session;
    private readonly List<GraphResource> _resources = new();
    private readonly List<GraphPass> _passes = new();
    private readonly List<VulkanGraphPipeline> _pipelines = new();
    private readonly List<BufferAllocation> _retiredStaging = new();
    private RenderGraphFrame _frame;
    private int[] _executionOrder = Array.Empty<int>();
    private BufferAllocation _staging;
    private bool _built;
    private bool _disposed;

    internal VulkanRenderGraph(VulkanWindowSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal VulkanWindowSession Session => _session;

    public void Build(in RenderGraphFrame frame, ReadOnlySpan<IRenderFeature> features)
    {
        ThrowIfDisposed();
        if (!frame.IsValid || frame.Views.Length != 1 || !frame.Views.Span[0].IsValid)
        {
            throw new ArgumentException("A Vulkan graph requires one valid render view.", nameof(frame));
        }

        ResetFrame();
        _frame = frame;
        var context = new VulkanGraphFeatureContext(frame.FrameNumber, frame.Views.Span[0]);
        try
        {
            for (var i = 0; i < features.Length; i++)
            {
                ArgumentNullException.ThrowIfNull(features[i]);
                features[i].AddPasses(this, context);
            }

            _executionOrder = CompileOrder();
            ValidateRasterSegments();
            _built = true;
        }
        catch
        {
            ResetFrame();
            throw;
        }
    }

    public void Execute()
    {
        ThrowIfDisposed();
        if (!_built)
        {
            throw new InvalidOperationException("RenderGraph.Execute requires a successful Build first.");
        }

        if (!_session.BeginGraphFrame())
        {
            return;
        }

        var states = new GraphResourceState[_resources.Count];
        var rasterActive = false;
        try
        {
            for (var orderIndex = 0; orderIndex < _executionOrder.Length; orderIndex++)
            {
                var pass = _passes[_executionOrder[orderIndex]];
                EmitBarriers(pass, states);

                if (pass.Kind == GraphPassKind.Raster)
                {
                    if (!rasterActive)
                    {
                        BeginRasterPass(pass);
                        rasterActive = true;
                    }

                    var commands = new VulkanRasterCommandContext(this, pass.Pipeline);
                    pass.RasterPass!.Record(commands);
                }
                else
                {
                    if (rasterActive)
                    {
                        _session.Api.CmdEndRenderPass(_session.CommandBuffer);
                        rasterActive = false;
                    }

                    if (pass.Kind == GraphPassKind.Compute)
                    {
                        var commands = new VulkanComputeCommandContext(this, pass.Pipeline);
                        pass.ComputePass!.Record(commands);
                    }
                    else
                    {
                        var commands = new VulkanTransferCommandContext(this);
                        pass.TransferPass!.Record(commands);
                    }
                }

                UpdateStates(pass, states);
            }

            if (rasterActive)
            {
                _session.Api.CmdEndRenderPass(_session.CommandBuffer);
            }

            if (!_session.EndGraphFrame())
            {
                throw new InvalidOperationException("Vulkan render-graph queue submission or presentation failed.");
            }
        }
        catch
        {
            _session.AbortGraphFrame();
            throw;
        }
        finally
        {
            _built = false;
            ResetFrame();
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeSynchronously();
        return ValueTask.CompletedTask;
    }

    internal void DisposeSynchronously()
    {
        if (!_disposed)
        {
            _disposed = true;
            ResetFrame();
            DisposeStaging();
        }
    }

    public RenderGraphTextureHandle ImportSurface(RenderSurfaceHandle surface)
    {
        ThrowIfBuilding();
        if (!surface.IsValid)
        {
            throw new ArgumentException("The imported surface handle is invalid.", nameof(surface));
        }

        if (surface != _session.SurfaceHandle)
        {
            throw new InvalidOperationException("The imported surface handle does not belong to this render session.");
        }

        return AddResource(GraphResource.Surface(surface));
    }

    public RenderGraphTextureHandle ImportTexture(RenderTextureHandle texture)
    {
        ThrowIfBuilding();
        if (!texture.IsValid || !_session.ResourceRegistry.TryGetTexture(texture, out var resource))
        {
            throw new InvalidOperationException($"The Vulkan texture handle {texture.Value}/{texture.Generation} is unknown or stale.");
        }

        return AddResource(GraphResource.ImportedTexture(resource));
    }

    public RenderGraphBufferHandle ImportBuffer(RenderBufferHandle buffer)
    {
        ThrowIfBuilding();
        if (!buffer.IsValid || !_session.ResourceRegistry.TryGetBuffer(buffer, out var resource))
        {
            throw new InvalidOperationException($"The Vulkan buffer handle {buffer.Value}/{buffer.Generation} is unknown or stale.");
        }

        return AddBufferResource(GraphResource.ImportedBuffer(resource));
    }

    public RenderGraphTextureHandle CreateTexture(in RenderTextureDescription description)
    {
        ThrowIfBuilding();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient texture description is invalid.", nameof(description));
        }

        return AddResource(GraphResource.Texture(CreateImage(description)));
    }

    public RenderGraphBufferHandle CreateBuffer(in RenderBufferDescription description)
    {
        ThrowIfBuilding();
        if (!description.IsValid)
        {
            throw new ArgumentException("The transient buffer description is invalid.", nameof(description));
        }

        var usage = ToVulkanBufferUsage(description.Usage);
        var allocation = _session.CreateBuffer(description.SizeInBytes, usage, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
        return AddBufferResource(GraphResource.Buffer(allocation, description));
    }

    public RenderGraphPassHandle AddRasterPass(in RasterPassDescription description, IRasterPass pass)
    {
        ThrowIfBuilding();
        ArgumentNullException.ThrowIfNull(pass);
        var pipeline = VulkanGraphPipeline.CreateRaster(_session, description.Pipeline);
        _pipelines.Add(pipeline);
        _passes.Add(GraphPass.Raster(description.Name, pipeline, pass));
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddComputePass(in ComputePassDescription description, IComputePass pass)
    {
        ThrowIfBuilding();
        ArgumentNullException.ThrowIfNull(pass);
        var pipeline = VulkanGraphPipeline.CreateCompute(_session, description.Pipeline.Shader);
        _pipelines.Add(pipeline);
        _passes.Add(GraphPass.Compute(description.Name, pipeline, pass));
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public RenderGraphPassHandle AddTransferPass(in TransferPassDescription description, ITransferPass pass)
    {
        ThrowIfBuilding();
        ArgumentNullException.ThrowIfNull(pass);
        _passes.Add(GraphPass.Transfer(description.Name, pass));
        return new RenderGraphPassHandle((uint)_passes.Count);
    }

    public void UseColorAttachment(RenderGraphPassHandle pass, uint index, in ColorAttachmentDescription attachment)
    {
        var node = GetPass(pass);
        if (node.Kind != GraphPassKind.Raster || index != 0 || !attachment.Texture.IsValid)
        {
            throw new ArgumentException("The current Vulkan graph supports one color attachment at index zero.", nameof(attachment));
        }

        var resource = GetTexture(attachment.Texture);
        if (!resource.IsSurface)
        {
            throw new NotSupportedException("Transient color attachments require a dedicated render-pass bridge and are not enabled yet.");
        }

        node.ClearColor = attachment.ClearValue;
        node.HasColorAttachment = true;
        AddUse(node, resource, RenderResourceAccess.Write, RenderPipelineStages.ColorOutput);
    }

    public void UseDepthStencilAttachment(RenderGraphPassHandle pass, in DepthStencilAttachmentDescription attachment)
        => throw new NotSupportedException("Depth-stencil attachments are not enabled by the current Vulkan session render pass.");

    public void UseTexture(RenderGraphPassHandle pass, RenderGraphTextureHandle texture, RenderResourceAccess access, RenderPipelineStages stages)
    {
        var node = GetPass(pass);
        AddUse(node, GetTexture(texture), access, stages);
    }

    public void UseBuffer(RenderGraphPassHandle pass, RenderGraphBufferHandle buffer, RenderResourceAccess access, RenderPipelineStages stages)
    {
        var node = GetPass(pass);
        AddUse(node, GetBuffer(buffer), access, stages);
    }

    internal GraphResource ResolveTexture(RenderGraphTextureHandle handle) => GetTexture(handle);
    internal GraphResource ResolveBuffer(RenderGraphBufferHandle handle) => GetBuffer(handle);
    internal Sampler ResolveSampler(RenderSamplerHandle handle, GraphResource resource)
    {
        if (!handle.IsValid)
        {
            throw new ArgumentException("The graph sampler handle is invalid.", nameof(handle));
        }

        if (_session.ResourceRegistry.TryGetSampler(handle, out var sampler))
        {
            return sampler;
        }

        var image = resource.Image;
        if (image is not null && image.Sampler.Handle != default)
        {
            return image.Sampler;
        }

        throw new InvalidOperationException($"The Vulkan sampler handle {handle.Value}/{handle.Generation} is unknown or stale.");
    }

    internal BufferAllocation EnsureStaging(ulong requiredBytes)
    {
        if (requiredBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        if (VulkanBufferAllocation.IsLive(in _staging) && _staging.AllocationSize >= requiredBytes)
        {
            return _staging;
        }

        var capacity = Math.Max(requiredBytes, VulkanBufferAllocation.IsLive(in _staging) ? checked(_staging.AllocationSize * 2) : 4096UL);
        var replacement = _session.CreateBuffer(
            capacity,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        if (VulkanBufferAllocation.IsLive(in _staging))
        {
            _retiredStaging.Add(_staging);
        }

        _staging = replacement;
        return replacement;
    }

    internal void WriteStaging(ReadOnlySpan<byte> data)
    {
        var allocation = _staging;
        void* mapped = null;
        Ensure(_session.Api.MapMemory(_session.Device, allocation.Memory, 0, allocation.AllocationSize, 0, &mapped), "MapMemory(graph staging)");
        try
        {
            fixed (byte* source = data)
            {
                System.Buffer.MemoryCopy(source, mapped, allocation.AllocationSize, (nuint)data.Length);
            }

            if (!allocation.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                var range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = allocation.Memory,
                    Size = (nuint)data.Length
                };
                Ensure(_session.Api.FlushMappedMemoryRanges(_session.Device, 1, &range), "FlushMappedMemoryRanges(graph staging)");
            }
        }
        finally
        {
            _session.Api.UnmapMemory(_session.Device, allocation.Memory);
        }
    }

    internal void AddStagingReadBarrier()
    {
        var allocation = _staging;
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.HostWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = allocation.Buffer,
            Offset = 0,
            Size = allocation.AllocationSize
        };
        _session.Api.CmdPipelineBarrier(
            _session.CommandBuffer,
            PipelineStageFlags.HostBit,
            PipelineStageFlags.TransferBit,
            DependencyFlags.None,
            ReadOnlySpan<MemoryBarrier>.Empty,
            new[] { barrier },
            ReadOnlySpan<ImageMemoryBarrier>.Empty);
    }

    private void BeginRasterPass(GraphPass pass)
    {
        if (!pass.HasColorAttachment)
        {
            throw new InvalidOperationException($"Raster pass '{pass.Name}' has no color attachment.");
        }

        var clearValue = new ClearValue(new ClearColorValue(
            pass.ClearColor.Red,
            pass.ClearColor.Green,
            pass.ClearColor.Blue,
            pass.ClearColor.Alpha));
        var beginInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _session.GraphRenderPass,
            Framebuffer = _session.GraphFramebuffer,
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _session.GraphExtent },
            ClearValueCount = 1,
            PClearValues = &clearValue
        };
        _session.Api.CmdBeginRenderPass(_session.CommandBuffer, &beginInfo, SubpassContents.Inline);
    }

    private void EmitBarriers(GraphPass pass, GraphResourceState[] states)
    {
        var buffers = new List<BufferMemoryBarrier>();
        var images = new List<ImageMemoryBarrier>();
        for (var i = 0; i < pass.Uses.Count; i++)
        {
            var use = pass.Uses[i];
            var previous = states[use.Resource.Index];
            var next = GraphResourceState.For(use.Access, use.Stages);
            if (use.Resource.IsSurface)
            {
                continue;
            }

            if (use.Resource.IsBuffer)
            {
                var allocation = use.Resource.BufferAllocation;
                buffers.Add(new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = previous.Access,
                    DstAccessMask = next.Access,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = allocation.Buffer,
                    Offset = 0,
                    Size = allocation.AllocationSize
                });
            }
            else if (use.Resource.Image is not null &&
                     (previous.Layout != next.Layout || previous.Access != next.Access))
            {
                images.Add(new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = previous.Access,
                    DstAccessMask = next.Access,
                    OldLayout = previous.Layout,
                    NewLayout = next.Layout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = use.Resource.Image.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = use.Resource.Image.Aspect,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                });
            }
        }

        if (buffers.Count == 0 && images.Count == 0)
        {
            return;
        }

        var sourceStages = PipelineStageFlags.TopOfPipeBit;
        var destinationStages = PipelineStageFlags.BottomOfPipeBit;
        for (var i = 0; i < pass.Uses.Count; i++)
        {
            sourceStages |= states[pass.Uses[i].Resource.Index].Stages;
            destinationStages |= ToVulkanStages(pass.Uses[i].Stages);
        }

        _session.Api.CmdPipelineBarrier(
            _session.CommandBuffer,
            sourceStages,
            destinationStages,
            DependencyFlags.None,
            ReadOnlySpan<MemoryBarrier>.Empty,
            CollectionsMarshal.AsSpan(buffers),
            CollectionsMarshal.AsSpan(images));
    }

    private static void UpdateStates(GraphPass pass, GraphResourceState[] states)
    {
        for (var i = 0; i < pass.Uses.Count; i++)
        {
            var use = pass.Uses[i];
            states[use.Resource.Index] = GraphResourceState.For(use.Access, use.Stages);
        }
    }

    private int[] CompileOrder()
    {
        var edges = new HashSet<int>[_passes.Count];
        var indegree = new int[_passes.Count];
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = new HashSet<int>();
        }

        var lastWriter = new int[_resources.Count];
        Array.Fill(lastWriter, -1);
        var readers = new List<int>[_resources.Count];
        for (var i = 0; i < readers.Length; i++)
        {
            readers[i] = new List<int>();
        }

        for (var passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            foreach (var use in _passes[passIndex].Uses)
            {
                var resourceIndex = use.Resource.Index;
                if (lastWriter[resourceIndex] >= 0)
                {
                    AddEdge(lastWriter[resourceIndex], passIndex, edges, indegree);
                }

                if (use.Access.HasFlag(RenderResourceAccess.Write))
                {
                    foreach (var reader in readers[resourceIndex])
                    {
                        AddEdge(reader, passIndex, edges, indegree);
                    }

                    readers[resourceIndex].Clear();
                    lastWriter[resourceIndex] = passIndex;
                }
                else if (use.Access.HasFlag(RenderResourceAccess.Read))
                {
                    readers[resourceIndex].Add(passIndex);
                }
            }
        }

        var ready = new Queue<int>();
        for (var i = 0; i < indegree.Length; i++)
        {
            if (indegree[i] == 0)
            {
                ready.Enqueue(i);
            }
        }

        var result = new int[_passes.Count];
        var count = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            result[count++] = current;
            foreach (var next in edges[current])
            {
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        if (count != result.Length)
        {
            throw new InvalidOperationException("Vulkan render graph contains a dependency cycle.");
        }

        return result;
    }

    private void ValidateRasterSegments()
    {
        var seenRaster = false;
        var closedRaster = false;
        foreach (var index in _executionOrder)
        {
            var raster = _passes[index].Kind == GraphPassKind.Raster;
            if (raster && closedRaster)
            {
                throw new InvalidOperationException("Raster passes must form one contiguous render-pass segment in this Vulkan implementation.");
            }

            if (raster)
            {
                seenRaster = true;
            }
            else if (seenRaster)
            {
                closedRaster = true;
            }
        }
    }

    private void ResetFrame()
    {
        foreach (var pipeline in _pipelines)
        {
            pipeline.Dispose(_session);
        }

        _pipelines.Clear();
        foreach (var resource in _resources)
        {
            resource.Dispose(_session);
        }

        _resources.Clear();
        foreach (var staging in _retiredStaging)
        {
            _session.DestroyAllocation(staging);
        }

        _retiredStaging.Clear();
        _passes.Clear();
        _executionOrder = Array.Empty<int>();
        _built = false;
    }

    private void DisposeStaging()
    {
        if (VulkanBufferAllocation.IsLive(in _staging))
        {
            _session.DestroyAllocation(_staging);
            _staging = default;
        }

        foreach (var staging in _retiredStaging)
        {
            _session.DestroyAllocation(staging);
        }

        _retiredStaging.Clear();
    }

    private RenderGraphTextureHandle AddResource(GraphResource resource)
    {
        resource.Index = _resources.Count;
        _resources.Add(resource);
        return new RenderGraphTextureHandle((uint)_resources.Count);
    }

    private RenderGraphBufferHandle AddBufferResource(GraphResource resource)
    {
        resource.Index = _resources.Count;
        _resources.Add(resource);
        return new RenderGraphBufferHandle((uint)_resources.Count);
    }

    private GraphResource GetTexture(RenderGraphTextureHandle handle)
    {
        if (!handle.IsValid || handle.Value > (uint)_resources.Count)
        {
            throw new ArgumentException("The graph texture handle is invalid.", nameof(handle));
        }

        var resource = _resources[(int)handle.Value - 1];
        if (!resource.IsTexture)
        {
            throw new ArgumentException("The graph handle is not a texture.", nameof(handle));
        }

        return resource;
    }

    private GraphResource GetBuffer(RenderGraphBufferHandle handle)
    {
        if (!handle.IsValid || handle.Value > (uint)_resources.Count)
        {
            throw new ArgumentException("The graph buffer handle is invalid.", nameof(handle));
        }

        var resource = _resources[(int)handle.Value - 1];
        if (!resource.IsBuffer)
        {
            throw new ArgumentException("The graph handle is not a buffer.", nameof(handle));
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

    private void AddUse(GraphPass pass, GraphResource resource, RenderResourceAccess access, RenderPipelineStages stages)
    {
        if (access == RenderResourceAccess.None || stages == RenderPipelineStages.None)
        {
            throw new ArgumentException("A graph resource use requires non-empty access and stages.");
        }

        pass.Uses.Add(new GraphUse(resource, access, stages));
    }

    private void ThrowIfBuilding()
    {
        ThrowIfDisposed();
        if (_built)
        {
            throw new InvalidOperationException("Graph resources and passes can only be registered before Execute.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void AddEdge(int from, int to, HashSet<int>[] edges, int[] indegree)
    {
        if (from != to && edges[from].Add(to))
        {
            indegree[to]++;
        }
    }

    private VulkanGraphImage CreateImage(in RenderTextureDescription description)
        => VulkanGraphImage.Create(_session, description);

    private static BufferUsageFlags ToVulkanBufferUsage(RenderBufferUsage usage)
    {
        var result = BufferUsageFlags.None;
        if (usage.HasFlag(RenderBufferUsage.Vertex)) result |= BufferUsageFlags.VertexBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Index)) result |= BufferUsageFlags.IndexBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Uniform)) result |= BufferUsageFlags.UniformBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Storage)) result |= BufferUsageFlags.StorageBufferBit;
        if (usage.HasFlag(RenderBufferUsage.Indirect)) result |= BufferUsageFlags.IndirectBufferBit;
        if (usage.HasFlag(RenderBufferUsage.TransferSource)) result |= BufferUsageFlags.TransferSrcBit;
        if (usage.HasFlag(RenderBufferUsage.TransferDestination)) result |= BufferUsageFlags.TransferDstBit;
        return result;
    }

    private static PipelineStageFlags ToVulkanStages(RenderPipelineStages stages)
    {
        var result = PipelineStageFlags.None;
        if (stages.HasFlag(RenderPipelineStages.Transfer)) result |= PipelineStageFlags.TransferBit;
        if (stages.HasFlag(RenderPipelineStages.Vertex)) result |= PipelineStageFlags.VertexShaderBit;
        if (stages.HasFlag(RenderPipelineStages.Fragment)) result |= PipelineStageFlags.FragmentShaderBit;
        if (stages.HasFlag(RenderPipelineStages.Compute)) result |= PipelineStageFlags.ComputeShaderBit;
        if (stages.HasFlag(RenderPipelineStages.ColorOutput)) result |= PipelineStageFlags.ColorAttachmentOutputBit;
        if (stages.HasFlag(RenderPipelineStages.DepthStencil)) result |= PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        return result == PipelineStageFlags.None ? PipelineStageFlags.AllCommandsBit : result;
    }

    private static AccessFlags ToVulkanAccess(RenderResourceAccess access, RenderPipelineStages stages)
    {
        var result = AccessFlags.None;
        if (stages.HasFlag(RenderPipelineStages.Transfer))
        {
            if (access.HasFlag(RenderResourceAccess.Read)) result |= AccessFlags.TransferReadBit;
            if (access.HasFlag(RenderResourceAccess.Write)) result |= AccessFlags.TransferWriteBit;
        }
        if (stages.HasFlag(RenderPipelineStages.ColorOutput))
        {
            if (access.HasFlag(RenderResourceAccess.Read)) result |= AccessFlags.ColorAttachmentReadBit;
            if (access.HasFlag(RenderResourceAccess.Write)) result |= AccessFlags.ColorAttachmentWriteBit;
        }
        if (stages.HasFlag(RenderPipelineStages.DepthStencil))
        {
            if (access.HasFlag(RenderResourceAccess.Read)) result |= AccessFlags.DepthStencilAttachmentReadBit;
            if (access.HasFlag(RenderResourceAccess.Write)) result |= AccessFlags.DepthStencilAttachmentWriteBit;
        }
        if (stages.HasFlag(RenderPipelineStages.Vertex | RenderPipelineStages.Fragment | RenderPipelineStages.Compute))
        {
            if (access.HasFlag(RenderResourceAccess.Read)) result |= AccessFlags.ShaderReadBit;
            if (access.HasFlag(RenderResourceAccess.Write)) result |= AccessFlags.ShaderWriteBit;
        }
        return result;
    }

    private static ImageLayout ToImageLayout(RenderResourceAccess access, RenderPipelineStages stages)
    {
        if (stages.HasFlag(RenderPipelineStages.ColorOutput)) return ImageLayout.ColorAttachmentOptimal;
        if (stages.HasFlag(RenderPipelineStages.DepthStencil)) return ImageLayout.DepthStencilAttachmentOptimal;
        if (stages.HasFlag(RenderPipelineStages.Transfer)) return access.HasFlag(RenderResourceAccess.Write) ? ImageLayout.TransferDstOptimal : ImageLayout.TransferSrcOptimal;
        if (access.HasFlag(RenderResourceAccess.Write)) return ImageLayout.General;
        return ImageLayout.ShaderReadOnlyOptimal;
    }

    private sealed class VulkanGraphFeatureContext(long frameNumber, RenderView view) : IRenderFeatureContext
    {
        public long FrameNumber { get; } = frameNumber;
        public RenderView View { get; } = view;
    }

    private enum GraphPassKind { Raster, Compute, Transfer }

    private sealed class GraphPass
    {
        internal string Name = string.Empty;
        internal GraphPassKind Kind;
        internal VulkanGraphPipeline? Pipeline;
        internal IRasterPass? RasterPass;
        internal IComputePass? ComputePass;
        internal ITransferPass? TransferPass;
        internal readonly List<GraphUse> Uses = new();
        internal bool HasColorAttachment;
        internal ClearColor ClearColor;

        internal static GraphPass Raster(string name, VulkanGraphPipeline pipeline, IRasterPass pass) => new() { Name = name, Kind = GraphPassKind.Raster, Pipeline = pipeline, RasterPass = pass };
        internal static GraphPass Compute(string name, VulkanGraphPipeline pipeline, IComputePass pass) => new() { Name = name, Kind = GraphPassKind.Compute, Pipeline = pipeline, ComputePass = pass };
        internal static GraphPass Transfer(string name, ITransferPass pass) => new() { Name = name, Kind = GraphPassKind.Transfer, TransferPass = pass };
    }

    internal sealed class GraphResource
    {
        internal int Index;
        internal bool IsTexture;
        internal bool IsBuffer;
        internal bool IsSurface;
        internal bool OwnsNativeResources;
        internal VulkanGraphImage? Image;
        internal BufferAllocation BufferAllocation;
        internal RenderBufferDescription BufferDescription;

        internal static GraphResource Surface(RenderSurfaceHandle handle) => new() { IsTexture = true, IsSurface = true };
        internal static GraphResource Texture(VulkanGraphImage image) => new() { IsTexture = true, Image = image, OwnsNativeResources = true };
        internal static GraphResource ImportedTexture(GraphResource resource) => new() { IsTexture = true, Image = resource.Image };
        internal static GraphResource Buffer(BufferAllocation allocation, RenderBufferDescription description) => new() { IsBuffer = true, BufferAllocation = allocation, BufferDescription = description, OwnsNativeResources = true };
        internal static GraphResource ImportedBuffer(GraphResource resource) => new() { IsBuffer = true, BufferAllocation = resource.BufferAllocation, BufferDescription = resource.BufferDescription };

        internal void Dispose(VulkanWindowSession session)
        {
            if (OwnsNativeResources && IsBuffer && VulkanBufferAllocation.IsLive(in BufferAllocation))
            {
                session.DestroyAllocation(BufferAllocation);
                BufferAllocation = default;
            }
            if (OwnsNativeResources)
            {
                Image?.Dispose(session);
            }
            Image = null;
        }
    }

    private readonly record struct GraphUse(GraphResource Resource, RenderResourceAccess Access, RenderPipelineStages Stages);

    private readonly record struct GraphResourceState(PipelineStageFlags Stages, AccessFlags Access, ImageLayout Layout)
    {
        internal static GraphResourceState For(RenderResourceAccess access, RenderPipelineStages stages)
            => new(ToVulkanStages(stages), ToVulkanAccess(access, stages), ToImageLayout(access, stages));
    }

    internal sealed class VulkanGraphImage
    {
        internal Image Image;
        internal DeviceMemory Memory;
        internal ImageView View;
        internal Sampler Sampler;
        internal ImageAspectFlags Aspect;
        internal RenderTextureFormat Format;
        internal uint Width;
        internal uint Height;

        internal static VulkanGraphImage Create(VulkanWindowSession session, in RenderTextureDescription description)
        {
            var api = session.Api;
            var format = ToVulkanFormat(description.Format);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(description.Width, description.Height, 1),
                MipLevels = description.MipLevels,
                ArrayLayers = description.Layers,
                Samples = ToSampleCount(description.Samples),
                Tiling = ImageTiling.Optimal,
                Usage = ToVulkanImageUsage(description.Usage),
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            Ensure(api.CreateImage(session.Device, imageInfo, null, out var image), "CreateImage(graph)");
            DeviceMemory memory = default;
            ImageView view = default;
            Sampler sampler = default;
            try
            {
                var requirements = api.GetImageMemoryRequirements(session.Device, image);
                var memoryType = session.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit);
                Ensure(api.AllocateMemory(session.Device, new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = memoryType
                }, null, out memory), "AllocateMemory(graph image)");
                Ensure(api.BindImageMemory(session.Device, image, memory, 0), "BindImageMemory(graph)");

                var aspect = IsDepthFormat(description.Format) ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
                if (description.Usage.HasFlag(RenderTextureUsage.Sampled) ||
                    description.Usage.HasFlag(RenderTextureUsage.Storage) ||
                    description.Usage.HasFlag(RenderTextureUsage.TransferSource) ||
                    description.Usage.HasFlag(RenderTextureUsage.TransferDestination))
                {
                    Ensure(api.CreateImageView(session.Device, new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = image,
                        ViewType = ImageViewType.Type2D,
                        Format = format,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = aspect,
                            BaseMipLevel = 0,
                            LevelCount = description.MipLevels,
                            BaseArrayLayer = 0,
                            LayerCount = description.Layers
                        }
                    }, null, out view), "CreateImageView(graph)");
                }

                if (description.Usage.HasFlag(RenderTextureUsage.Sampled))
                {
                    Ensure(api.CreateSampler(session.Device, new SamplerCreateInfo
                    {
                        SType = StructureType.SamplerCreateInfo,
                        MagFilter = Filter.Linear,
                        MinFilter = Filter.Linear,
                        MipmapMode = SamplerMipmapMode.Linear,
                        AddressModeU = SamplerAddressMode.ClampToEdge,
                        AddressModeV = SamplerAddressMode.ClampToEdge,
                        AddressModeW = SamplerAddressMode.ClampToEdge,
                        MaxLod = description.MipLevels
                    }, null, out sampler), "CreateSampler(graph)");
                }

                return new VulkanGraphImage { Image = image, Memory = memory, View = view, Sampler = sampler, Aspect = aspect, Format = description.Format, Width = description.Width, Height = description.Height };
            }
            catch
            {
                if (sampler.Handle != default) api.DestroySampler(session.Device, sampler, null);
                if (view.Handle != default) api.DestroyImageView(session.Device, view, null);
                if (memory.Handle != default) api.FreeMemory(session.Device, memory, null);
                api.DestroyImage(session.Device, image, null);
                throw;
            }
        }

        internal void Dispose(VulkanWindowSession session)
        {
            var api = session.Api;
            if (Sampler.Handle != default) api.DestroySampler(session.Device, Sampler, null);
            if (View.Handle != default) api.DestroyImageView(session.Device, View, null);
            if (Image.Handle != default) api.DestroyImage(session.Device, Image, null);
            if (Memory.Handle != default) api.FreeMemory(session.Device, Memory, null);
            Image = default;
            Memory = default;
            View = default;
            Sampler = default;
        }

        private static ImageUsageFlags ToVulkanImageUsage(RenderTextureUsage usage)
        {
            var result = ImageUsageFlags.None;
            if (usage.HasFlag(RenderTextureUsage.Sampled)) result |= ImageUsageFlags.SampledBit;
            if (usage.HasFlag(RenderTextureUsage.Storage)) result |= ImageUsageFlags.StorageBit;
            if (usage.HasFlag(RenderTextureUsage.ColorAttachment)) result |= ImageUsageFlags.ColorAttachmentBit;
            if (usage.HasFlag(RenderTextureUsage.DepthStencilAttachment)) result |= ImageUsageFlags.DepthStencilAttachmentBit;
            if (usage.HasFlag(RenderTextureUsage.TransferSource)) result |= ImageUsageFlags.TransferSrcBit;
            if (usage.HasFlag(RenderTextureUsage.TransferDestination)) result |= ImageUsageFlags.TransferDstBit;
            return result;
        }

        private static Format ToVulkanFormat(RenderTextureFormat format) => format switch
        {
            RenderTextureFormat.R8Unorm => Silk.NET.Vulkan.Format.R8Unorm,
            RenderTextureFormat.Rgba8Unorm => Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            RenderTextureFormat.Rgba8Srgb => Silk.NET.Vulkan.Format.R8G8B8A8Srgb,
            RenderTextureFormat.Bgra8Unorm => Silk.NET.Vulkan.Format.B8G8R8A8Unorm,
            RenderTextureFormat.Bgra8Srgb => Silk.NET.Vulkan.Format.B8G8R8A8Srgb,
            RenderTextureFormat.Rgba16Float => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
            RenderTextureFormat.D32Float => Silk.NET.Vulkan.Format.D32Sfloat,
            RenderTextureFormat.D24UnormS8UInt => Silk.NET.Vulkan.Format.D24UnormS8Uint,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported Vulkan graph texture format.")
        };

        private static SampleCountFlags ToSampleCount(uint samples) => samples switch
        {
            1 => SampleCountFlags.Count1Bit,
            2 => SampleCountFlags.Count2Bit,
            4 => SampleCountFlags.Count4Bit,
            8 => SampleCountFlags.Count8Bit,
            16 => SampleCountFlags.Count16Bit,
            32 => SampleCountFlags.Count32Bit,
            64 => SampleCountFlags.Count64Bit,
            _ => throw new ArgumentOutOfRangeException(nameof(samples), samples, "Unsupported Vulkan sample count.")
        };

        private static bool IsDepthFormat(RenderTextureFormat format) => format is RenderTextureFormat.D32Float or RenderTextureFormat.D24UnormS8UInt;
    }

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{operation} failed: {result}");
    }
}

internal sealed unsafe class VulkanGraphResourceRegistry
{
    private readonly Dictionary<RenderTextureHandle, VulkanRenderGraph.GraphResource> _textures = new();
    private readonly Dictionary<RenderBufferHandle, VulkanRenderGraph.GraphResource> _buffers = new();
    private readonly Dictionary<RenderSamplerHandle, Sampler> _samplers = new();
    private bool _disposed;

    internal void RegisterTexture(RenderTextureHandle handle, VulkanRenderGraph.GraphResource resource)
    {
        ThrowIfDisposed();
        ValidateHandle(handle, nameof(handle));
        ArgumentNullException.ThrowIfNull(resource);
        if (!_textures.TryAdd(handle, resource))
        {
            throw new InvalidOperationException($"The Vulkan texture handle {handle.Value}/{handle.Generation} is already registered.");
        }
    }

    internal void RegisterBuffer(RenderBufferHandle handle, VulkanRenderGraph.GraphResource resource)
    {
        ThrowIfDisposed();
        ValidateHandle(handle, nameof(handle));
        ArgumentNullException.ThrowIfNull(resource);
        if (!_buffers.TryAdd(handle, resource))
        {
            throw new InvalidOperationException($"The Vulkan buffer handle {handle.Value}/{handle.Generation} is already registered.");
        }
    }

    internal void RegisterSampler(RenderSamplerHandle handle, Sampler sampler)
    {
        ThrowIfDisposed();
        ValidateHandle(handle, nameof(handle));
        if (sampler.Handle == default || !_samplers.TryAdd(handle, sampler))
        {
            throw new InvalidOperationException($"The Vulkan sampler handle {handle.Value}/{handle.Generation} is invalid or already registered.");
        }
    }

    internal bool TryGetTexture(RenderTextureHandle handle, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VulkanRenderGraph.GraphResource? resource)
    {
        if (_disposed)
        {
            resource = null;
            return false;
        }

        return _textures.TryGetValue(handle, out resource);
    }

    internal bool TryGetBuffer(RenderBufferHandle handle, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VulkanRenderGraph.GraphResource? resource)
    {
        if (_disposed)
        {
            resource = null;
            return false;
        }

        return _buffers.TryGetValue(handle, out resource);
    }

    internal bool TryGetSampler(RenderSamplerHandle handle, out Sampler sampler)
    {
        if (_disposed)
        {
            sampler = default;
            return false;
        }

        return _samplers.TryGetValue(handle, out sampler);
    }

    internal void Dispose(VulkanWindowSession session)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var seen = new HashSet<VulkanRenderGraph.GraphResource>();
        foreach (var resource in _textures.Values)
        {
            if (seen.Add(resource)) resource.Dispose(session);
        }

        foreach (var resource in _buffers.Values)
        {
            if (seen.Add(resource)) resource.Dispose(session);
        }

        foreach (var sampler in _samplers.Values)
        {
            if (sampler.Handle != default) session.Api.DestroySampler(session.Device, sampler, null);
        }

        _textures.Clear();
        _buffers.Clear();
        _samplers.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateHandle(RenderTextureHandle handle, string name)
    {
        if (!handle.IsValid) throw new ArgumentException("The renderer handle is invalid.", name);
    }

    private static void ValidateHandle(RenderBufferHandle handle, string name)
    {
        if (!handle.IsValid) throw new ArgumentException("The renderer handle is invalid.", name);
    }

    private static void ValidateHandle(RenderSamplerHandle handle, string name)
    {
        if (!handle.IsValid) throw new ArgumentException("The renderer handle is invalid.", name);
    }
}

internal sealed unsafe class VulkanGraphPipeline
{
    internal readonly record struct GraphBinding(
        RenderShaderBinding Binding,
        DescriptorType DescriptorType,
        ShaderStageFlags StageFlags);

    private readonly DescriptorSetLayout[] _descriptorSetLayouts;
    private readonly DescriptorPool _descriptorPool;
    private bool _disposed;

    internal Pipeline Pipeline { get; }
    internal PipelineLayout Layout { get; }
    internal PipelineBindPoint BindPoint { get; }
    internal DescriptorSet[] DescriptorSets { get; }
    internal GraphBinding[] Bindings { get; }
    internal ShaderStageFlags StageFlags { get; }
    internal uint PushConstantSize { get; }

    private VulkanGraphPipeline(
        Pipeline pipeline,
        PipelineLayout layout,
        PipelineBindPoint bindPoint,
        DescriptorSetLayout[] descriptorSetLayouts,
        DescriptorPool descriptorPool,
        DescriptorSet[] descriptorSets,
        GraphBinding[] bindings,
        ShaderStageFlags stageFlags,
        uint pushConstantSize)
    {
        Pipeline = pipeline;
        Layout = layout;
        BindPoint = bindPoint;
        _descriptorSetLayouts = descriptorSetLayouts;
        _descriptorPool = descriptorPool;
        DescriptorSets = descriptorSets;
        Bindings = bindings;
        StageFlags = stageFlags;
        PushConstantSize = pushConstantSize;
    }

    internal static VulkanGraphPipeline CreateCompute(VulkanWindowSession session, IShaderArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Stage != ShaderStage.Compute)
        {
            throw new ArgumentException("A graph compute pass requires a compute artifact.", nameof(artifact));
        }
        ValidateArtifact(artifact, true);

        var bindings = BuildBindings(artifact.Abi.Resources, ShaderStageMask.Compute, out var maxSet);
        CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var sets);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = CreateShaderModule(session, artifact.Spirv, "compute");
            var pushRanges = CreatePushRanges(artifact.Abi.PushConstants, ShaderStageFlags.ComputeBit, out var pushSize);
            fixed (DescriptorSetLayout* layoutPtr = layouts)
            fixed (PushConstantRange* pushPtr = pushRanges)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layouts.Length,
                    PSetLayouts = layoutPtr,
                    PushConstantRangeCount = (uint)pushRanges.Length,
                    PPushConstantRanges = pushPtr
                };
                Ensure(session.Api.CreatePipelineLayout(session.Device, layoutInfo, null, out pipelineLayout), "CreatePipelineLayout(graph compute)");
            }

            var entryPoint = SpirvEntryPointReader.ReadComputeEntryPoint(artifact.Spirv);
            if (!string.Equals(entryPoint, artifact.EntryPoint, StringComparison.Ordinal))
            {
                throw new ArgumentException("The compute artifact entry point does not match SPIR-V.", nameof(artifact));
            }

            var entryPointBytes = Encoding.UTF8.GetBytes(entryPoint + "\0");
            fixed (byte* entryPointPtr = entryPointBytes)
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = entryPointPtr
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = pipelineLayout
                };
                var outputs = stackalloc Pipeline[1];
                Ensure(session.Api.CreateComputePipelines(session.Device, default, &pipelineInfo, null, new Span<Pipeline>(outputs, 1)), "CreateComputePipelines(graph)");
                pipeline = outputs[0];
            }

            session.Api.DestroyShaderModule(session.Device, shaderModule, null);
            shaderModule = default;
            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Compute, layouts, pool, sets, bindings, ShaderStageFlags.ComputeBit, GetPushConstantSize(artifact.Abi.PushConstants));
        }
        catch
        {
            if (shaderModule.Handle != default) session.Api.DestroyShaderModule(session.Device, shaderModule, null);
            if (pipeline.Handle != default) session.Api.DestroyPipeline(session.Device, pipeline, null);
            if (pipelineLayout.Handle != default) session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
            DestroyDescriptorState(session, layouts, pool);
            throw;
        }
    }

    internal static VulkanGraphPipeline CreateRaster(VulkanWindowSession session, RasterPipelineDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var shader = description.ShaderProgram;
        ValidateArtifact(shader.Vertex, false);
        ValidateArtifact(shader.Fragment, false);
        var mergedResources = MergeResources(shader.Vertex.Abi.Resources, shader.Fragment.Abi.Resources);
        var bindings = BuildBindings(mergedResources, ShaderStageMask.AllGraphics, out var maxSet);
        CreateDescriptorState(session, bindings, maxSet, out var layouts, out var pool, out var sets);
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        ShaderModule vertexModule = default;
        ShaderModule fragmentModule = default;
        try
        {
            vertexModule = CreateShaderModule(session, shader.Vertex.Spirv, "vertex");
            fragmentModule = CreateShaderModule(session, shader.Fragment.Spirv, "fragment");
            var pushConstants = shader.Vertex.Abi.PushConstants.Count > 0
                ? shader.Vertex.Abi.PushConstants
                : shader.Fragment.Abi.PushConstants;
            var ranges = pushConstants.Count > 0
                ? CreatePushRanges(pushConstants, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, out _)
                : Array.Empty<PushConstantRange>();
            var pushSize = GetPushConstantSize(shader.Vertex.Abi.PushConstants, shader.Fragment.Abi.PushConstants);
            fixed (DescriptorSetLayout* layoutPtr = layouts)
            fixed (PushConstantRange* pushPtr = ranges)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layouts.Length,
                    PSetLayouts = layoutPtr,
                    PushConstantRangeCount = (uint)ranges.Length,
                    PPushConstantRanges = pushPtr
                };
                Ensure(session.Api.CreatePipelineLayout(session.Device, layoutInfo, null, out pipelineLayout), "CreatePipelineLayout(graph raster)");
            }

            var vertexEntryName = SpirvEntryPointReader.ReadGraphicsEntryPoint(shader.Vertex.Spirv, true);
            var fragmentEntryName = SpirvEntryPointReader.ReadGraphicsEntryPoint(shader.Fragment.Spirv, false);
            if (!string.Equals(vertexEntryName, shader.Vertex.EntryPoint, StringComparison.Ordinal) ||
                !string.Equals(fragmentEntryName, shader.Fragment.EntryPoint, StringComparison.Ordinal))
            {
                throw new ArgumentException("Graphics artifact entry points do not match SPIR-V.", nameof(description));
            }

            var vertexEntryPoint = Encoding.UTF8.GetBytes(vertexEntryName + "\0");
            var fragmentEntryPoint = Encoding.UTF8.GetBytes(fragmentEntryName + "\0");
            fixed (byte* vertexName = vertexEntryPoint)
            fixed (byte* fragmentName = fragmentEntryPoint)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexModule, PName = vertexName };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentModule, PName = fragmentName };
                var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = ToTopology(description.Topology) };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var rasterization = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = ToCullMode(description.CullMode), FrontFace = description.FrontFace == RasterFrontFace.Clockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise, LineWidth = 1f };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var blend = new PipelineColorBlendAttachmentState { BlendEnable = description.BlendMode != RenderBlendMode.Opaque, SrcColorBlendFactor = description.BlendMode == RenderBlendMode.PremultipliedAlpha ? BlendFactor.One : BlendFactor.SrcAlpha, DstColorBlendFactor = description.BlendMode == RenderBlendMode.Additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
                var colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
                var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
                var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = description.DepthTest, DepthWriteEnable = description.DepthWrite, DepthCompareOp = CompareOp.LessOrEqual };
                var pipelineInfo = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly, PViewportState = &viewportState, PRasterizationState = &rasterization, PMultisampleState = &multisample, PDepthStencilState = &depth, PColorBlendState = &colorBlend, PDynamicState = &dynamic, Layout = pipelineLayout, RenderPass = session.GraphRenderPass, Subpass = 0 };
                var outputs = stackalloc Pipeline[1];
                Ensure(session.Api.CreateGraphicsPipelines(session.Device, default, 1, &pipelineInfo, null, outputs), "CreateGraphicsPipelines(graph)");
                pipeline = outputs[0];
            }

            session.Api.DestroyShaderModule(session.Device, vertexModule, null);
            session.Api.DestroyShaderModule(session.Device, fragmentModule, null);
            vertexModule = default;
            fragmentModule = default;
            return new VulkanGraphPipeline(pipeline, pipelineLayout, PipelineBindPoint.Graphics, layouts, pool, sets, bindings, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, pushSize);
        }
        catch
        {
            if (vertexModule.Handle != default) session.Api.DestroyShaderModule(session.Device, vertexModule, null);
            if (fragmentModule.Handle != default) session.Api.DestroyShaderModule(session.Device, fragmentModule, null);
            if (pipeline.Handle != default) session.Api.DestroyPipeline(session.Device, pipeline, null);
            if (pipelineLayout.Handle != default) session.Api.DestroyPipelineLayout(session.Device, pipelineLayout, null);
            DestroyDescriptorState(session, layouts, pool);
            throw;
        }
    }

    internal void Dispose(VulkanWindowSession session)
    {
        if (_disposed) return;
        _disposed = true;
        var api = session.Api;
        if (Pipeline.Handle != default) api.DestroyPipeline(session.Device, Pipeline, null);
        if (Layout.Handle != default) api.DestroyPipelineLayout(session.Device, Layout, null);
        DestroyDescriptorState(session, _descriptorSetLayouts, _descriptorPool);
    }

    private static GraphBinding[] BuildBindings(IReadOnlyList<ShaderResourceBinding> resources, ShaderStageMask stages, out int maxSet)
    {
        maxSet = -1;
        var result = new List<GraphBinding>(resources.Count);
        for (var i = 0; i < resources.Count; i++)
        {
            var resource = resources[i];
            if ((resource.Stages & stages) == 0) continue;
            if (resource.DescriptorCount != 1)
            {
                throw new ArgumentException("RenderGraph command contexts currently bind one descriptor per shader binding.");
            }
            maxSet = Math.Max(maxSet, checked((int)resource.Binding.Set));
            var binding = new GraphBinding(new RenderShaderBinding(resource.Binding.Set, resource.Binding.Binding), ToDescriptorType(resource.Kind), ToStageFlags(resource.Stages));
            var existing = result.FindIndex(item => item.Binding == binding.Binding);
            if (existing >= 0)
            {
                if (result[existing].DescriptorType != binding.DescriptorType) throw new ArgumentException($"Shader descriptor {binding.Binding.Set}/{binding.Binding.Binding} has incompatible kinds.");
                result[existing] = new GraphBinding(binding.Binding, binding.DescriptorType, result[existing].StageFlags | binding.StageFlags);
            }
            else result.Add(binding);
        }
        return result.ToArray();
    }

    private static List<ShaderResourceBinding> MergeResources(IReadOnlyList<ShaderResourceBinding> left, IReadOnlyList<ShaderResourceBinding> right)
    {
        var result = new List<ShaderResourceBinding>(left.Count + right.Count);
        for (var i = 0; i < left.Count; i++) result.Add(left[i]);
        for (var i = 0; i < right.Count; i++)
        {
            var existing = result.FindIndex(item => item.Binding == right[i].Binding);
            if (existing < 0) result.Add(right[i]);
            else if (result[existing].Kind != right[i].Kind || result[existing].DescriptorCount != right[i].DescriptorCount) throw new ArgumentException($"Shader descriptor {right[i].Binding.Set}/{right[i].Binding.Binding} differs between stages.");
        }
        return result;
    }

    private static void CreateDescriptorState(VulkanWindowSession session, GraphBinding[] bindings, int maxSet, out DescriptorSetLayout[] layouts, out DescriptorPool pool, out DescriptorSet[] sets)
    {
        var setCount = maxSet + 1;
        if (setCount > session.MaxBoundDescriptorSets) throw new InvalidOperationException($"Shader uses {setCount} descriptor sets, device limit is {session.MaxBoundDescriptorSets}.");
        layouts = new DescriptorSetLayout[setCount];
        sets = new DescriptorSet[setCount];
        pool = default;
        try
        {
            for (var set = 0; set < setCount; set++)
            {
                var setBindings = bindings.Where(binding => binding.Binding.Set == (uint)set).Select(binding => new DescriptorSetLayoutBinding { Binding = binding.Binding.Binding, DescriptorType = binding.DescriptorType, DescriptorCount = 1, StageFlags = binding.StageFlags }).ToArray();
                fixed (DescriptorSetLayoutBinding* bindingPtr = setBindings)
                {
                    var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)setBindings.Length, PBindings = bindingPtr };
                    Ensure(session.Api.CreateDescriptorSetLayout(session.Device, layoutInfo, null, out layouts[set]), "CreateDescriptorSetLayout(graph)");
                }
            }

            var poolSizes = bindings.GroupBy(binding => binding.DescriptorType).Select(group => new DescriptorPoolSize { Type = group.Key, DescriptorCount = (uint)group.Count() }).ToArray();
            if (poolSizes.Length > 0)
            {
                fixed (DescriptorPoolSize* poolPtr = poolSizes)
                {
                    var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setCount, PoolSizeCount = (uint)poolSizes.Length, PPoolSizes = poolPtr };
                    Ensure(session.Api.CreateDescriptorPool(session.Device, poolInfo, null, out pool), "CreateDescriptorPool(graph)");
                }
                for (var set = 0; set < setCount; set++)
                {
                    var layout = layouts[set];
                    var allocationInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
                    Ensure(session.Api.AllocateDescriptorSets(session.Device, allocationInfo, out sets[set]), "AllocateDescriptorSets(graph)");
                }
            }
        }
        catch
        {
            DestroyDescriptorState(session, layouts, pool);
            layouts = Array.Empty<DescriptorSetLayout>();
            sets = Array.Empty<DescriptorSet>();
            pool = default;
            throw;
        }
    }

    private static void DestroyDescriptorState(VulkanWindowSession session, DescriptorSetLayout[] layouts, DescriptorPool pool)
    {
        if (pool.Handle != default) session.Api.DestroyDescriptorPool(session.Device, pool, null);
        for (var i = layouts.Length - 1; i >= 0; i--) if (layouts[i].Handle != default) session.Api.DestroyDescriptorSetLayout(session.Device, layouts[i], null);
    }

    private static ShaderModule CreateShaderModule(VulkanWindowSession session, ReadOnlySpan<byte> spirv, string stage)
    {
        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        fixed (uint* code = words)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)(words.Length * sizeof(uint)), PCode = code };
            VulkanGraphPipeline.Ensure(session.Api.CreateShaderModule(session.Device, info, null, out var module), $"CreateShaderModule(graph {stage})");
            return module;
        }
    }

    private static PushConstantRange[] CreatePushRanges(IReadOnlyList<ShaderPushConstantRange> ranges, ShaderStageFlags stageFlags, out uint size)
    {
        size = GetPushConstantSize(ranges);
        return ranges.Select(range => new PushConstantRange { StageFlags = stageFlags, Offset = range.Offset, Size = range.Size }).ToArray();
    }

    private static uint GetPushConstantSize(IReadOnlyList<ShaderPushConstantRange> ranges) => ranges.Count == 0 ? 0 : ranges.Max(range => checked(range.Offset + range.Size));
    private static uint GetPushConstantSize(IReadOnlyList<ShaderPushConstantRange> vertex, IReadOnlyList<ShaderPushConstantRange> fragment) => Math.Max(GetPushConstantSize(vertex), GetPushConstantSize(fragment));
    private static DescriptorType ToDescriptorType(ShaderResourceKind kind) => kind switch { ShaderResourceKind.StorageBuffer => DescriptorType.StorageBuffer, ShaderResourceKind.UniformBuffer => DescriptorType.UniformBuffer, ShaderResourceKind.SampledTexture or ShaderResourceKind.CombinedTextureSampler => DescriptorType.CombinedImageSampler, _ => throw new ArgumentException("The current graph command contexts support storage/uniform buffers and sampled combined image samplers only.") };
    private static void ValidateArtifact(IShaderArtifact artifact, bool compute)
    {
        if (string.IsNullOrWhiteSpace(artifact.EntryPoint) ||
            (compute ? artifact.Stage != ShaderStage.Compute : artifact.Stage == ShaderStage.Unknown))
        {
            throw new ArgumentException("The shader artifact entry point or stage is not valid for the graph pipeline.", nameof(artifact));
        }
    }
    private static ShaderStageFlags ToStageFlags(ShaderStageMask stages) { var result = ShaderStageFlags.None; if (stages.HasFlag(ShaderStageMask.Vertex)) result |= ShaderStageFlags.VertexBit; if (stages.HasFlag(ShaderStageMask.Fragment)) result |= ShaderStageFlags.FragmentBit; if (stages.HasFlag(ShaderStageMask.Compute)) result |= ShaderStageFlags.ComputeBit; return result; }
    private static Silk.NET.Vulkan.PrimitiveTopology ToTopology(Delta.Render.RenderGraph.PrimitiveTopology topology) => topology switch { Delta.Render.RenderGraph.PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, Delta.Render.RenderGraph.PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList, Delta.Render.RenderGraph.PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList, _ => Silk.NET.Vulkan.PrimitiveTopology.TriangleList };
    private static CullModeFlags ToCullMode(RasterCullMode mode) => mode switch { RasterCullMode.Front => CullModeFlags.FrontBit, RasterCullMode.Back => CullModeFlags.BackBit, _ => CullModeFlags.None };
    private static void Ensure(Result result, string operation) { if (result != Result.Success) throw new InvalidOperationException($"{operation} failed: {result}"); }
}

internal sealed unsafe class VulkanRasterCommandContext : IRasterCommandContext
{
    private readonly VulkanRenderGraph _graph;
    private readonly VulkanGraphPipeline _pipeline;
    private readonly VulkanGraphBindings _bindings;

    internal VulkanRasterCommandContext(VulkanRenderGraph graph, VulkanGraphPipeline? pipeline)
    {
        _graph = graph;
        _pipeline = pipeline ?? throw new InvalidOperationException("Raster pass has no pipeline.");
        _bindings = new VulkanGraphBindings(_pipeline);
        _graph.Session.Api.CmdBindPipeline(_graph.Session.CommandBuffer, PipelineBindPoint.Graphics, _pipeline.Pipeline);
    }

    public void BindBuffer(RenderShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0) => _bindings.BindBuffer(binding, _graph.ResolveBuffer(buffer), offset, sizeInBytes);
    public void BindTexture(RenderShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler) => _bindings.BindTexture(binding, _graph.ResolveTexture(texture), _graph.ResolveSampler(sampler, _graph.ResolveTexture(texture)));
    public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => _bindings.PushConstants(_graph.Session, data, offset);
    public void SetViewport(in RenderViewport viewport) { if (!viewport.IsValid) throw new ArgumentException("Invalid viewport.", nameof(viewport)); var value = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth); _graph.Session.Api.CmdSetViewport(_graph.Session.CommandBuffer, 0, 1, &value); }
    public void SetScissor(in PixelRect scissor) { if (scissor.IsEmpty) throw new ArgumentException("Empty scissor.", nameof(scissor)); var value = new Rect2D { Offset = new Offset2D(scissor.X, scissor.Y), Extent = new Extent2D((uint)scissor.Width, (uint)scissor.Height) }; _graph.Session.Api.CmdSetScissor(_graph.Session.CommandBuffer, 0, 1, &value); }
    public void BindVertexBuffer(uint binding, RenderGraphBufferHandle buffer, ulong offset = 0) { var resource = _graph.ResolveBuffer(buffer); var value = resource.BufferAllocation.Buffer; _graph.Session.Api.CmdBindVertexBuffers(_graph.Session.CommandBuffer, binding, 1, &value, &offset); }
    public void BindIndexBuffer(RenderGraphBufferHandle buffer, IndexElementFormat format, ulong offset = 0) => _graph.Session.Api.CmdBindIndexBuffer(_graph.Session.CommandBuffer, _graph.ResolveBuffer(buffer).BufferAllocation.Buffer, offset, format == IndexElementFormat.UnsignedShort ? IndexType.Uint16 : IndexType.Uint32);
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) { _bindings.Bind(_graph.Session); _graph.Session.Api.CmdDraw(_graph.Session.CommandBuffer, vertexCount, instanceCount, firstVertex, firstInstance); }
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0) { _bindings.Bind(_graph.Session); _graph.Session.Api.CmdDrawIndexed(_graph.Session.CommandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance); }
}

internal sealed unsafe class VulkanComputeCommandContext : IComputeCommandContext
{
    private readonly VulkanRenderGraph _graph;
    private readonly VulkanGraphPipeline _pipeline;
    private readonly VulkanGraphBindings _bindings;
    internal VulkanComputeCommandContext(VulkanRenderGraph graph, VulkanGraphPipeline? pipeline) { _graph = graph; _pipeline = pipeline ?? throw new InvalidOperationException("Compute pass has no pipeline."); _bindings = new VulkanGraphBindings(_pipeline); _graph.Session.Api.CmdBindPipeline(_graph.Session.CommandBuffer, PipelineBindPoint.Compute, _pipeline.Pipeline); }
    public void BindBuffer(RenderShaderBinding binding, RenderGraphBufferHandle buffer, ulong offset = 0, ulong sizeInBytes = 0) => _bindings.BindBuffer(binding, _graph.ResolveBuffer(buffer), offset, sizeInBytes);
    public void BindTexture(RenderShaderBinding binding, RenderGraphTextureHandle texture, RenderSamplerHandle sampler) => _bindings.BindTexture(binding, _graph.ResolveTexture(texture), _graph.ResolveSampler(sampler, _graph.ResolveTexture(texture)));
    public void PushConstants(ReadOnlySpan<byte> data, uint offset = 0) => _bindings.PushConstants(_graph.Session, data, offset);
    public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1) { if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0) return; _bindings.Bind(_graph.Session); _graph.Session.Api.CmdDispatch(_graph.Session.CommandBuffer, groupCountX, groupCountY, groupCountZ); }
}

internal sealed unsafe class VulkanTransferCommandContext : ITransferCommandContext
{
    private readonly VulkanRenderGraph _graph;
    internal VulkanTransferCommandContext(VulkanRenderGraph graph) => _graph = graph;
    public void CopyBuffer(RenderGraphBufferHandle source, RenderGraphBufferHandle destination, ulong sizeInBytes, ulong sourceOffset = 0, ulong destinationOffset = 0)
    {
        var sourceResource = _graph.ResolveBuffer(source);
        var destinationResource = _graph.ResolveBuffer(destination);
        if (sizeInBytes == 0 || sourceOffset > sourceResource.BufferDescription.SizeInBytes ||
            sizeInBytes > sourceResource.BufferDescription.SizeInBytes - sourceOffset ||
            destinationOffset > destinationResource.BufferDescription.SizeInBytes ||
            sizeInBytes > destinationResource.BufferDescription.SizeInBytes - destinationOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "The graph buffer copy exceeds a declared resource.");
        }

        var copy = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = sizeInBytes };
        _graph.Session.Api.CmdCopyBuffer(
            _graph.Session.CommandBuffer,
            sourceResource.BufferAllocation.Buffer,
            destinationResource.BufferAllocation.Buffer,
            new[] { copy });
    }

    public void UploadBuffer(RenderGraphBufferHandle destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var resource = _graph.ResolveBuffer(destination);
        if (destinationOffset > resource.BufferDescription.SizeInBytes ||
            (ulong)data.Length > resource.BufferDescription.SizeInBytes - destinationOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "The graph buffer upload exceeds the declared resource.");
        }

        var staging = _graph.EnsureStaging((ulong)data.Length);
        _graph.WriteStaging(data);
        _graph.AddStagingReadBarrier();
        var copy = new BufferCopy { SrcOffset = 0, DstOffset = destinationOffset, Size = (ulong)data.Length };
        _graph.Session.Api.CmdCopyBuffer(
            _graph.Session.CommandBuffer,
            staging.Buffer,
            resource.BufferAllocation.Buffer,
            new[] { copy });
    }

    public void UploadTexture(RenderGraphTextureHandle destination, in PixelRect destinationRegion, ReadOnlySpan<byte> data, uint sourceRowPitch)
    {
        var resource = _graph.ResolveTexture(destination);
        var image = resource.Image ?? throw new InvalidOperationException("The destination texture has no Vulkan image.");
        if (destinationRegion.IsEmpty || destinationRegion.X < 0 || destinationRegion.Y < 0 ||
            (uint)destinationRegion.X + (uint)destinationRegion.Width > image.Width ||
            (uint)destinationRegion.Y + (uint)destinationRegion.Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationRegion), "The graph texture upload region is outside the declared texture.");
        }

        var bytesPerPixel = BytesPerPixel(image.Format);
        var minimumRowPitch = checked((uint)destinationRegion.Width * bytesPerPixel);
        if (sourceRowPitch < minimumRowPitch || sourceRowPitch % bytesPerPixel != 0 ||
            (ulong)sourceRowPitch * (uint)destinationRegion.Height > (ulong)data.Length)
        {
            throw new ArgumentException("The texture upload row pitch or data length is invalid.", nameof(data));
        }

        var staging = _graph.EnsureStaging(checked((ulong)sourceRowPitch * (uint)destinationRegion.Height));
        _graph.WriteStaging(data[..checked((int)((ulong)sourceRowPitch * (uint)destinationRegion.Height))]);
        _graph.AddStagingReadBarrier();
        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = sourceRowPitch / bytesPerPixel,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = image.Aspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(destinationRegion.X, destinationRegion.Y, 0),
            ImageExtent = new Extent3D((uint)destinationRegion.Width, (uint)destinationRegion.Height, 1)
        };
        _graph.Session.Api.CmdCopyBufferToImage(
            _graph.Session.CommandBuffer,
            staging.Buffer,
            image.Image,
            ImageLayout.TransferDstOptimal,
            new[] { copy });
    }

    private static uint BytesPerPixel(RenderTextureFormat format) => format switch
    {
        RenderTextureFormat.R8Unorm => 1,
        RenderTextureFormat.Rgba8Unorm or RenderTextureFormat.Rgba8Srgb or RenderTextureFormat.Bgra8Unorm or RenderTextureFormat.Bgra8Srgb => 4,
        RenderTextureFormat.Rgba16Float => 8,
        RenderTextureFormat.D32Float or RenderTextureFormat.D24UnormS8UInt => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The texture format has no supported upload layout.")
    };
}

internal sealed unsafe class VulkanGraphBindings
{
    private readonly VulkanGraphPipeline _pipeline;
    private readonly GraphBindingValue[] _values;
    internal VulkanGraphBindings(VulkanGraphPipeline pipeline) { _pipeline = pipeline; _values = new GraphBindingValue[pipeline.Bindings.Length]; }
    internal void BindBuffer(RenderShaderBinding binding, VulkanRenderGraph.GraphResource resource, ulong offset, ulong size) { var index = Find(binding); if (resource is null || !resource.IsBuffer || offset > resource.BufferAllocation.AllocationSize) throw new ArgumentException("The resource is not a graph buffer or the offset is invalid.", nameof(resource)); _values[index] = GraphBindingValue.FromBuffer(resource.BufferAllocation, offset, size == 0 ? resource.BufferAllocation.AllocationSize - offset : size); }
    internal void BindTexture(RenderShaderBinding binding, VulkanRenderGraph.GraphResource resource, Sampler sampler) { var index = Find(binding); if (sampler.Handle == default || resource is null || !resource.IsTexture || resource.Image is null || resource.Image.View.Handle == default) throw new ArgumentException("The texture or sampler handle is invalid for this graph."); _values[index] = GraphBindingValue.FromTexture(resource.Image, sampler); }
    internal void PushConstants(VulkanWindowSession session, ReadOnlySpan<byte> data, uint offset) { if (data.Length == 0 || offset + (uint)data.Length > _pipeline.PushConstantSize) throw new ArgumentException("Push constants exceed the pipeline ABI.", nameof(data)); fixed (byte* pointer = data) session.Api.CmdPushConstants(session.CommandBuffer, _pipeline.Layout, _pipeline.StageFlags, offset, (uint)data.Length, pointer); }
    internal void Bind(VulkanWindowSession session) { if (_pipeline.Bindings.Length == 0) return; var writes = stackalloc WriteDescriptorSet[_values.Length]; var bufferInfos = stackalloc DescriptorBufferInfo[_values.Length]; var imageInfos = stackalloc DescriptorImageInfo[_values.Length]; for (var i = 0; i < _values.Length; i++) { var value = _values[i]; if (!value.IsSet) throw new InvalidOperationException($"Shader binding {_pipeline.Bindings[i].Binding.Set}/{_pipeline.Bindings[i].Binding.Binding} was not supplied."); var set = _pipeline.DescriptorSets[(int)_pipeline.Bindings[i].Binding.Set]; writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = _pipeline.Bindings[i].Binding.Binding, DescriptorCount = 1, DescriptorType = _pipeline.Bindings[i].DescriptorType }; if (value.IsBuffer) { bufferInfos[i] = new DescriptorBufferInfo { Buffer = value.Buffer.Buffer, Offset = value.Offset, Range = value.Size }; writes[i].PBufferInfo = &bufferInfos[i]; } else { var image = value.Image ?? throw new InvalidOperationException("A graph texture descriptor lost its owned image."); imageInfos[i] = new DescriptorImageInfo { Sampler = value.Sampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal }; writes[i].PImageInfo = &imageInfos[i]; } } session.Api.UpdateDescriptorSets(session.Device, (uint)_values.Length, writes, 0, null); fixed (DescriptorSet* sets = _pipeline.DescriptorSets) session.Api.CmdBindDescriptorSets(session.CommandBuffer, _pipeline.BindPoint, _pipeline.Layout, 0, (uint)_pipeline.DescriptorSets.Length, sets, 0, null); }
    private int Find(RenderShaderBinding binding) { for (var i = 0; i < _pipeline.Bindings.Length; i++) if (_pipeline.Bindings[i].Binding.Set == binding.Set && _pipeline.Bindings[i].Binding.Binding == binding.Binding) return i; throw new ArgumentException($"Shader binding {binding.Set}/{binding.Binding} is not declared by the pipeline.", nameof(binding)); }
    private readonly record struct GraphBindingValue(bool IsSet, bool IsBuffer, BufferAllocation Buffer, VulkanRenderGraph.VulkanGraphImage? Image, Sampler Sampler, ulong Offset, ulong Size) { internal static GraphBindingValue FromBuffer(BufferAllocation b, ulong o, ulong s) => new(true, true, b, null, default, o, s); internal static GraphBindingValue FromTexture(VulkanRenderGraph.VulkanGraphImage i, Sampler sampler) => new(true, false, default, i, sampler, 0, 0); }
}
