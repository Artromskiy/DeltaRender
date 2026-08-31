using Delta.Render.RenderGraph;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

internal sealed class UiClipResolver(PixelExtent viewport, List<string> diagnostics)
{
    private readonly PixelExtent _viewport = viewport;
    private readonly List<string> _diagnostics = diagnostics;
    private UiClipRegion[] _clips = [];
    private PixelRect[] _resolvedClips = [];
    private int[] _clipMarks = [];
    private int _clipCount;
    private int _clipMarkEpoch;

    internal void SetFrame(UiClipRegion[] clips, int count)
    {
        _clips = clips;
        _clipCount = count;
        EnsureCapacity(ref _resolvedClips, count);
        EnsureCapacity(ref _clipMarks, count);
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
            clip = UiDisplayListGeometry.ViewportRect(_viewport);
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
        result = UiDisplayListGeometry.ViewportRect(_viewport);
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
