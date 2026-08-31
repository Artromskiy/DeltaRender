using System.Collections.Generic;

namespace Delta.Render.Vulkan;

internal sealed class VulkanPipelineCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Dictionary<TKey, TValue> _values;

    internal VulkanPipelineCache(IEqualityComparer<TKey>? comparer = null)
    {
        _values = new Dictionary<TKey, TValue>(comparer);
    }

    internal int Count => _values.Count;

    internal int CreateCount { get; private set; }

    internal int HitCount { get; private set; }

    internal IEnumerable<TValue> Values => _values.Values;

    internal TValue GetOrCreate(TKey key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_values.TryGetValue(key, out var value))
        {
            HitCount++;
            return value;
        }

        value = factory();
        _values.Add(key, value);
        CreateCount++;
        return value;
    }

    internal void Clear() => _values.Clear();
}
