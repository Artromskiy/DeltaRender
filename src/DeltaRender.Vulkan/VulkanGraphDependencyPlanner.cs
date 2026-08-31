using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanGraphDependencyPlanner
{
    private List<int>[] _edges = [];
    private int[] _indegree = [];
    private int[] _lastWriter = [];
    private List<int>[] _readers = [];
    private readonly List<int> _ready = [];
    private PassSignature[] _cachedPasses = [];
    private UseSignature[] _cachedUses = [];
    private int[] _cachedOrder = [];
    private int _cachedPassCount;
    private int _cachedResourceCount;
    private int _cachedUseCount;
    private bool _hasCachedOrder;

    internal int Compile(IReadOnlyList<VulkanRenderGraph.GraphPass> passes, int resourceCount, Span<int> order)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        if (order.Length < passes.Count)
        {
            throw new ArgumentException("The destination order span is too small for the graph.", nameof(order));
        }

        if (TryReuseOrder(passes, resourceCount, order))
        {
            return passes.Count;
        }

        EnsureStorage(passes.Count, resourceCount);
        Array.Clear(_indegree, 0, passes.Count);
        Array.Fill(_lastWriter, -1, 0, resourceCount);

        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            foreach (var use in passes[passIndex].Uses)
            {
                var resource = use.Resource.Index;
                if (_lastWriter[resource] >= 0)
                {
                    AddEdge(_lastWriter[resource], passIndex);
                }

                if (use.Access.HasFlag(RenderResourceAccess.Write))
                {
                    foreach (var reader in _readers[resource])
                    {
                        AddEdge(reader, passIndex);
                    }

                    _readers[resource].Clear();
                    _lastWriter[resource] = passIndex;
                }
                else if (use.Access.HasFlag(RenderResourceAccess.Read))
                {
                    _readers[resource].Add(passIndex);
                }
            }
        }

        for (var index = 0; index < passes.Count; index++)
        {
            if (_indegree[index] == 0)
            {
                _ready.Add(index);
            }
        }

        var count = 0;
        while (_ready.Count != 0)
        {
            var readyIndex = 0;
            for (var index = 1; index < _ready.Count; index++)
            {
                if (passes[_ready[index]].Kind == VulkanRenderGraph.PassKind.Transfer)
                {
                    readyIndex = index;
                    break;
                }
            }

            var current = _ready[readyIndex];
            _ready.RemoveAt(readyIndex);
            order[count++] = current;
            foreach (var next in _edges[current])
            {
                if (--_indegree[next] == 0)
                {
                    _ready.Add(next);
                }
            }
        }

        if (count != passes.Count)
        {
            throw new InvalidOperationException("Render graph contains a dependency cycle.");
        }

        CaptureTopology(passes, resourceCount, order, count);
        return count;
    }

    internal int PassCapacity => _edges.Length;
    internal int ResourceCapacity => _lastWriter.Length;

    private void EnsureStorage(int passCount, int resourceCount)
    {
        if (_edges.Length < passCount)
        {
            var capacity = GrowCapacity(_edges.Length, passCount);
            _edges = new List<int>[capacity];
            for (var index = 0; index < capacity; index++)
            {
                _edges[index] = new List<int>();
            }
        }

        for (var index = 0; index < passCount; index++)
        {
            _edges[index].Clear();
        }

        if (_indegree.Length < passCount)
        {
            _indegree = new int[GrowCapacity(_indegree.Length, passCount)];
        }

        if (_lastWriter.Length < resourceCount)
        {
            _lastWriter = new int[GrowCapacity(_lastWriter.Length, resourceCount)];
        }

        if (_readers.Length < resourceCount)
        {
            var capacity = GrowCapacity(_readers.Length, resourceCount);
            _readers = new List<int>[capacity];
            for (var index = 0; index < capacity; index++)
            {
                _readers[index] = new List<int>();
            }
        }

        for (var index = 0; index < resourceCount; index++)
        {
            _readers[index].Clear();
        }

        _ready.Clear();
    }

    private static int GrowCapacity(int current, int required)
    {
        var capacity = current == 0 ? 8 : checked(current * 2);
        return Math.Max(capacity, required);
    }

    private bool TryReuseOrder(
        IReadOnlyList<VulkanRenderGraph.GraphPass> passes,
        int resourceCount,
        Span<int> order)
    {
        if (!_hasCachedOrder || _cachedPassCount != passes.Count || _cachedResourceCount != resourceCount)
        {
            return false;
        }

        var useIndex = 0;
        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            var pass = passes[passIndex];
            var signature = _cachedPasses[passIndex];
            if (signature.Kind != pass.Kind || signature.UseStart != useIndex || signature.UseCount != pass.Uses.Count)
            {
                return false;
            }

            foreach (var use in pass.Uses)
            {
                if (_cachedUses[useIndex] != new UseSignature(use.Resource.Index, use.Access, use.Stages))
                {
                    return false;
                }

                useIndex++;
            }
        }

        if (useIndex != _cachedUseCount)
        {
            return false;
        }

        _cachedOrder.AsSpan(0, passes.Count).CopyTo(order);
        return true;
    }

    private void CaptureTopology(
        IReadOnlyList<VulkanRenderGraph.GraphPass> passes,
        int resourceCount,
        Span<int> order,
        int orderCount)
    {
        var useCount = 0;
        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            useCount = checked(useCount + passes[passIndex].Uses.Count);
        }

        EnsureTopologyCapacity(passes.Count, useCount, orderCount);
        var useIndex = 0;
        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            var pass = passes[passIndex];
            _cachedPasses[passIndex] = new PassSignature(pass.Kind, useIndex, pass.Uses.Count);
            foreach (var use in pass.Uses)
            {
                _cachedUses[useIndex++] = new UseSignature(use.Resource.Index, use.Access, use.Stages);
            }
        }

        order[..orderCount].CopyTo(_cachedOrder);
        _cachedPassCount = passes.Count;
        _cachedResourceCount = resourceCount;
        _cachedUseCount = useCount;
        _hasCachedOrder = true;
    }

    private void EnsureTopologyCapacity(int passCount, int useCount, int orderCount)
    {
        if (_cachedPasses.Length < passCount)
        {
            _cachedPasses = new PassSignature[GrowCapacity(_cachedPasses.Length, passCount)];
        }

        if (_cachedUses.Length < useCount)
        {
            _cachedUses = new UseSignature[GrowCapacity(_cachedUses.Length, useCount)];
        }

        if (_cachedOrder.Length < orderCount)
        {
            _cachedOrder = new int[GrowCapacity(_cachedOrder.Length, orderCount)];
        }
    }

    private void AddEdge(int from, int to)
    {
        if (from != to && !_edges[from].Contains(to))
        {
            _edges[from].Add(to);
            _indegree[to]++;
        }
    }

    private readonly record struct PassSignature(VulkanRenderGraph.PassKind Kind, int UseStart, int UseCount);

    private readonly record struct UseSignature(
        int ResourceIndex,
        RenderResourceAccess Access,
        RenderPipelineStages Stages);
}
