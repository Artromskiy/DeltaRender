using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanGraphDependencyPlanner
{
    private List<int>[] _edges = [];
    private int[] _indegree = [];
    private int[] _lastWriter = [];
    private List<int>[] _readers = [];
    private readonly List<int> _readyTransfers = [];
    private readonly List<int> _readyOther = [];
    private int _readyTransferIndex;
    private int _readyOtherIndex;

    internal int Compile(IReadOnlyList<VulkanRenderGraph.GraphPass> passes, int resourceCount, Span<int> order)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        if (order.Length < passes.Count)
        {
            throw new ArgumentException("The destination order span is too small for the graph.", nameof(order));
        }

        if (passes.Count == 0)
        {
            return 0;
        }

        if (passes.Count == 1 && passes[0].Uses.Count == 0)
        {
            order[0] = 0;
            return 1;
        }

        EnsureStorage(passes.Count, resourceCount);
        Array.Clear(_indegree, 0, passes.Count);
        Array.Fill(_lastWriter, -1, 0, resourceCount);

        for (var passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            foreach (var use in passes[passIndex].Uses)
            {
                var resource = use.Resource.Index;
                if (_lastWriter.RefAt(resource) >= 0)
                {
                    AddEdge(_lastWriter.RefAt(resource), passIndex);
                }

                if (use.Access.HasFlag(RenderResourceAccess.Write))
                {
                    foreach (var reader in _readers.RefAt(resource))
                    {
                        AddEdge(reader, passIndex);
                    }

                    _readers.RefAt(resource).Clear();
                    _lastWriter.RefAt(resource) = passIndex;
                }
                else if (use.Access.HasFlag(RenderResourceAccess.Read))
                {
                    _readers.RefAt(resource).Add(passIndex);
                }
            }
        }

        for (var index = 0; index < passes.Count; index++)
        {
            if (_indegree.RefAt(index) == 0)
            {
                AddReady(passes[index].Kind, index);
            }
        }

        var count = 0;
        while (_readyTransferIndex < _readyTransfers.Count || _readyOtherIndex < _readyOther.Count)
        {
            int current;
            if (_readyTransferIndex < _readyTransfers.Count)
            {
                current = _readyTransfers.RefAt(_readyTransferIndex++);
            }
            else
            {
                current = _readyOther.RefAt(_readyOtherIndex++);
            }

            order[count++] = current;
            foreach (var next in _edges.RefAt(current))
            {
                if (--_indegree.RefAt(next) == 0)
                {
                    AddReady(passes[next].Kind, next);
                }
            }
        }

        if (count != passes.Count)
        {
            throw new InvalidOperationException("Render graph contains a dependency cycle.");
        }

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
                _edges.RefAt(index) = new List<int>();
            }
        }

        for (var index = 0; index < passCount; index++)
        {
            _edges.RefAt(index).Clear();
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
                _readers.RefAt(index) = new List<int>();
            }
        }

        for (var index = 0; index < resourceCount; index++)
        {
            _readers.RefAt(index).Clear();
        }

        _readyTransfers.Clear();
        _readyOther.Clear();
        _readyTransferIndex = 0;
        _readyOtherIndex = 0;
    }

    private void AddReady(VulkanRenderGraph.PassKind kind, int passIndex)
    {
        if (kind == VulkanRenderGraph.PassKind.Transfer)
        {
            _readyTransfers.Add(passIndex);
        }
        else
        {
            _readyOther.Add(passIndex);
        }
    }

    private static int GrowCapacity(int current, int required)
    {
        var capacity = current == 0 ? 8 : checked(current * 2);
        return Math.Max(capacity, required);
    }

    private void AddEdge(int from, int to)
    {
        if (from != to && !_edges.RefAt(from).Contains(to))
        {
            _edges.RefAt(from).Add(to);
            _indegree.RefAt(to)++;
        }
    }

}
