using System.Numerics;
using Delta.Render.RenderGraph;
using Delta.Text.Contract;

namespace Delta.Render.Text;

internal enum TextRenderLayer : byte
{
    Base,
    Shadow,
    Glow,
    InnerShadow,
    InnerGlow,
}

internal readonly record struct PendingRun(
    ShapedText Text,
    float OriginX,
    float OriginY,
    Vector4 Color,
    PixelRect Clip,
    bool MergeWithPrevious,
    RenderBlendState BlendState,
    TextShaderVariant? BaseShaderVariant,
    TextShaderVariant? ShadowShaderVariant,
    TextShaderVariant? GlowShaderVariant,
    TextShaderVariant? InnerShadowShaderVariant,
    TextShaderVariant? InnerGlowShaderVariant,
    TextEffectValues EffectValues,
    TextGradientValues GradientValues,
    TextRunCacheKey CacheKey,
    uint Version);

internal readonly record struct TextRunCacheKey(uint Value, uint Generation)
{
    internal bool IsValid => Value != 0 && Generation != 0;
}

internal sealed class CachedRun
{
    internal byte[] PackedBytes { get; set; } = [];
    internal TextBatch[] Batches { get; set; } = new TextBatch[4];
    internal int PackedByteCount;
    internal int InstanceCount;
    internal int BatchCount;
    internal uint Version;
    internal float OriginX;
    internal float OriginY;
    internal Vector4 Color;
    internal PixelRect Clip;
    internal bool MergeWithPrevious;
    internal RenderBlendState BlendState;
    internal TextShaderVariant? BaseShaderVariant;
    internal TextShaderVariant? ShadowShaderVariant;
    internal TextShaderVariant? GlowShaderVariant;
    internal TextShaderVariant? InnerShadowShaderVariant;
    internal TextShaderVariant? InnerGlowShaderVariant;
    internal TextEffectValues EffectValues;
    internal TextGradientValues GradientValues;
    internal ulong AtlasEpoch;
}

internal struct TextBatch
{
    internal TextBatch(int pageIndex, PixelRect clip, int start, int count)
    {
        PageIndex = pageIndex;
        Clip = clip;
        Start = start;
        Count = count;
    }

    internal int PageIndex;
    internal PixelRect Clip;
    internal int Start;
    internal int Count;
}
