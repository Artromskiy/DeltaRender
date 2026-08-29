using System.Diagnostics.CodeAnalysis;
using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanResourceRegistry
{
    private readonly ResourceTable<PersistentBuffer> _buffers = new();
    private readonly ResourceTable<PersistentTexture> _textures = new();
    private readonly ResourceTable<PersistentSampler> _samplers = new();

    internal IEnumerable<PersistentBuffer> Buffers => _buffers.Values;
    internal IEnumerable<PersistentTexture> Textures => _textures.Values;
    internal IEnumerable<PersistentSampler> Samplers => _samplers.Values;

    internal void AddBuffer(ulong value, PersistentBuffer buffer) => _buffers.Add(value, buffer);
    internal void AddTexture(ulong value, PersistentTexture texture) => _textures.Add(value, texture);
    internal void AddSampler(ulong value, PersistentSampler sampler) => _samplers.Add(value, sampler);

    internal bool TryGetBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
        => _buffers.TryGet(handle.IsValid, handle.Value, handle.Generation, out buffer);

    internal bool TryGetTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
        => _textures.TryGet(handle.IsValid, handle.Value, handle.Generation, out texture);

    internal bool TryGetSampler(RenderSamplerHandle handle, [NotNullWhen(true)] out PersistentSampler? sampler)
        => _samplers.TryGet(handle.IsValid, handle.Value, handle.Generation, out sampler);

    internal bool TryRemoveBuffer(RenderBufferHandle handle, [NotNullWhen(true)] out PersistentBuffer? buffer)
        => _buffers.TryRemove(handle.IsValid, handle.Value, handle.Generation, out buffer);

    internal bool TryRemoveTexture(RenderTextureHandle handle, [NotNullWhen(true)] out PersistentTexture? texture)
        => _textures.TryRemove(handle.IsValid, handle.Value, handle.Generation, out texture);

    internal bool TryRemoveSampler(RenderSamplerHandle handle, [NotNullWhen(true)] out PersistentSampler? sampler)
        => _samplers.TryRemove(handle.IsValid, handle.Value, handle.Generation, out sampler);

    internal void Clear()
    {
        _buffers.Clear();
        _textures.Clear();
        _samplers.Clear();
    }

    private sealed class ResourceTable<T> where T : class, IVulkanResourceGeneration
    {
        private readonly Dictionary<ulong, T> _values = new();

        internal IEnumerable<T> Values => _values.Values;

        internal void Add(ulong value, T resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            _values.Add(value, resource);
        }

        internal bool TryGet(bool isValid, ulong value, uint generation, [NotNullWhen(true)] out T? resource)
        {
            resource = null;
            if (!isValid || !_values.TryGetValue(value, out var candidate) || candidate.Generation != generation)
            {
                return false;
            }

            resource = candidate;
            return true;
        }

        internal bool TryRemove(bool isValid, ulong value, uint generation, [NotNullWhen(true)] out T? resource)
        {
            if (!TryGet(isValid, value, generation, out resource))
            {
                return false;
            }

            _values.Remove(value);
            return true;
        }

        internal void Clear() => _values.Clear();
    }
}

internal interface IVulkanResourceGeneration
{
    uint Generation { get; }
}
