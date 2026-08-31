using System.Runtime.InteropServices;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanGraphBarrierPlanner(VulkanRenderGraph graph)
{
    private readonly List<BufferMemoryBarrier> _buffers = new();
    private readonly List<ImageMemoryBarrier> _images = new();

    internal void Emit(VulkanRenderGraph.GraphPass pass, VulkanRenderGraph.ResourceState[] states)
    {
        var sourceStages = PipelineStageFlags.None;
        var destinationStages = PipelineStageFlags.None;
        _buffers.Clear();
        _images.Clear();
        foreach (var use in pass.Uses)
        {
            var previous = states.RefAt(use.Resource.Index);
            var next = VulkanRenderGraph.ResourceState.For(use.Access, use.Stages);
            if (use.Resource.IsBuffer)
            {
                if (previous == next)
                {
                    continue;
                }

                var allocation = use.Resource.Buffer?.Allocation ?? throw new InvalidOperationException("The graph buffer is unavailable while planning a barrier.");
                _buffers.Add(new BufferMemoryBarrier { SType = StructureType.BufferMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = next.Access, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = allocation.Buffer, Offset = 0, Size = allocation.AllocationSize });
                sourceStages |= NormalizeStage(previous.Stages);
                destinationStages |= NormalizeStage(next.Stages);
            }
            else if (use.Resource.Image.Handle != default && (previous.Layout != next.Layout || previous.Access != next.Access))
            {
                _images.Add(new ImageMemoryBarrier { SType = StructureType.ImageMemoryBarrier, SrcAccessMask = previous.Access, DstAccessMask = next.Access, OldLayout = previous.Layout, NewLayout = next.Layout, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = use.Resource.Image, SubresourceRange = new ImageSubresourceRange { AspectMask = use.Resource.AspectMask, LevelCount = 1, LayerCount = 1 } });
                sourceStages |= NormalizeStage(previous.Stages);
                destinationStages |= NormalizeStage(next.Stages);
            }
        }

        if (_buffers.Count != 0 || _images.Count != 0)
        {
            graph.CommandWriter.PipelineBarrier(sourceStages, destinationStages, CollectionsMarshal.AsSpan(_buffers), CollectionsMarshal.AsSpan(_images));
        }
    }

    internal void EmitRasterSegmentEntry(int firstRasterPosition, VulkanRenderGraph.ResourceState[] states)
    {
        var firstPass = graph.OrderedPassAt(firstRasterPosition);
        for (var orderPosition = firstRasterPosition + 1; orderPosition < graph.OrderCount; orderPosition++)
        {
            var pass = graph.OrderedPassAt(orderPosition);
            if (pass.Kind != VulkanRenderGraph.PassKind.Raster)
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
                var next = VulkanRenderGraph.ResourceState.For(use.Access, use.Stages);
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
                graph.CommandWriter.PipelineBarrier(
                    NormalizeStage(previous.Stages),
                    NormalizeStage(next.Stages),
                    in barrier);
                states.RefAt(use.Resource.Index) = next;
            }
        }
    }

    internal static void UpdateStates(VulkanRenderGraph.GraphPass pass, VulkanRenderGraph.ResourceState[] states)
    {
        foreach (var use in pass.Uses)
        {
            states.RefAt(use.Resource.Index) = VulkanRenderGraph.ResourceState.For(use.Access, use.Stages);
        }
    }

    private static PipelineStageFlags NormalizeStage(PipelineStageFlags stages)
    {
        var stage = stages & ~PipelineStageFlags.TopOfPipeBit;
        return stage == PipelineStageFlags.None ? PipelineStageFlags.TopOfPipeBit : stage;
    }

    private static bool UsesResource(VulkanRenderGraph.GraphPass pass, VulkanRenderGraph.GraphResource resource)
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
}
