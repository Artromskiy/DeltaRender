using Delta.Render.RenderGraph;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

internal sealed class UiClipResolver(PixelExtent viewport, List<string> diagnostics)
{
    private PixelExtent _logicalViewport = viewport;
    private readonly List<string> _diagnostics = diagnostics;
    private UiClipRegion[] _clips = [];
    private PixelRect[] _resolvedClips = [];
    private int[] _resolvedEpochs = [];
    private int[] _clipMarks = [];
    private int _clipCount;
    private int _clipMarkEpoch;
    private int _frameEpoch;

    internal void SetFrame(UiClipRegion[] clips, int count, PixelExtent logicalViewport)
    {
        _clips = clips;
        _clipCount = count;
        _logicalViewport = logicalViewport;
        EnsureCapacity(ref _resolvedClips, count);
        EnsureCapacity(ref _resolvedEpochs, count);
        EnsureCapacity(ref _clipMarks, count);
        if (_frameEpoch == int.MaxValue)
        {
            Array.Clear(_resolvedEpochs, 0, count);
            _frameEpoch = 1;
        }
        else
        {
            _frameEpoch++;
        }
    }

    internal void Clear()
    {
        _clips = [];
        _clipCount = 0;
    }

    internal bool TryGetClip(UiClipId id, out PixelRect clip, int orderIndex)
    {
        if (!id.IsValid)
        {
            clip = UiDisplayListGeometry.ViewportRect(_logicalViewport);
            return true;
        }

        if ((uint)id.Value >= (uint)_clipCount)
        {
            clip = default;
            AddDiagnostic($"Order[{orderIndex}] references missing clip {id.Value}.");
            return false;
        }

        clip = _resolvedClips.RefAt(id.Value);
        return true;
    }

    internal bool TryResolve(UiClipId id, out PixelRect result)
    {
        result = UiDisplayListGeometry.ViewportRect(_logicalViewport);
        if (id.IsValid && _resolvedEpochs.RefAt(id.Value) == _frameEpoch)
        {
            result = _resolvedClips.RefAt(id.Value);
            return true;
        }

        var stamp = NextClipStamp();
        var current = id;
        while (current.IsValid)
        {
            if ((uint)current.Value >= (uint)_clipCount)
            {
                AddDiagnostic($"Clip {id.Value} has a missing parent/reference {current.Value}.");
                return false;
            }

            if (_clipMarks.RefAt(current.Value) == stamp)
            {
                AddDiagnostic($"Clip {id.Value} contains a parent cycle.");
                return false;
            }

            _clipMarks.RefAt(current.Value) = stamp;
            if (_resolvedEpochs.RefAt(current.Value) == _frameEpoch)
            {
                result = UiDisplayListGeometry.Intersect(result, _resolvedClips.RefAt(current.Value));
                break;
            }

            var region = _clips.RefAt(current.Value);
            if (region.Kind != UiClipKind.Rectangle)
            {
                AddDiagnostic($"Clip {current.Value} uses unsupported kind {region.Kind}; only rectangular clips are currently accepted.");
                return false;
            }

            if (!UiDisplayListGeometry.TryConvertBounds(region.Bounds, out var local))
            {
                AddDiagnostic($"Clip {current.Value} has invalid bounds.");
                return false;
            }

            result = UiDisplayListGeometry.Intersect(result, local);
            current = region.Parent;
        }

        _resolvedClips.RefAt(id.Value) = result;
        _resolvedEpochs.RefAt(id.Value) = _frameEpoch;
        return true;
    }

    private int NextClipStamp()
    {
        if (_clipMarkEpoch == int.MaxValue)
        {
            Array.Clear(_clipMarks, 0, _clipCount);
            _clipMarkEpoch = 1;
        }
        else
        {
            _clipMarkEpoch++;
        }

        return _clipMarkEpoch;
    }

    private void AddDiagnostic(string message) => _diagnostics.Add(message);

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (required <= storage.Length)
        {
            return;
        }

        var capacity = storage.Length == 0 ? 4 : storage.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        Array.Resize(ref storage, capacity);
    }
}
