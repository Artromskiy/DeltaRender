using System.Collections.Generic;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanGraphReadback(VulkanRenderGraph graph)
{
    private readonly List<ReadbackRequest> _requests = new();

    internal RenderGraphReadbackHandle AddBuffer(RenderGraphBufferHandle buffer, in BufferRange range)
    {
        graph.ThrowIfMutable();
        var resource = graph.ResolveBuffer(buffer);
        var allocation = resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable.");
        if (range.IsEmpty || range.Offset > allocation.AllocationSize || range.SizeInBytes > allocation.AllocationSize - range.Offset || range.SizeInBytes > int.MaxValue)
        {
            throw new ArgumentException("The readback range is outside the buffer.", nameof(range));
        }

        _requests.Add(new ReadbackRequest(resource, checked((int)range.SizeInBytes), range.Offset));
        return new RenderGraphReadbackHandle((uint)_requests.Count);
    }

    internal RenderGraphReadbackHandle AddTexture(RenderGraphTextureHandle texture, in PixelRect region)
    {
        graph.ThrowIfMutable();
        if (region.IsEmpty || region.X < 0 || region.Y < 0)
        {
            throw new ArgumentException("The texture readback region is invalid.", nameof(region));
        }

        var resource = graph.ResolveTexture(texture);
        if (graph.Session.IsWindowed)
        {
            throw new NotSupportedException("Windowed texture readback is not supported by the current graph session.");
        }

        var size = checked(region.Width * region.Height * 4);
        _requests.Add(new ReadbackRequest(resource, size, 0, region, true));
        return new RenderGraphReadbackHandle((uint)_requests.Count);
    }

    internal int Copy(RenderGraphReadbackHandle readback, Span<byte> destination)
    {
        if (!readback.IsValid || readback.Value > (uint)_requests.Count)
        {
            throw new ArgumentException("The readback handle is invalid.", nameof(readback));
        }

        var request = _requests[(int)readback.Value - 1];
        if (!request.Submitted)
        {
            throw new InvalidOperationException("The readback is not associated with a submitted graph.");
        }

        if (destination.Length < request.Size)
        {
            throw new ArgumentException($"The destination requires at least {request.Size} bytes.", nameof(destination));
        }

        var session = graph.Session;
        session.WaitForReadback();
        var staging = session.StagingBuffer;
        void* pointer = null;
        var mapped = session.Api.MapMemory(session.Device, staging.Memory, request.StagingOffset, (ulong)request.Size, 0, &pointer);
        if (mapped != Result.Success)
        {
            throw new InvalidOperationException($"MapMemory(readback) failed: {mapped}");
        }

        try
        {
            new ReadOnlySpan<byte>(pointer, request.Size).CopyTo(destination);
            if (!staging.MemoryProperties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                var range = new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = staging.Memory, Offset = 0, Size = (nuint)staging.AllocationSize };
                VulkanCall.Ensure(session.Api.InvalidateMappedMemoryRanges(session.Device, 1, &range), "InvalidateMappedMemoryRanges");
                new ReadOnlySpan<byte>(pointer, request.Size).CopyTo(destination);
            }
        }
        finally
        {
            session.Api.UnmapMemory(session.Device, staging.Memory);
        }

        return request.Size;
    }

    internal void Record(ReadOnlySpan<VulkanRenderGraph.ResourceState> states)
    {
        var session = graph.Session;
        var commandWriter = graph.CommandWriter;
        foreach (var request in _requests)
        {
            var offset = session.ReserveStaging(request.Size);
            var staging = session.StagingBuffer;
            request.StagingOffset = offset;
            request.Submitted = true;
            if (request.IsTexture)
            {
                var image = request.Resource.IsTarget ? session.GraphImage : request.Resource.Image;
                if (image.Handle == default)
                {
                    throw new InvalidOperationException("The texture readback image is unavailable.");
                }

                var previous = states.RefAt(request.Resource.Index);
                var imageBarrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = previous.Access,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = previous.Layout,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
                };
                var previousStages = previous.Stages;
                commandWriter.PipelineBarrier(previousStages == 0 ? PipelineStageFlags.TopOfPipeBit : previousStages, PipelineStageFlags.TransferBit, in imageBarrier);
                var copy = new BufferImageCopy
                {
                    BufferOffset = offset,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                    ImageOffset = new Offset3D(request.Region.X, request.Region.Y, 0),
                    ImageExtent = new Extent3D((uint)request.Region.Width, (uint)request.Region.Height, 1)
                };
                commandWriter.CopyImageToBuffer(image, ImageLayout.TransferSrcOptimal, staging.Buffer, copy);
            }
            else
            {
                var buffer = request.Resource.Buffer?.Allocation ?? throw new InvalidOperationException("The buffer readback resource is unavailable.");
                var previous = states.RefAt(request.Resource.Index);
                var sourceBarrier = new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = AccessFlags.TransferReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = buffer.Buffer, Offset = request.SourceOffset, Size = (ulong)request.Size };
                commandWriter.PipelineBarrier(previous.Stages == 0 ? PipelineStageFlags.TopOfPipeBit : previous.Stages, PipelineStageFlags.TransferBit, in sourceBarrier);
                var copy = new BufferCopy { SrcOffset = request.SourceOffset, DstOffset = offset, Size = (ulong)request.Size };
                session.Api.CmdCopyBuffer(session.CommandBuffer, buffer.Buffer, staging.Buffer, 1, &copy);
            }

            var hostBarrier = new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = staging.Buffer, Offset = offset, Size = (ulong)request.Size };
            commandWriter.PipelineBarrier(PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, in hostBarrier);
        }
    }

    internal void Reset() => _requests.Clear();

    private sealed class ReadbackRequest(VulkanRenderGraph.GraphResource resource, int size, ulong sourceOffset, PixelRect region = default, bool isTexture = false)
    {
        internal VulkanRenderGraph.GraphResource Resource = resource;
        internal int Size = size;
        internal ulong SourceOffset = sourceOffset;
        internal ulong StagingOffset;
        internal bool Submitted;
        internal PixelRect Region = region;
        internal bool IsTexture = isTexture;
    }
}
