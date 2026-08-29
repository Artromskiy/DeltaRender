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

internal sealed class VulkanTransientResourcePool<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Dictionary<TKey, List<TValue>> _free = new();
    private readonly HashSet<TValue> _inPool = new();

    internal int CreateCount { get; private set; }

    internal int ReuseCount { get; private set; }

    internal int ReturnCount { get; private set; }

    internal int FreeCount => _inPool.Count;

    internal TValue Acquire(TKey key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_free.TryGetValue(key, out var resources) && resources.Count > 0)
        {
            var last = resources.Count - 1;
            var value = resources[last];
            resources.RemoveAt(last);
            _inPool.Remove(value);
            ReuseCount++;
            return value;
        }

        var created = factory();
        CreateCount++;
        return created;
    }

    internal void Return(TKey key, TValue value)
    {
        if (!_inPool.Add(value))
        {
            throw new InvalidOperationException("A transient resource was returned to the pool more than once.");
        }

        if (!_free.TryGetValue(key, out var resources))
        {
            resources = new List<TValue>();
            _free.Add(key, resources);
        }

        resources.Add(value);
        ReturnCount++;
    }

    internal void Drain(Action<TValue> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        foreach (var resources in _free.Values)
        {
            foreach (var resource in resources)
            {
                release(resource);
            }
        }

        _free.Clear();
        _inPool.Clear();
    }
}
