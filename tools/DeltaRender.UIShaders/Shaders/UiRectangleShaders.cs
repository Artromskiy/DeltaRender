using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;
using static Delta.maths;
using static Delta.Shader.intrinsics;

namespace Delta.Render.UIShaders;

public readonly struct UiFrameConstants
{
    public readonly float2 Resolution;

    public UiFrameConstants(float2 resolution)
    {
        Resolution = resolution;
    }
}

public readonly struct SolidRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 Color;

    public SolidRectangleParameters(float4 rect, float4 color)
    {
        Rect = rect;
        Color = color;
    }
}

[Interstage]
public struct SolidRectanglePayload
{
    public Position Position;
    public VertexColor Color;
}

public readonly struct SolidRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidRectangleFragmentContext { }

public readonly struct RoundedRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 FillColor;
    public readonly float4 BorderColor;
    public readonly float4 CornerRadii;
    public readonly float BorderWidth;

    public RoundedRectangleParameters(
        float4 rect,
        float4 fillColor,
        float4 borderColor,
        float4 cornerRadii,
        float borderWidth)
    {
        Rect = rect;
        FillColor = fillColor;
        BorderColor = borderColor;
        CornerRadii = cornerRadii;
        BorderWidth = borderWidth;
    }
}

public readonly struct CachedMaskRoundedRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 MaskUvRect;
    public readonly float4 Color;

    public CachedMaskRoundedRectangleParameters(float4 rect, float4 maskUvRect, float4 color)
    {
        Rect = rect;
        MaskUvRect = maskUvRect;
        Color = color;
    }
}

[Interstage]
public struct RoundedRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public VertexColor FillColor;
    public FragmentColor BorderColor;
    public CornerRadii CornerRadii;
    public BorderWidth BorderWidth;
}

public readonly struct RoundedRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<RoundedRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct RoundedRectangleFragmentContext { }

[Interstage]
public struct CachedMaskRoundedRectanglePayload
{
    public Position Position;
    public Uv0 MaskUv;
    public VertexColor Color;
}

public readonly struct CachedMaskRoundedRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<CachedMaskRoundedRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct CachedMaskRoundedRectangleFragmentContext
{
    [Layout(0, 1)]
    public readonly SampledTexture2D Mask;
}

public readonly struct UiEffectLayerParameters
{
    public readonly float4 Color;
    public readonly float2 Offset;
    public readonly float Width;
    public readonly float BlurRadius;
    public readonly float Spread;
    public readonly float Intensity;

    public UiEffectLayerParameters(
        float4 color,
        float2 offset,
        float width,
        float blurRadius,
        float spread,
        float intensity)
    {
        Color = color;
        Offset = offset;
        Width = width;
        BlurRadius = blurRadius;
        Spread = spread;
        Intensity = intensity;
    }
}

public readonly struct UiEffectParameters
{
    public readonly UiEffectLayerParameters StrokeOrOutline;
    public readonly UiEffectLayerParameters OuterShadow;
    public readonly UiEffectLayerParameters InsetShadow;
    public readonly UiEffectLayerParameters Glow;

    public UiEffectParameters(
        UiEffectLayerParameters strokeOrOutline,
        UiEffectLayerParameters outerShadow,
        UiEffectLayerParameters insetShadow,
        UiEffectLayerParameters glow)
    {
        StrokeOrOutline = strokeOrOutline;
        OuterShadow = outerShadow;
        InsetShadow = insetShadow;
        Glow = glow;
    }
}

public readonly struct AnalyticRoundedRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 FillColor;
    public readonly float4 CornerRadii;
    public readonly float4 StrokeColor;
    public readonly float2 StrokeOffset;
    public readonly float StrokeWidth;
    public readonly float StrokeBlurRadius;
    public readonly float StrokeSpread;
    public readonly float StrokeIntensity;
    public readonly float4 OuterShadowColor;
    public readonly float2 OuterShadowOffset;
    public readonly float OuterShadowWidth;
    public readonly float OuterShadowBlurRadius;
    public readonly float OuterShadowSpread;
    public readonly float OuterShadowIntensity;
    public readonly float4 InsetShadowColor;
    public readonly float2 InsetShadowOffset;
    public readonly float InsetShadowWidth;
    public readonly float InsetShadowBlurRadius;
    public readonly float InsetShadowSpread;
    public readonly float InsetShadowIntensity;
    public readonly float4 GlowColor;
    public readonly float2 GlowOffset;
    public readonly float GlowWidth;
    public readonly float GlowBlurRadius;
    public readonly float GlowSpread;
    public readonly float GlowIntensity;

    public AnalyticRoundedRectangleParameters(
        float4 rect,
        float4 fillColor,
        float4 cornerRadii,
        UiEffectParameters effects)
    {
        Rect = rect;
        FillColor = fillColor;
        CornerRadii = cornerRadii;
        StrokeColor = effects.StrokeOrOutline.Color;
        StrokeOffset = effects.StrokeOrOutline.Offset;
        StrokeWidth = effects.StrokeOrOutline.Width;
        StrokeBlurRadius = effects.StrokeOrOutline.BlurRadius;
        StrokeSpread = effects.StrokeOrOutline.Spread;
        StrokeIntensity = effects.StrokeOrOutline.Intensity;
        OuterShadowColor = effects.OuterShadow.Color;
        OuterShadowOffset = effects.OuterShadow.Offset;
        OuterShadowWidth = effects.OuterShadow.Width;
        OuterShadowBlurRadius = effects.OuterShadow.BlurRadius;
        OuterShadowSpread = effects.OuterShadow.Spread;
        OuterShadowIntensity = effects.OuterShadow.Intensity;
        InsetShadowColor = effects.InsetShadow.Color;
        InsetShadowOffset = effects.InsetShadow.Offset;
        InsetShadowWidth = effects.InsetShadow.Width;
        InsetShadowBlurRadius = effects.InsetShadow.BlurRadius;
        InsetShadowSpread = effects.InsetShadow.Spread;
        InsetShadowIntensity = effects.InsetShadow.Intensity;
        GlowColor = effects.Glow.Color;
        GlowOffset = effects.Glow.Offset;
        GlowWidth = effects.Glow.Width;
        GlowBlurRadius = effects.Glow.BlurRadius;
        GlowSpread = effects.Glow.Spread;
        GlowIntensity = effects.Glow.Intensity;
    }
}

[Interstage]
public struct AnalyticRoundedRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public VertexColor FillColor;
    public CornerRadii CornerRadii;
    public EffectStrokeColor StrokeColor;
    public EffectStrokeGeometry StrokeGeometry;
    public EffectStrokeFalloff StrokeFalloff;
    public EffectOuterShadowColor OuterShadowColor;
    public EffectOuterShadowGeometry OuterShadowGeometry;
    public EffectOuterShadowFalloff OuterShadowFalloff;
    public EffectInsetShadowColor InsetShadowColor;
    public EffectInsetShadowGeometry InsetShadowGeometry;
    public EffectInsetShadowFalloff InsetShadowFalloff;
    public EffectGlowColor GlowColor;
    public EffectGlowGeometry GlowGeometry;
    public EffectGlowFalloff GlowFalloff;
}

public readonly struct AnalyticRoundedRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<AnalyticRoundedRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct AnalyticRoundedRectangleFragmentContext { }

public static class UiRectangleShaders
{
    private static float2 GetQuadLocal(uint vertexIndex)
    {
        // Битовые маски для индексов 0..5, формирующих два треугольника (quad).
        // X = 1 для вершин 1, 2, 4 (маска 22 = 0b010110)
        // Y = 1 для вершин 2, 4, 5 (маска 52 = 0b110100)
        float x = (22u >> (int)vertexIndex) & 1u;
        float y = (52u >> (int)vertexIndex) & 1u;
        return new float2(x, y);
    }

    private static float2 ToClipPosition(float4 rect, float2 local, float2 resolution)
    {
        float2 pixel = rect.xy + local * rect.zw;
        return (pixel / resolution) * 2f - 1f;
    }

    private static float4 GetCornerData(float4 cornerRadii, float2 pixel, float2 size)
    {
        float4 d = new float4(pixel, size - pixel);
        float4 influence = max(cornerRadii - max(d.xzzx, d.yyww), 0f);

        float2 max2 = max(influence.xy, influence.zw);
        float maxInfluence = max(max2.x, max2.y);

        float4 hasMax = step(maxInfluence, influence);
        float4 notMax = 1f - hasMax;

        float m0 = notMax.x;
        float m1 = m0 * notMax.y;
        float m2 = m1 * notMax.z;

        float4 winner = hasMax * new float4(1f, m0, m1, m2);
        float r = dot(cornerRadii, winner);

        float2 isRightTop = new float2(winner.y + winner.z, winner.z + winner.w);
        float2 center = isRightTop * (size - 2f * r) + r;

        return new float4(r, center.x, center.y, maxInfluence);
    }

    [VertexShader("solid-rectangle")]
    public static SolidRectanglePayload SolidRectangleVertex(in SolidRectangleVertexContext context, in SolidRectanglePayload input)
    {
        SolidRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new SolidRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Color = new VertexColor(instance.Color)
        };
    }

    [FragmentShader("solid-rectangle")]
    public static float4 SolidRectangleFragment(in SolidRectangleFragmentContext context, in SolidRectanglePayload input)
    {
        float4 c = input.Color.Value;
        return new float4(c.xyz * c.w, c.w);
    }

    [VertexShader("rounded-rectangle")]
    public static RoundedRectanglePayload RoundedRectangleVertex(in RoundedRectangleVertexContext context, in RoundedRectanglePayload input)
    {
        RoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new RoundedRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            BorderColor = new FragmentColor(instance.BorderColor),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            BorderWidth = new BorderWidth(instance.BorderWidth)
        };
    }

    [FragmentShader("rounded-rectangle")]
    public static float4 RoundedRectangleFragment(in RoundedRectangleFragmentContext context, in RoundedRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;

        float4 cornerData = GetCornerData(input.CornerRadii.Value, pixel, size);

        float distance;
        // Если мы в зоне влияния угла, считаем расстояние только до круга.
        // Иначе считаем стандартный Box SDF. Это экономит инструкции.
        if (cornerData.w > 0f)
        {
            distance = length(pixel - cornerData.yz) - cornerData.x;
        }
        else
        {
            float2 halfSize = size * 0.5f;
            float2 q = abs(pixel - halfSize) - halfSize;
            distance = length(max(q, 0f)) + min(max(q.x, q.y), 0f);
        }

        float edge = max(fwidth(distance) * 0.5f, 0.0001f);
        float outerCoverage = 1f - smoothstep(-edge, edge, distance);

        if (outerCoverage <= 0f)
        {
            _ = discard;
        }

        float innerCoverage = 1f - smoothstep(-edge, edge, distance + input.BorderWidth.Value);
        float borderCoverage = outerCoverage - innerCoverage;

        // Предварительное умножение альфы (Premultiply Alpha) исходных цветов
        float4 f = input.FillColor.Value;
        float4 b = input.BorderColor.Value;
        float4 fill = new float4(f.xyz * f.w, f.w);
        float4 border = new float4(b.xyz * b.w, b.w);

        return fill * innerCoverage + border * borderCoverage;
    }

    [VertexShader("cached-mask-rounded-rectangle")]
    public static CachedMaskRoundedRectanglePayload CachedMaskRoundedRectangleVertex(
        in CachedMaskRoundedRectangleVertexContext context,
        in CachedMaskRoundedRectanglePayload input)
    {
        CachedMaskRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new CachedMaskRoundedRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            MaskUv = new Uv0(instance.MaskUvRect.xy + local * instance.MaskUvRect.zw),
            Color = new VertexColor(instance.Color)
        };
    }

    [FragmentShader("cached-mask-rounded-rectangle")]
    public static float4 CachedMaskRoundedRectangleFragment(
        in CachedMaskRoundedRectangleFragmentContext context,
        in CachedMaskRoundedRectanglePayload input)
    {
        float4 mask = context.Mask.Sample<float2, float4>(input.MaskUv.Value);
        float4 color = input.Color.Value;
        return new float4(mask.xyz * color.xyz, mask.w * color.w);
    }

    private static float GetRoundedDistance(float4 cornerRadii, float2 pixel, float2 size)
    {
        float4 cornerData = GetCornerData(cornerRadii, pixel, size);

        if (cornerData.w > 0f)
        {
            return length(pixel - cornerData.yz) - cornerData.x;
        }

        float2 halfSize = size * 0.5f;
        float2 q = abs(pixel - halfSize) - halfSize;
        return length(max(q, 0f)) + min(max(q.x, q.y), 0f);
    }

    private static float Coverage(float distance)
    {
        float edge = max(fwidth(distance) * 0.5f, 0.0001f);
        return 1f - smoothstep(-edge, edge, distance);
    }

    private static float4 Premultiply(float4 color, float coverage)
    {
        float alpha = color.w * coverage;
        return new float4(color.xyz * alpha, alpha);
    }

    private static float4 Over(float4 source, float4 destination)
    {
        return source + destination * (1f - source.w);
    }

    private static float4 ApplyStroke(
        float distance,
        UiEffectLayerParameters stroke,
        float4 destination)
    {
        float outer = Coverage(distance);
        float inner = Coverage(distance + stroke.Width);
        float coverage = max(outer - inner, 0f);
        return Over(Premultiply(stroke.Color, coverage), destination);
    }

    private static float4 ApplyOuterShadow(
        float4 cornerRadii,
        float2 pixel,
        float2 size,
        UiEffectLayerParameters shadow,
        float4 destination)
    {
        float distance = GetRoundedDistance(cornerRadii, pixel - shadow.Offset, size) - shadow.Spread;
        float blur = max(shadow.BlurRadius, 0.0001f);
        float coverage = 1f - smoothstep(0f, blur + fwidth(distance), max(distance, 0f));
        return Over(Premultiply(shadow.Color, coverage * shadow.Intensity), destination);
    }

    private static float4 ApplyInsetShadow(
        float distance,
        float4 cornerRadii,
        float2 pixel,
        float2 size,
        UiEffectLayerParameters shadow,
        float4 destination)
    {
        float shiftedDistance = GetRoundedDistance(cornerRadii, pixel - shadow.Offset, size);
        float depth = max(-shiftedDistance - shadow.Spread, 0f);
        float blur = max(shadow.BlurRadius, 0.0001f);
        float coverage = Coverage(distance) * (1f - smoothstep(
            shadow.Width,
            shadow.Width + blur + fwidth(shiftedDistance),
            depth));
        return Over(Premultiply(shadow.Color, coverage * shadow.Intensity), destination);
    }

    private static float4 ApplyGlow(
        float distance,
        UiEffectLayerParameters glow,
        float4 destination)
    {
        float blur = max(glow.BlurRadius, 0.0001f);
        float outside = 1f - Coverage(distance);
        float coverage = exp(-max(distance - glow.Spread, 0f) / blur) * outside * glow.Intensity;
        return Over(Premultiply(glow.Color, coverage), destination);
    }

    [VertexShader("analytic-rounded-rectangle")]
    public static AnalyticRoundedRectanglePayload AnalyticRoundedRectangleVertex(
        in AnalyticRoundedRectangleVertexContext context,
        in AnalyticRoundedRectanglePayload input)
    {
        AnalyticRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new AnalyticRoundedRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            StrokeColor = new EffectStrokeColor(instance.StrokeColor),
            StrokeGeometry = new EffectStrokeGeometry(new float4(
                instance.StrokeOffset,
                instance.StrokeWidth,
                instance.StrokeBlurRadius)),
            StrokeFalloff = new EffectStrokeFalloff(new float2(
                instance.StrokeSpread,
                instance.StrokeIntensity)),
            OuterShadowColor = new EffectOuterShadowColor(instance.OuterShadowColor),
            OuterShadowGeometry = new EffectOuterShadowGeometry(new float4(
                instance.OuterShadowOffset,
                instance.OuterShadowWidth,
                instance.OuterShadowBlurRadius)),
            OuterShadowFalloff = new EffectOuterShadowFalloff(new float2(
                instance.OuterShadowSpread,
                instance.OuterShadowIntensity)),
            InsetShadowColor = new EffectInsetShadowColor(instance.InsetShadowColor),
            InsetShadowGeometry = new EffectInsetShadowGeometry(new float4(
                instance.InsetShadowOffset,
                instance.InsetShadowWidth,
                instance.InsetShadowBlurRadius)),
            InsetShadowFalloff = new EffectInsetShadowFalloff(new float2(
                instance.InsetShadowSpread,
                instance.InsetShadowIntensity)),
            GlowColor = new EffectGlowColor(instance.GlowColor),
            GlowGeometry = new EffectGlowGeometry(new float4(
                instance.GlowOffset,
                instance.GlowWidth,
                instance.GlowBlurRadius)),
            GlowFalloff = new EffectGlowFalloff(new float2(
                instance.GlowSpread,
                instance.GlowIntensity))
        };
    }

    [FragmentShader("analytic-rounded-rectangle")]
    public static float4 AnalyticRoundedRectangleFragment(
        in AnalyticRoundedRectangleFragmentContext context,
        in AnalyticRoundedRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters stroke = new(
            input.StrokeColor.Value,
            input.StrokeGeometry.Value.xy,
            input.StrokeGeometry.Value.z,
            input.StrokeGeometry.Value.w,
            input.StrokeFalloff.Value.x,
            input.StrokeFalloff.Value.y);
        UiEffectLayerParameters shadow = new(
            input.OuterShadowColor.Value,
            input.OuterShadowGeometry.Value.xy,
            input.OuterShadowGeometry.Value.z,
            input.OuterShadowGeometry.Value.w,
            input.OuterShadowFalloff.Value.x,
            input.OuterShadowFalloff.Value.y);
        UiEffectLayerParameters glow = new(
            input.GlowColor.Value,
            input.GlowGeometry.Value.xy,
            input.GlowGeometry.Value.z,
            input.GlowGeometry.Value.w,
            input.GlowFalloff.Value.x,
            input.GlowFalloff.Value.y);
        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, size);

        float4 color = ApplyOuterShadow(
            input.CornerRadii.Value,
            pixel,
            size,
            shadow,
            new float4(0f, 0f, 0f, 0f));
        color = ApplyGlow(distance, glow, color);
        color = Over(Premultiply(input.FillColor.Value, Coverage(distance)), color);
        UiEffectLayerParameters insetShadow = new(
            input.InsetShadowColor.Value,
            input.InsetShadowGeometry.Value.xy,
            input.InsetShadowGeometry.Value.z,
            input.InsetShadowGeometry.Value.w,
            input.InsetShadowFalloff.Value.x,
            input.InsetShadowFalloff.Value.y);
        color = ApplyInsetShadow(
            distance,
            input.CornerRadii.Value,
            pixel,
            size,
            insetShadow,
            color);
        return ApplyStroke(distance, stroke, color);
    }
}
