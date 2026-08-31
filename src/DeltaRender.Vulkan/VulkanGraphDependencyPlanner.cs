using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanGraphDependencyPlanner
{
    private HashSet<int>[] _edges = [];
    private int[] _indegree = [];
    private int[] _lastWriter = [];
    private List<int>[] _readers = [];
    private readonly List<int> _ready = [];
    private int[] _order = [];

    internal int[] Compile(IReadOnlyList<VulkanRenderGraph.GraphPass> passes, int resourceCount)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);

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

        for (var index = 0; index < _indegree.Length; index++)
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
            _order[count++] = current;
            foreach (var next in _edges[current])
            {
                if (--_indegree[next] == 0)
                {
                    _ready.Add(next);
                }
            }
        }

        if (count != _order.Length)
        {
            throw new InvalidOperationException("Render graph contains a dependency cycle.");
        }

        return _order;
    }

    private void EnsureStorage(int passCount, int resourceCount)
    {
        if (_edges.Length != passCount)
        {
            _edges = new HashSet<int>[passCount];
            for (var index = 0; index < passCount; index++)
            {
                _edges[index] = new HashSet<int>();
            }
        }
        else
        {
            for (var index = 0; index < passCount; index++)
            {
                _edges[index].Clear();
            }
        }

        if (_indegree.Length != passCount)
        {
            _indegree = new int[passCount];
            _order = new int[passCount];
        }

        if (_lastWriter.Length != resourceCount)
        {
            _lastWriter = new int[resourceCount];
        }

        if (_readers.Length != resourceCount)
        {
            _readers = new List<int>[resourceCount];
            for (var index = 0; index < resourceCount; index++)
            {
                _readers[index] = new List<int>();
            }
        }
        else
        {
            for (var index = 0; index < resourceCount; index++)
            {
                _readers[index].Clear();
            }
        }

        _ready.Clear();
    }

    private void AddEdge(int from, int to)
    {
        if (from != to && _edges[from].Add(to))
        {
            _indegree[to]++;
        }
    }
}
