using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Delta.Render.Vulkan;

internal sealed class VulkanTransientResourcePool<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Dictionary<TKey, List<TValue>> _free = [];
    private readonly HashSet<TValue> _inPool = [];

    internal int CreateCount { get; private set; }

    internal int ReuseCount { get; private set; }

    internal int ReturnCount { get; private set; }

    internal int FreeCount => _inPool.Count;

    internal TValue Acquire(TKey key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryTake(key, out var value))
        {
            return value;
        }

        var created = factory();
        RecordCreated();
        return created;
    }

    internal bool TryTake(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_free.TryGetValue(key, out var resources) && resources.Count > 0)
        {
            int last = resources.Count - 1;
            value = resources[last];
            resources.RemoveAt(last);
            _inPool.Remove(value);
            ReuseCount++;
            return true;
        }

        value = default;
        return false;
    }

    internal void RecordCreated() => CreateCount++;

    internal void Return(TKey key, TValue value)
    {
        if (!_inPool.Add(value))
        {
            throw new InvalidOperationException("A transient resource was returned to the pool more than once.");
        }

        if (!_free.TryGetValue(key, out var resources))
        {
            resources = [];
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

    internal void Clear()
    {
        _free.Clear();
        _inPool.Clear();
    }
}
