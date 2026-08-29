using System.Diagnostics.CodeAnalysis;
using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanResourceRegistry
{
    private readonly Dictionary<ulong, PersistentBuffer> _buffers = new();
    private readonly Dictionary<ulong, PersistentTexture> _textures = new();
    private readonly Dictionary<ulong, PersistentSampler> _samplers = new();

    internal IEnumerable<PersistentBuffer> Buffers => _buffers.Values;
    internal IEnumerable<PersistentTexture> Textures => _textures.Values;
    internal IEnumerable<PersistentSampler> Samplers => _samplers.Values;

    internal void AddBuffer(ulong value, PersistentBuffer buffer) => _buffers.Add(value, buffer);
    internal void AddTexture(ulong value, PersistentTexture texture) => _textures.Add(value, texture);
    internal void AddSampler(ulong value, PersistentSampler sampler) => _samplers.Add(value, sampler);

    internal bool TryGetBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
        => TryGet(_buffers, handle.IsValid, handle.Value, handle.Generation, out buffer);

    internal bool TryGetTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
        => TryGet(_textures, handle.IsValid, handle.Value, handle.Generation, out texture);

    internal bool TryGetSampler(RenderSamplerHandle handle, [NotNullWhen(true)] out PersistentSampler? sampler)
        => TryGet(_samplers, handle.IsValid, handle.Value, handle.Generation, out sampler);

    internal bool TryRemoveBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
        => TryRemove(_buffers, handle.IsValid, handle.Value, handle.Generation, out buffer);

    internal bool TryRemoveTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
        => TryRemove(_textures, handle.IsValid, handle.Value, handle.Generation, out texture);

    internal bool TryRemoveSampler(RenderSamplerHandle handle, [NotNullWhen(true)] out PersistentSampler? sampler)
        => TryRemove(_samplers, handle.IsValid, handle.Value, handle.Generation, out sampler);

    internal void Clear()
    {
        _buffers.Clear();
        _textures.Clear();
        _samplers.Clear();
    }

    private static bool TryGet<T>(Dictionary<ulong, T> values, bool isValid, ulong value, uint generation, [NotNullWhen(true)] out T? resource)
        where T : class, IVulkanResourceGeneration
    {
        resource = null;
        if (!isValid || !values.TryGetValue(value, out var candidate) || candidate.Generation != generation)
        {
            return false;
        }

        resource = candidate;
        return true;
    }

    private static bool TryRemove<T>(Dictionary<ulong, T> values, bool isValid, ulong value, uint generation, [NotNullWhen(true)] out T? resource)
        where T : class, IVulkanResourceGeneration
    {
        if (!TryGet(values, isValid, value, generation, out resource))
        {
            return false;
        }

        values.Remove(value);
        return true;
    }
}

internal interface IVulkanResourceGeneration
{
    uint Generation { get; }
}
