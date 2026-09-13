using Delta;
using Delta.Render.RenderGraph;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

/// <summary>Builds the renderer-owned one-dimensional texture used by a textured gradient ABI.</summary>
internal static class UiGradientTexture
{
    internal const uint Width = 256;
    internal const uint Height = 1;
    internal const uint BytesPerPixel = 4;

    internal static RenderTextureDescription Description => new(
        Width,
        Height,
        RenderTextureFormat.Rgba8Unorm,
        RenderTextureUsage.Sampled | RenderTextureUsage.TransferDestination);

    internal static byte[] CreatePixels(UiLinearGradientResource gradient)
    {
        if (gradient.Stops is null || gradient.Stops.Count is < 2 or > 4)
        {
            throw new ArgumentException("A gradient texture requires two to four stops.", nameof(gradient));
        }

        var pixels = new byte[checked((int)(Width * Height * BytesPerPixel))];
        var stopIndex = 0;
        for (var pixelIndex = 0; pixelIndex < Width; pixelIndex++)
        {
            var position = (float)pixelIndex / (Width - 1);
            while (stopIndex + 1 < gradient.Stops.Count &&
                   position >= gradient.Stops[stopIndex + 1].Position)
            {
                stopIndex++;
            }

            var left = gradient.Stops[stopIndex];
            var right = stopIndex + 1 < gradient.Stops.Count ? gradient.Stops[stopIndex + 1] : left;
            var amount = right.Position > left.Position
                ? Maths.Clamp((position - left.Position) / (right.Position - left.Position), 0f, 1f)
                : 0f;
            var color = left.Color + (right.Color - left.Color) * amount;
            var offset = checked((int)(pixelIndex * BytesPerPixel));
            pixels[offset] = ToByte(color.x);
            pixels[offset + 1] = ToByte(color.y);
            pixels[offset + 2] = ToByte(color.z);
            pixels[offset + 3] = ToByte(color.w);
        }

        return pixels;
    }

    private static byte ToByte(float value)
    {
        var scaled = Maths.Round(Maths.Clamp(value, 0f, 1f) * byte.MaxValue);
        return (byte)Maths.Clamp((int)scaled, byte.MinValue, byte.MaxValue);
    }
}

/// <summary>Owns session textures and samplers for gradient LUTs until a feature is disposed.</summary>
internal sealed class UiGradientTextureCache : IDisposable
{
    private readonly IRenderFrameSession _session;
    private readonly Dictionary<UiResourceId, Entry> _entries = [];
    private bool _disposed;

    internal UiGradientTextureCache(IRenderFrameSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal UiGradientTextureLease Prepare(UiResourceId resource, UiLinearGradientResource gradient)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!resource.IsValid)
        {
            throw new ArgumentException("A gradient texture requires a valid resource identity.", nameof(resource));
        }

        if (_entries.TryGetValue(resource, out var entry) && SameGradient(entry.Gradient, gradient))
        {
            return entry.Lease(resource);
        }

        if (entry is not null)
        {
            Release(entry);
        }

        entry = CreateEntry(gradient);
        _entries[resource] = entry;
        return entry.Lease(resource);
    }

    internal void MarkUploaded(UiResourceId resource)
    {
        if (_entries.TryGetValue(resource, out var entry))
        {
            entry.Uploaded = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            Release(entry);
        }

        _entries.Clear();
    }

    private Entry CreateEntry(UiLinearGradientResource gradient)
    {
        var texture = default(RenderTextureHandle);
        var sampler = default(RenderSamplerHandle);
        try
        {
            texture = _session.CreateTexture(UiGradientTexture.Description);
            sampler = _session.CreateSampler(new RenderSamplerDescription());
            return new Entry(gradient, texture, sampler, UiGradientTexture.CreatePixels(gradient));
        }
        catch
        {
            if (sampler.IsValid)
            {
                _session.Release(sampler);
            }

            if (texture.IsValid)
            {
                _session.Release(texture);
            }

            throw;
        }
    }

    private void Release(Entry entry)
    {
        if (entry.Texture.IsValid)
        {
            _session.Release(entry.Texture);
            entry.Texture = default;
        }

        if (entry.Sampler.IsValid)
        {
            _session.Release(entry.Sampler);
            entry.Sampler = default;
        }
    }

    private static bool SameGradient(UiLinearGradientResource left, UiLinearGradientResource right)
    {
        if (left.Start != right.Start || left.End != right.End || left.Units != right.Units ||
            left.IsRelativeToBounds != right.IsRelativeToBounds || left.AngleDegrees != right.AngleDegrees ||
            left.IsRadial != right.IsRadial || left.OutlineColor != right.OutlineColor ||
            left.OutlineWidth != right.OutlineWidth || left.Stops.Count != right.Stops.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Stops.Count; index++)
        {
            if (left.Stops[index] != right.Stops[index])
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Entry(
        UiLinearGradientResource gradient,
        RenderTextureHandle texture,
        RenderSamplerHandle sampler,
        byte[] pixels)
    {
        internal UiLinearGradientResource Gradient { get; } = gradient;
        internal RenderTextureHandle Texture { get; set; } = texture;
        internal RenderSamplerHandle Sampler { get; set; } = sampler;
        internal byte[] Pixels { get; } = pixels;
        internal bool Uploaded;

        internal UiGradientTextureLease Lease(UiResourceId resource)
            => new(resource, Texture, Sampler, Pixels, !Uploaded);
    }
}

internal readonly record struct UiGradientTextureLease(
    UiResourceId Resource,
    RenderTextureHandle Texture,
    RenderSamplerHandle Sampler,
    byte[] Pixels,
    bool NeedsUpload);
