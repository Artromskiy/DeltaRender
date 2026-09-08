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

public readonly struct SolidStrokeRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 FillColor;
    public readonly float4 StrokeColor;
    public readonly float StrokeWidth;

    public SolidStrokeRectangleParameters(float4 rect, float4 fillColor, float4 strokeColor, float strokeWidth)
    {
        Rect = rect;
        FillColor = fillColor;
        StrokeColor = strokeColor;
        StrokeWidth = strokeWidth;
    }
}

[Interstage]
public struct SolidStrokeRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public VertexColor FillColor;
    public FragmentColor StrokeColor;
    public BorderWidth StrokeWidth;
}

public readonly struct SolidStrokeRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidStrokeRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidStrokeRectangleFragmentContext { }

public readonly struct RoundedRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 FillColor;
    public readonly float4 CornerRadii;

    public RoundedRectangleParameters(
        float4 rect,
        float4 fillColor,
        float4 cornerRadii)
    {
        Rect = rect;
        FillColor = fillColor;
        CornerRadii = cornerRadii;
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
    public CornerRadii CornerRadii;
}

public readonly struct RoundedRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<RoundedRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct RoundedRectangleFragmentContext { }

public readonly struct RoundedStrokeRectangleParameters
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

    public RoundedStrokeRectangleParameters(
        float4 rect,
        float4 fillColor,
        float4 cornerRadii,
        UiEffectLayerParameters stroke)
    {
        Rect = rect;
        FillColor = fillColor;
        CornerRadii = cornerRadii;
        StrokeColor = stroke.Color;
        StrokeOffset = stroke.Offset;
        StrokeWidth = stroke.Width;
        StrokeBlurRadius = stroke.BlurRadius;
        StrokeSpread = stroke.Spread;
        StrokeIntensity = stroke.Intensity;
    }
}

[Interstage]
public struct RoundedStrokeRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public VertexColor FillColor;
    public CornerRadii CornerRadii;
    public EffectStrokeColor StrokeColor;
    public EffectStrokeGeometry StrokeGeometry;
    public EffectStrokeFalloff StrokeFalloff;
}

public readonly struct RoundedStrokeRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<RoundedStrokeRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct RoundedStrokeRectangleFragmentContext { }

public readonly struct SolidOuterShadowOnlyRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 OuterShadowColor;
    public readonly float2 OuterShadowOffset;
    public readonly float OuterShadowWidth;
    public readonly float OuterShadowBlurRadius;
    public readonly float OuterShadowSpread;
    public readonly float OuterShadowIntensity;

    public SolidOuterShadowOnlyRectangleParameters(float4 rect, UiEffectLayerParameters shadow)
    {
        Rect = rect;
        OuterShadowColor = shadow.Color;
        OuterShadowOffset = shadow.Offset;
        OuterShadowWidth = shadow.Width;
        OuterShadowBlurRadius = shadow.BlurRadius;
        OuterShadowSpread = shadow.Spread;
        OuterShadowIntensity = shadow.Intensity;
    }
}

[Interstage]
public struct SolidOuterShadowOnlyRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public EffectOuterShadowColor OuterShadowColor;
    public EffectOuterShadowGeometry OuterShadowGeometry;
    public EffectOuterShadowFalloff OuterShadowFalloff;
}

public readonly struct SolidOuterShadowOnlyRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidOuterShadowOnlyRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidOuterShadowOnlyRectangleFragmentContext { }

public readonly struct SolidOuterGlowOnlyRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 OuterGlowColor;
    public readonly float2 OuterGlowOffset;
    public readonly float OuterGlowRadius;
    public readonly float OuterGlowSpread;
    public readonly float OuterGlowIntensity;

    public SolidOuterGlowOnlyRectangleParameters(float4 rect, UiEffectLayerParameters glow)
    {
        Rect = rect;
        OuterGlowColor = glow.Color;
        OuterGlowOffset = glow.Offset;
        OuterGlowRadius = glow.BlurRadius;
        OuterGlowSpread = glow.Spread;
        OuterGlowIntensity = glow.Intensity;
    }
}

[Interstage]
public struct SolidOuterGlowOnlyRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public EffectOuterGlowColor OuterGlowColor;
    public EffectOuterGlowGeometry OuterGlowGeometry;
    public EffectOuterGlowFalloff OuterGlowFalloff;
}

public readonly struct SolidOuterGlowOnlyRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidOuterGlowOnlyRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidOuterGlowOnlyRectangleFragmentContext { }

public readonly struct RoundedOuterShadowOnlyRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 CornerRadii;
    public readonly float4 OuterShadowColor;
    public readonly float2 OuterShadowOffset;
    public readonly float OuterShadowWidth;
    public readonly float OuterShadowBlurRadius;
    public readonly float OuterShadowSpread;
    public readonly float OuterShadowIntensity;

    public RoundedOuterShadowOnlyRectangleParameters(
        float4 rect,
        float4 cornerRadii,
        UiEffectLayerParameters shadow)
    {
        Rect = rect;
        CornerRadii = cornerRadii;
        OuterShadowColor = shadow.Color;
        OuterShadowOffset = shadow.Offset;
        OuterShadowWidth = shadow.Width;
        OuterShadowBlurRadius = shadow.BlurRadius;
        OuterShadowSpread = shadow.Spread;
        OuterShadowIntensity = shadow.Intensity;
    }
}

[Interstage]
public struct RoundedOuterShadowOnlyRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public CornerRadii CornerRadii;
    public EffectOuterShadowColor OuterShadowColor;
    public EffectOuterShadowGeometry OuterShadowGeometry;
    public EffectOuterShadowFalloff OuterShadowFalloff;
}

public readonly struct RoundedOuterShadowOnlyRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<RoundedOuterShadowOnlyRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct RoundedOuterShadowOnlyRectangleFragmentContext { }

public readonly struct RoundedOuterGlowOnlyRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 CornerRadii;
    public readonly float4 OuterGlowColor;
    public readonly float2 OuterGlowOffset;
    public readonly float OuterGlowRadius;
    public readonly float OuterGlowSpread;
    public readonly float OuterGlowIntensity;

    public RoundedOuterGlowOnlyRectangleParameters(
        float4 rect,
        float4 cornerRadii,
        UiEffectLayerParameters glow)
    {
        Rect = rect;
        CornerRadii = cornerRadii;
        OuterGlowColor = glow.Color;
        OuterGlowOffset = glow.Offset;
        OuterGlowRadius = glow.BlurRadius;
        OuterGlowSpread = glow.Spread;
        OuterGlowIntensity = glow.Intensity;
    }
}

[Interstage]
public struct RoundedOuterGlowOnlyRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public CornerRadii CornerRadii;
    public EffectOuterGlowColor OuterGlowColor;
    public EffectOuterGlowGeometry OuterGlowGeometry;
    public EffectOuterGlowFalloff OuterGlowFalloff;
}

public readonly struct RoundedOuterGlowOnlyRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<RoundedOuterGlowOnlyRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct RoundedOuterGlowOnlyRectangleFragmentContext { }

public readonly struct InnerShadowRoundedRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 FillColor;
    public readonly float4 CornerRadii;
    public readonly float4 InnerShadowColor;
    public readonly float2 InnerShadowOffset;
    public readonly float InnerShadowWidth;
    public readonly float InnerShadowBlurRadius;
    public readonly float InnerShadowSpread;
    public readonly float InnerShadowIntensity;

    public InnerShadowRoundedRectangleParameters(
        float4 rect,
        float4 fillColor,
        float4 cornerRadii,
        UiEffectLayerParameters shadow)
    {
        Rect = rect;
        FillColor = fillColor;
        CornerRadii = cornerRadii;
        InnerShadowColor = shadow.Color;
        InnerShadowOffset = shadow.Offset;
        InnerShadowWidth = shadow.Width;
        InnerShadowBlurRadius = shadow.BlurRadius;
        InnerShadowSpread = shadow.Spread;
        InnerShadowIntensity = shadow.Intensity;
    }
}

[Interstage]
public struct InnerShadowRoundedRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public VertexColor FillColor;
    public CornerRadii CornerRadii;
    public EffectInnerShadowColor InnerShadowColor;
    public EffectInnerShadowGeometry InnerShadowGeometry;
    public EffectInnerShadowFalloff InnerShadowFalloff;
}

public readonly struct InnerShadowRoundedRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<InnerShadowRoundedRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct InnerShadowRoundedRectangleFragmentContext { }

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

public static class UiRectangleShaders
{
    private static float2 ToClipPosition(float4 rect, float2 local, float2 resolution)
    {
        float2 pixel = rect.xy + local * rect.zw;
        return (pixel / resolution) * 2f - 1f;
    }

    private static float4 GetExpandedRasterRect(
        float4 rect,
        float2 offset,
        float spread,
        float blurRadius)
    {
        float extent = max(spread + blurRadius, 0f);
        float2 padding = abs(offset) + new float2(extent);
        return new float4(rect.xy - padding, rect.zw + padding * 2f);
    }

    private static float2 GetSourceUv(float4 sourceRect, float4 rasterRect, float2 local)
    {
        float2 pixel = rasterRect.xy + local * rasterRect.zw;
        return (pixel - sourceRect.xy) / sourceRect.zw;
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
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
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

    [VertexShader("solid-stroke")]
    public static SolidStrokeRectanglePayload SolidStrokeRectangleVertex(
        in SolidStrokeRectangleVertexContext context,
        in SolidStrokeRectanglePayload input)
    {
        SolidStrokeRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidStrokeRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            StrokeColor = new FragmentColor(instance.StrokeColor),
            StrokeWidth = new BorderWidth(instance.StrokeWidth)
        };
    }

    [FragmentShader("solid-stroke")]
    public static float4 SolidStrokeRectangleFragment(
        in SolidStrokeRectangleFragmentContext context,
        in SolidStrokeRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        float distance = GetRoundedDistance(new float4(0f, 0f, 0f, 0f), pixel, size);
        float outerCoverage = Coverage(distance);
        float innerCoverage = Coverage(distance + input.StrokeWidth.Value);
        float4 fill = Premultiply(input.FillColor.Value, innerCoverage);
        float4 stroke = Premultiply(input.StrokeColor.Value, max(outerCoverage - innerCoverage, 0f));
        return Over(fill, stroke);
    }

    [VertexShader("solid-outer-shadow-only")]
    public static SolidOuterShadowOnlyRectanglePayload SolidOuterShadowOnlyRectangleVertex(
        in SolidOuterShadowOnlyRectangleVertexContext context,
        in SolidOuterShadowOnlyRectanglePayload input)
    {
        SolidOuterShadowOnlyRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 rasterRect = GetExpandedRasterRect(
            instance.Rect,
            instance.OuterShadowOffset,
            instance.OuterShadowSpread,
            instance.OuterShadowBlurRadius);
        float2 clip = ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new SolidOuterShadowOnlyRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(GetSourceUv(instance.Rect, rasterRect, local)),
            Rect = new SegmentRect(instance.Rect),
            OuterShadowColor = new EffectOuterShadowColor(instance.OuterShadowColor),
            OuterShadowGeometry = new EffectOuterShadowGeometry(new float4(
                instance.OuterShadowOffset,
                instance.OuterShadowWidth,
                instance.OuterShadowBlurRadius)),
            OuterShadowFalloff = new EffectOuterShadowFalloff(new float2(
                instance.OuterShadowSpread,
                instance.OuterShadowIntensity))
        };
    }

    [FragmentShader("solid-outer-shadow-only")]
    public static float4 SolidOuterShadowOnlyRectangleFragment(
        in SolidOuterShadowOnlyRectangleFragmentContext context,
        in SolidOuterShadowOnlyRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters shadow = new UiEffectLayerParameters(
            input.OuterShadowColor.Value,
            input.OuterShadowGeometry.Value.xy,
            input.OuterShadowGeometry.Value.z,
            input.OuterShadowGeometry.Value.w,
            input.OuterShadowFalloff.Value.x,
            input.OuterShadowFalloff.Value.y);
        return ApplyOuterShadow(
            new float4(0f, 0f, 0f, 0f),
            pixel,
            size,
            shadow,
            new float4(0f, 0f, 0f, 0f));
    }

    [VertexShader("solid-outer-glow-only")]
    public static SolidOuterGlowOnlyRectanglePayload SolidOuterGlowOnlyRectangleVertex(
        in SolidOuterGlowOnlyRectangleVertexContext context,
        in SolidOuterGlowOnlyRectanglePayload input)
    {
        SolidOuterGlowOnlyRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 rasterRect = GetExpandedRasterRect(
            instance.Rect,
            instance.OuterGlowOffset,
            instance.OuterGlowSpread,
            instance.OuterGlowRadius);
        float2 clip = ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new SolidOuterGlowOnlyRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(GetSourceUv(instance.Rect, rasterRect, local)),
            Rect = new SegmentRect(instance.Rect),
            OuterGlowColor = new EffectOuterGlowColor(instance.OuterGlowColor),
            OuterGlowGeometry = new EffectOuterGlowGeometry(new float4(
                instance.OuterGlowOffset,
                0f,
                instance.OuterGlowRadius)),
            OuterGlowFalloff = new EffectOuterGlowFalloff(new float2(
                instance.OuterGlowSpread,
                instance.OuterGlowIntensity))
        };
    }

    [FragmentShader("solid-outer-glow-only")]
    public static float4 SolidOuterGlowOnlyRectangleFragment(
        in SolidOuterGlowOnlyRectangleFragmentContext context,
        in SolidOuterGlowOnlyRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters glow = new UiEffectLayerParameters(
            input.OuterGlowColor.Value,
            input.OuterGlowGeometry.Value.xy,
            input.OuterGlowGeometry.Value.z,
            input.OuterGlowGeometry.Value.w,
            input.OuterGlowFalloff.Value.x,
            input.OuterGlowFalloff.Value.y);
        float distance = GetRoundedDistance(new float4(0f, 0f, 0f, 0f), pixel, size);
        return ApplyOuterGlow(distance, glow, new float4(0f, 0f, 0f, 0f));
    }

    [VertexShader("rounded-rectangle")]
    public static RoundedRectanglePayload RoundedRectangleVertex(in RoundedRectangleVertexContext context, in RoundedRectanglePayload input)
    {
        RoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new RoundedRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            CornerRadii = new CornerRadii(instance.CornerRadii)
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

        float4 f = input.FillColor.Value;
        float4 fill = new float4(f.xyz * f.w, f.w);
        return fill * outerCoverage;
    }

    [VertexShader("rounded-stroke")]
    public static RoundedStrokeRectanglePayload RoundedStrokeRectangleVertex(
        in RoundedStrokeRectangleVertexContext context,
        in RoundedStrokeRectanglePayload input)
    {
        RoundedStrokeRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new RoundedStrokeRectanglePayload
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
                instance.StrokeIntensity))
        };
    }

    [FragmentShader("rounded-stroke")]
    public static float4 RoundedStrokeRectangleFragment(
        in RoundedStrokeRectangleFragmentContext context,
        in RoundedStrokeRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, size);
        UiEffectLayerParameters stroke = new UiEffectLayerParameters(
            input.StrokeColor.Value,
            input.StrokeGeometry.Value.xy,
            input.StrokeGeometry.Value.z,
            input.StrokeGeometry.Value.w,
            input.StrokeFalloff.Value.x,
            input.StrokeFalloff.Value.y);
        float outer = Coverage(distance);
        float inner = min(Coverage(distance + stroke.Width), outer);
        float4 fill = Premultiply(input.FillColor.Value, 1f);
        float4 strokedFill = Over(Premultiply(stroke.Color, 1f), fill);
        return fill * inner + strokedFill * (outer - inner);
    }

    [VertexShader("rounded-outer-shadow-only")]
    public static RoundedOuterShadowOnlyRectanglePayload RoundedOuterShadowOnlyRectangleVertex(
        in RoundedOuterShadowOnlyRectangleVertexContext context,
        in RoundedOuterShadowOnlyRectanglePayload input)
    {
        RoundedOuterShadowOnlyRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 rasterRect = GetExpandedRasterRect(
            instance.Rect,
            instance.OuterShadowOffset,
            instance.OuterShadowSpread,
            instance.OuterShadowBlurRadius);
        float2 clip = ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new RoundedOuterShadowOnlyRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(GetSourceUv(instance.Rect, rasterRect, local)),
            Rect = new SegmentRect(instance.Rect),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            OuterShadowColor = new EffectOuterShadowColor(instance.OuterShadowColor),
            OuterShadowGeometry = new EffectOuterShadowGeometry(new float4(
                instance.OuterShadowOffset,
                instance.OuterShadowWidth,
                instance.OuterShadowBlurRadius)),
            OuterShadowFalloff = new EffectOuterShadowFalloff(new float2(
                instance.OuterShadowSpread,
                instance.OuterShadowIntensity))
        };
    }

    [FragmentShader("rounded-outer-shadow-only")]
    public static float4 RoundedOuterShadowOnlyRectangleFragment(
        in RoundedOuterShadowOnlyRectangleFragmentContext context,
        in RoundedOuterShadowOnlyRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters shadow = new UiEffectLayerParameters(
            input.OuterShadowColor.Value,
            input.OuterShadowGeometry.Value.xy,
            input.OuterShadowGeometry.Value.z,
            input.OuterShadowGeometry.Value.w,
            input.OuterShadowFalloff.Value.x,
            input.OuterShadowFalloff.Value.y);
        return ApplyOuterShadow(
            input.CornerRadii.Value,
            pixel,
            size,
            shadow,
            new float4(0f, 0f, 0f, 0f));
    }

    [VertexShader("rounded-outer-glow-only")]
    public static RoundedOuterGlowOnlyRectanglePayload RoundedOuterGlowOnlyRectangleVertex(
        in RoundedOuterGlowOnlyRectangleVertexContext context,
        in RoundedOuterGlowOnlyRectanglePayload input)
    {
        RoundedOuterGlowOnlyRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 rasterRect = GetExpandedRasterRect(
            instance.Rect,
            instance.OuterGlowOffset,
            instance.OuterGlowSpread,
            instance.OuterGlowRadius);
        float2 clip = ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new RoundedOuterGlowOnlyRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(GetSourceUv(instance.Rect, rasterRect, local)),
            Rect = new SegmentRect(instance.Rect),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            OuterGlowColor = new EffectOuterGlowColor(instance.OuterGlowColor),
            OuterGlowGeometry = new EffectOuterGlowGeometry(new float4(
                instance.OuterGlowOffset,
                0f,
                instance.OuterGlowRadius)),
            OuterGlowFalloff = new EffectOuterGlowFalloff(new float2(
                instance.OuterGlowSpread,
                instance.OuterGlowIntensity))
        };
    }

    [FragmentShader("rounded-outer-glow-only")]
    public static float4 RoundedOuterGlowOnlyRectangleFragment(
        in RoundedOuterGlowOnlyRectangleFragmentContext context,
        in RoundedOuterGlowOnlyRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters glow = new UiEffectLayerParameters(
            input.OuterGlowColor.Value,
            input.OuterGlowGeometry.Value.xy,
            input.OuterGlowGeometry.Value.z,
            input.OuterGlowGeometry.Value.w,
            input.OuterGlowFalloff.Value.x,
            input.OuterGlowFalloff.Value.y);
        var distance = GetRoundedDistance(input.CornerRadii.Value, pixel, size);
        return ApplyOuterGlow(distance, glow, new float4(0f, 0f, 0f, 0f));
    }

    [VertexShader("rounded-inner-shadow")]
    public static InnerShadowRoundedRectanglePayload InnerShadowRoundedRectangleVertex(
        in InnerShadowRoundedRectangleVertexContext context,
        in InnerShadowRoundedRectanglePayload input)
    {
        InnerShadowRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new InnerShadowRoundedRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            InnerShadowColor = new EffectInnerShadowColor(instance.InnerShadowColor),
            InnerShadowGeometry = new EffectInnerShadowGeometry(new float4(
                instance.InnerShadowOffset,
                instance.InnerShadowWidth,
                instance.InnerShadowBlurRadius)),
            InnerShadowFalloff = new EffectInnerShadowFalloff(new float2(
                instance.InnerShadowSpread,
                instance.InnerShadowIntensity))
        };
    }

    [FragmentShader("rounded-inner-shadow")]
    public static float4 InnerShadowRoundedRectangleFragment(
        in InnerShadowRoundedRectangleFragmentContext context,
        in InnerShadowRoundedRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 pixel = input.Uv.Value * size;
        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, size);
        UiEffectLayerParameters shadow = new UiEffectLayerParameters(
            input.InnerShadowColor.Value,
            input.InnerShadowGeometry.Value.xy,
            input.InnerShadowGeometry.Value.z,
            input.InnerShadowGeometry.Value.w,
            input.InnerShadowFalloff.Value.x,
            input.InnerShadowFalloff.Value.y);
        float4 fill = Premultiply(input.FillColor.Value, Coverage(distance));
        return ApplyInnerShadow(
            distance,
            input.CornerRadii.Value,
            pixel,
            size,
            shadow,
            fill);
    }

    [VertexShader("cached-mask-rounded-rectangle")]
    public static CachedMaskRoundedRectanglePayload CachedMaskRoundedRectangleVertex(
        in CachedMaskRoundedRectangleVertexContext context,
        in CachedMaskRoundedRectanglePayload input)
    {
        CachedMaskRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
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

    private static float4 ApplyInnerShadow(
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

    private static float4 ApplyOuterGlow(
        float distance,
        UiEffectLayerParameters outerGlow,
        float4 destination)
    {
        float outside = 1f - Coverage(distance);
        float coverage = OuterGlowCoverage(distance, outerGlow) * outside;
        return Over(Premultiply(outerGlow.Color, coverage), destination);
    }

    private static float OuterGlowCoverage(float distance, UiEffectLayerParameters outerGlow)
    {
        float blur = max(outerGlow.BlurRadius, 0.0001f);
        return (1f - smoothstep(0f, blur, max(distance - outerGlow.Spread, 0f))) * outerGlow.Intensity;
    }

}
