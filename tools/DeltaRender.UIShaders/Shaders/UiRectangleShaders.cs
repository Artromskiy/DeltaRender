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
    public readonly float4 StrokeWidths;

    public SolidStrokeRectangleParameters(float4 rect, float4 fillColor, UiEffectLayerParameters stroke)
    {
        Rect = rect;
        FillColor = fillColor;
        StrokeColor = stroke.Color;
        StrokeWidth = stroke.Width;
        StrokeWidths = stroke.SideWidths;
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
    public BorderWidths StrokeWidths;
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
    public readonly float4 StrokeWidths;
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
        StrokeWidths = stroke.SideWidths;
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
    public BorderWidths StrokeWidths;
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
    public readonly float4 SideWidths;

    public UiEffectLayerParameters(
        float4 color,
        float2 offset,
        float width,
        float blurRadius,
        float spread,
        float intensity,
        float4 sideWidths)
    {
        Color = color;
        Offset = offset;
        Width = width;
        BlurRadius = blurRadius;
        Spread = spread;
        Intensity = intensity;
        SideWidths = sideWidths;
    }
}

public static class UiRectangleShaders
{
    private static float4 GetExpandedRasterRect(
        float4 rect,
        float2 offset,
        float spread,
        float blurRadius)
    {
        float extent = max(spread + blurRadius, 0f);
        float2 padding = abs(offset) + new float2(extent);
        return new float4(rect.xy - padding, rect.zw + padding + padding);
    }

    private static float2 GetSourceUv(float4 sourceRect, float4 rasterRect, float2 local)
    {
        return (rasterRect.xy + local * rasterRect.zw - sourceRect.xy) / sourceRect.zw;
    }

    private static float GetCornerRadius(float4 cornerRadii, float2 pixel, float2 halfSize)
    {
        float2 rightBottom = step(halfSize, pixel);
        float2 topBottom = cornerRadii.xw + rightBottom.x * (cornerRadii.yz - cornerRadii.xw);
        return topBottom.x + rightBottom.y * (topBottom.y - topBottom.x);
    }

    private static float2 GetCornerRadiusPair(
        float4 cornerRadiiX,
        float4 cornerRadiiY,
        float2 pixel,
        float2 center)
    {
        float2 rightBottom = step(center, pixel);
        float2 topBottomX = cornerRadiiX.xw + rightBottom.x * (cornerRadiiX.yz - cornerRadiiX.xw);
        float2 topBottomY = cornerRadiiY.xw + rightBottom.x * (cornerRadiiY.yz - cornerRadiiY.xw);
        return new float2(
            topBottomX.x + rightBottom.y * (topBottomX.y - topBottomX.x),
            topBottomY.x + rightBottom.y * (topBottomY.y - topBottomY.x));
    }

    private static float Edge(float distance) => max(fwidth(distance) * 0.5f, 0.0001f);

    [VertexShader("solid-rectangle")]
    public static SolidRectanglePayload SolidRectangleVertex(in SolidRectangleVertexContext context, in SolidRectanglePayload input)
    {
        SolidRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new SolidRectanglePayload
        {
            Position = new Position(position),
            Color = new VertexColor(instance.Color)
        };
    }

    [FragmentShader("solid-rectangle")]
    public static float4 SolidRectangleFragment(in SolidRectangleFragmentContext context, in SolidRectanglePayload input)
    {
        return UiColorMath.Premultiply(input.Color.Value);
    }

    [VertexShader("solid-stroke")]
    public static SolidStrokeRectanglePayload SolidStrokeRectangleVertex(
        in SolidStrokeRectangleVertexContext context,
        in SolidStrokeRectanglePayload input)
    {
        SolidStrokeRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidStrokeRectanglePayload
        {
            Position = new Position(position),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            FillColor = new VertexColor(instance.FillColor),
            StrokeColor = new FragmentColor(instance.StrokeColor),
            StrokeWidth = new BorderWidth(instance.StrokeWidth),
            StrokeWidths = new BorderWidths(instance.StrokeWidths),
        };
    }

    [FragmentShader("solid-stroke")]
    public static float4 SolidStrokeRectangleFragment(
        in SolidStrokeRectangleFragmentContext context,
        in SolidStrokeRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        float distance = GetBoxDistance(pixel, halfSize);
        float edge = Edge(distance);
        float outerCoverage = Coverage(distance, edge);
        float4 strokeWidths = ResolveStrokeWidths(input.StrokeWidth.Value, input.StrokeWidths.Value);
        float innerDistance = GetInsetBoxDistance(pixel, halfSize, strokeWidths);
        float innerCoverage = min(Coverage(innerDistance, Edge(innerDistance)), outerCoverage);
        float4 fill = UiColorMath.Premultiply(input.FillColor.Value, innerCoverage);
        float4 stroke = UiColorMath.Premultiply(input.StrokeColor.Value, max(outerCoverage - innerCoverage, 0f));
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
        float4 position = QuadGeometry.ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new SolidOuterShadowOnlyRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters shadow = CreateEffectParameters(
            input.OuterShadowColor.Value,
            input.OuterShadowGeometry.Value,
            input.OuterShadowFalloff.Value);
        return ApplyBoxOuterShadow(
            pixel,
            halfSize,
            shadow);
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
        float4 position = QuadGeometry.ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new SolidOuterGlowOnlyRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters glow = CreateEffectParameters(
            input.OuterGlowColor.Value,
            input.OuterGlowGeometry.Value,
            input.OuterGlowFalloff.Value);
        float distance = GetBoxDistance(pixel, halfSize);
        return ApplyOuterGlow(distance, glow);
    }

    [VertexShader("rounded-rectangle")]
    public static RoundedRectanglePayload RoundedRectangleVertex(in RoundedRectangleVertexContext context, in RoundedRectanglePayload input)
    {
        RoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new RoundedRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;

        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, halfSize);

        float edge = Edge(distance);
        float outerCoverage = Coverage(distance, edge);
        return UiColorMath.Premultiply(input.FillColor.Value, outerCoverage);
    }

    [VertexShader("rounded-stroke")]
    public static RoundedStrokeRectanglePayload RoundedStrokeRectangleVertex(
        in RoundedStrokeRectangleVertexContext context,
        in RoundedStrokeRectanglePayload input)
    {
        RoundedStrokeRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new RoundedStrokeRectanglePayload
        {
            Position = new Position(position),
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
            StrokeWidths = new BorderWidths(instance.StrokeWidths),
        };
    }

    [FragmentShader("rounded-stroke")]
    public static float4 RoundedStrokeRectangleFragment(
        in RoundedStrokeRectangleFragmentContext context,
        in RoundedStrokeRectanglePayload input)
    {
        float2 size = input.Rect.Value.zw;
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, halfSize);
        UiEffectLayerParameters stroke = CreateEffectParameters(
            input.StrokeColor.Value,
            input.StrokeGeometry.Value,
            input.StrokeFalloff.Value);
        float edge = Edge(distance);
        float outer = Coverage(distance, edge);
        float4 strokeWidths = ResolveStrokeWidths(stroke.Width, input.StrokeWidths.Value);
        float innerDistance = GetInsetRoundedDistance(input.CornerRadii.Value, strokeWidths, pixel, halfSize);
        float inner = min(Coverage(innerDistance, Edge(innerDistance)), outer);
        float4 fill = UiColorMath.Premultiply(input.FillColor.Value);
        float4 strokedFill = Over(UiColorMath.Premultiply(stroke.Color), fill);
        return inner * fill + (outer - inner) * strokedFill;
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
        float4 position = QuadGeometry.ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new RoundedOuterShadowOnlyRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters shadow = CreateEffectParameters(
            input.OuterShadowColor.Value,
            input.OuterShadowGeometry.Value,
            input.OuterShadowFalloff.Value);
        return ApplyOuterShadow(
            input.CornerRadii.Value,
            pixel,
            halfSize,
            shadow);
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
        float4 position = QuadGeometry.ToClipPosition(rasterRect, local, context.Frame.Resolution);
        return new RoundedOuterGlowOnlyRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        UiEffectLayerParameters glow = CreateEffectParameters(
            input.OuterGlowColor.Value,
            input.OuterGlowGeometry.Value,
            input.OuterGlowFalloff.Value);
        var distance = GetRoundedDistance(input.CornerRadii.Value, pixel, halfSize);
        return ApplyOuterGlow(distance, glow);
    }

    [VertexShader("rounded-inner-shadow")]
    public static InnerShadowRoundedRectanglePayload InnerShadowRoundedRectangleVertex(
        in InnerShadowRoundedRectangleVertexContext context,
        in InnerShadowRoundedRectanglePayload input)
    {
        InnerShadowRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);

        return new InnerShadowRoundedRectanglePayload
        {
            Position = new Position(position),
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
        float2 halfSize = size * 0.5f;
        float2 pixel = input.Uv.Value * size;
        float distance = GetRoundedDistance(input.CornerRadii.Value, pixel, halfSize);
        UiEffectLayerParameters shadow = CreateEffectParameters(
            input.InnerShadowColor.Value,
            input.InnerShadowGeometry.Value,
            input.InnerShadowFalloff.Value);
        float shapeCoverage = Coverage(distance);
        float4 fill = UiColorMath.Premultiply(input.FillColor.Value, shapeCoverage);
        return ApplyInnerShadow(
            input.CornerRadii.Value,
            pixel,
            halfSize,
            shadow,
            shapeCoverage,
            fill);
    }

    [VertexShader("cached-mask-rounded-rectangle")]
    public static CachedMaskRoundedRectanglePayload CachedMaskRoundedRectangleVertex(
        in CachedMaskRoundedRectangleVertexContext context,
        in CachedMaskRoundedRectanglePayload input)
    {
        CachedMaskRoundedRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new CachedMaskRoundedRectanglePayload
        {
            Position = new Position(position),
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

    private static float GetRoundedDistance(float4 cornerRadii, float2 pixel, float2 halfSize)
    {
        float radius = GetCornerRadius(cornerRadii, pixel, halfSize);
        float2 q = abs(pixel - halfSize) - halfSize + radius;
        return length(max(q, 0f)) + min(max(q.x, q.y), 0f) - radius;
    }

    private static float GetBoxDistance(float2 pixel, float2 halfSize)
    {
        return GetBoxDistance(pixel, halfSize, halfSize);
    }

    private static float GetBoxDistance(float2 pixel, float2 center, float2 halfSize)
    {
        float2 q = abs(pixel - center) - halfSize;
        return length(max(q, 0f)) + min(max(q.x, q.y), 0f);
    }

    private static float GetInsetBoxDistance(float2 pixel, float2 halfSize, float4 sideWidths)
    {
        float2 innerHalfSize = max(
            halfSize - new float2(
                (sideWidths.x + sideWidths.z) * 0.5f,
                (sideWidths.y + sideWidths.w) * 0.5f),
            new float2(0f));
        float2 innerCenter = halfSize + new float2(
            (sideWidths.x - sideWidths.z) * 0.5f,
            (sideWidths.y - sideWidths.w) * 0.5f);
        return GetBoxDistance(pixel, innerCenter, innerHalfSize);
    }

    private static float GetInsetRoundedDistance(
        float4 cornerRadii,
        float4 sideWidths,
        float2 pixel,
        float2 halfSize)
    {
        float2 innerHalfSize = max(
            halfSize - new float2(
                (sideWidths.x + sideWidths.z) * 0.5f,
                (sideWidths.y + sideWidths.w) * 0.5f),
            new float2(0f));
        float2 innerCenter = halfSize + new float2(
            (sideWidths.x - sideWidths.z) * 0.5f,
            (sideWidths.y - sideWidths.w) * 0.5f);
        float4 innerRadiiX = max(
            cornerRadii - new float4(sideWidths.x, sideWidths.z, sideWidths.z, sideWidths.x),
            new float4(0f));
        float4 innerRadiiY = max(
            cornerRadii - new float4(sideWidths.y, sideWidths.y, sideWidths.w, sideWidths.w),
            new float4(0f));
        float2 radius = GetCornerRadiusPair(innerRadiiX, innerRadiiY, pixel, innerCenter);
        float2 q = abs(pixel - innerCenter) - innerHalfSize + radius;
        // The rounded SDF adds each axis radius to q. Subtract those radii
        // again for the straight-edge branch; otherwise the inner contour is
        // displaced by the corner radius and a stroke paints large solid bands.
        float edgeDistance = max(q.x - radius.x, q.y - radius.y);
        float2 safeRadius = max(radius, new float2(0.0001f));
        float cornerDistance = (length(max(q, new float2(0f)) / safeRadius) - 1f) *
            max(min(radius.x, radius.y), 0.0001f);
        float corner = step(0f, min(q.x, q.y));
        return corner * cornerDistance + (1f - corner) * edgeDistance;
    }

    private static float4 ResolveStrokeWidths(float width, float4 sideWidths)
    {
        float hasSideWidths = step(0.0001f, dot(abs(sideWidths), new float4(1f)));
        return sideWidths + (1f - hasSideWidths) * new float4(width);
    }

    private static float Coverage(float distance)
    {
        return Coverage(distance, Edge(distance));
    }

    private static float Coverage(float distance, float edge)
    {
        return 1f - smoothstep(-edge, edge, distance);
    }

    private static UiEffectLayerParameters CreateEffectParameters(
        float4 color,
        float4 geometry,
        float2 falloff) =>
        new UiEffectLayerParameters(
            color,
            geometry.xy,
            geometry.z,
            geometry.w,
            falloff.x,
            falloff.y,
            new float4(0f));

    private static float4 Over(float4 source, float4 destination)
    {
        return source + (1f - source.w) * destination;
    }

    private static float4 ApplyOuterShadow(
        float4 cornerRadii,
        float2 pixel,
        float2 halfSize,
        UiEffectLayerParameters shadow)
    {
        float distance = GetRoundedDistance(cornerRadii, pixel - shadow.Offset, halfSize) - shadow.Spread;
        return ApplyOuterShadowDistance(distance, shadow);
    }

    private static float4 ApplyBoxOuterShadow(
        float2 pixel,
        float2 halfSize,
        UiEffectLayerParameters shadow)
    {
        float distance = GetBoxDistance(pixel - shadow.Offset, halfSize) - shadow.Spread;
        return ApplyOuterShadowDistance(distance, shadow);
    }

    private static float4 ApplyOuterShadowDistance(
        float distance,
        UiEffectLayerParameters shadow)
    {
        float blur = max(shadow.BlurRadius, 0.0001f);
        float coverage = 1f - smoothstep(0f, blur + fwidth(distance), max(distance, 0f));
        return UiColorMath.Premultiply(shadow.Color, coverage * shadow.Intensity);
    }

    private static float4 ApplyInnerShadow(
        float4 cornerRadii,
        float2 pixel,
        float2 halfSize,
        UiEffectLayerParameters shadow,
        float shapeCoverage,
        float4 destination)
    {
        float shiftedDistance = GetRoundedDistance(cornerRadii, pixel - shadow.Offset, halfSize);
        float depth = max(-shiftedDistance - shadow.Spread, 0f);
        float blur = max(shadow.BlurRadius, 0.0001f);
        float coverage = shapeCoverage * (1f - smoothstep(
            shadow.Width,
            shadow.Width + blur + fwidth(shiftedDistance),
            depth));
        return Over(UiColorMath.Premultiply(shadow.Color, coverage * shadow.Intensity), destination);
    }

    private static float4 ApplyOuterGlow(
        float distance,
        UiEffectLayerParameters outerGlow)
    {
        float edge = Edge(distance);
        // Keep the glow fully covered under the stroke's antialias band. The
        // stroke is composited afterward, so fading glow across the same band
        // would attenuate it twice and leave a dark seam at the contour.
        float outside = smoothstep(-edge, 0f, distance);
        float coverage = OuterGlowCoverage(distance, outerGlow) * outside;
        return UiColorMath.Premultiply(outerGlow.Color, coverage);
    }

    private static float OuterGlowCoverage(float distance, UiEffectLayerParameters outerGlow)
    {
        float blur = max(outerGlow.BlurRadius, 0.0001f);
        return (1f - smoothstep(0f, blur, max(distance - outerGlow.Spread, 0f))) * outerGlow.Intensity;
    }

}
