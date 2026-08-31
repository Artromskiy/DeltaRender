using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed class PersistentBuffer : IVulkanResourceGeneration
{
    internal PersistentBuffer(
        BufferAllocation allocation,
        RenderBufferDescription description,
        uint generation)
    {
        Allocation = allocation;
        Description = description;
        Generation = generation;
    }

    internal BufferAllocation Allocation { get; }

    internal RenderBufferDescription Description { get; }

    internal uint Generation { get; }

    uint IVulkanResourceGeneration.Generation => Generation;
}

internal sealed record PersistentTexture(
    Image Image,
    DeviceMemory Memory,
    ImageView View,
    Format Format,
    Extent2D Extent,
    uint Generation) : IVulkanResourceGeneration
{
    internal RenderTextureUsage Usage { get; init; }
}

internal sealed record PersistentSampler(Sampler Sampler, uint Generation) : IVulkanResourceGeneration;
