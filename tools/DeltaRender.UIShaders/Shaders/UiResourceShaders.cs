using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;
using static Delta.maths;
using static Delta.Shader.intrinsics;

namespace Delta.Render.UIShaders;

public readonly struct SolidLinearGradientParameters
{
    public readonly float4 Rect;
    public readonly float4 GradientLine;
    public readonly float4 CornerRadii;
    public readonly float4 Stop0Color;
    public readonly float4 Stop1Color;
    public readonly float4 Stop2Color;
    public readonly float4 Stop3Color;
    public readonly float4 StopPositions;
    public readonly float StopCount;
    public readonly float4 OutlineColor;
    public readonly float OutlineWidth;

    public SolidLinearGradientParameters(
        float4 rect,
        float4 gradientLine,
        float4 cornerRadii,
        float4 stop0Color,
        float4 stop1Color,
        float4 stop2Color,
        float4 stop3Color,
        float4 stopPositions,
        float stopCount,
        float4 outlineColor,
        float outlineWidth)
    {
        Rect = rect;
        GradientLine = gradientLine;
        CornerRadii = cornerRadii;
        Stop0Color = stop0Color;
        Stop1Color = stop1Color;
        Stop2Color = stop2Color;
        Stop3Color = stop3Color;
        StopPositions = stopPositions;
        StopCount = stopCount;
        OutlineColor = outlineColor;
        OutlineWidth = outlineWidth;
    }
}

[Interstage]
public struct SolidLinearGradientPayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public SegmentRect GradientLine;
    public CornerRadii CornerRadii;
    public VertexColor Stop0Color;
    public VertexColor Stop1Color;
    public VertexColor Stop2Color;
    public VertexColor Stop3Color;
    public VertexColor StopPositions;
    public BorderWidth StopCount;
    public FragmentColor OutlineColor;
    public BorderWidth OutlineWidth;
}

public readonly struct SolidLinearGradientVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidLinearGradientParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidLinearGradientFragmentContext { }

public readonly struct SolidImageRectangleParameters
{
    public readonly float4 Rect;
    public readonly float4 TintColor;
    public readonly float4 UvRect;

    public SolidImageRectangleParameters(float4 rect, float4 tintColor, float4 uvRect)
    {
        Rect = rect;
        TintColor = tintColor;
        UvRect = uvRect;
    }
}

[Interstage]
public struct SolidImageRectanglePayload
{
    public Position Position;
    public Uv0 Uv;
    public VertexColor TintColor;
}

public readonly struct SolidImageRectangleVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<SolidImageRectangleParameters> Instances;

    [PushConstant]
    public readonly UiFrameConstants Frame;
}

public readonly struct SolidImageRectangleFragmentContext
{
    [Layout(0, 1)]
    public readonly SampledTexture2D Image;
}

public static class UiResourceShaders
{
    private static float GetCornerRadius(float4 cornerRadii, float2 pixel, float2 halfSize)
    {
        float2 rightBottom = step(halfSize, pixel);
        float2 topBottom = cornerRadii.xw + rightBottom.x * (cornerRadii.yz - cornerRadii.xw);
        return topBottom.x + rightBottom.y * (topBottom.y - topBottom.x);
    }

    private static float GetRoundedDistance(float4 cornerRadii, float2 pixel, float2 halfSize)
    {
        float radius = GetCornerRadius(cornerRadii, pixel, halfSize);
        float2 q = abs(pixel - halfSize) - halfSize + radius;
        return length(max(q, 0f)) + min(max(q.x, q.y), 0f) - radius;
    }

    private static float Coverage(float distance)
    {
        float edge = max(fwidth(distance) * 0.5f, 0.0001f);
        return 1f - smoothstep(-edge, edge, distance);
    }

    private static float4 Over(float4 source, float4 destination) =>
        source + (1f - source.w) * destination;

    [VertexShader("solid-linear-gradient")]
    public static SolidLinearGradientPayload SolidLinearGradientVertex(in SolidLinearGradientVertexContext context, in SolidLinearGradientPayload input)
    {
        SolidLinearGradientParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidLinearGradientPayload
        {
            Position = new Position(position),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            GradientLine = new SegmentRect(instance.GradientLine),
            CornerRadii = new CornerRadii(instance.CornerRadii),
            Stop0Color = new VertexColor(instance.Stop0Color),
            Stop1Color = new VertexColor(instance.Stop1Color),
            Stop2Color = new VertexColor(instance.Stop2Color),
            Stop3Color = new VertexColor(instance.Stop3Color),
            StopPositions = new VertexColor(instance.StopPositions),
            StopCount = new BorderWidth(instance.StopCount),
            OutlineColor = new FragmentColor(instance.OutlineColor),
            OutlineWidth = new BorderWidth(instance.OutlineWidth),
        };
    }

    [FragmentShader("solid-linear-gradient")]
    public static float4 SolidLinearGradientFragment(in SolidLinearGradientFragmentContext context, in SolidLinearGradientPayload input)
    {
        float2 pixel = input.Rect.Value.xy + input.Uv.Value * input.Rect.Value.zw;
        float2 delta = input.GradientLine.Value.zw - input.GradientLine.Value.xy;
        float t = clamp(dot(pixel - input.GradientLine.Value.xy, delta) / max(dot(delta, delta), 0.0001f), 0f, 1f);
        float4 positions = input.StopPositions.Value;
        float3 availableStops = step(
            new float3(2f, 3f, 4f),
            new float3(input.StopCount.Value));
        float4 after = step(positions, new float4(t));
        float4 color0 = input.Stop0Color.Value;
        float4 color1 = input.Stop1Color.Value;
        float4 color2 = input.Stop2Color.Value;
        float4 color3 = input.Stop3Color.Value;
        float3 stopDelta = max(positions.yzw - positions.xyz, new float3(0.0001f));
        float3 stopAmount = clamp(
            (new float3(t) - positions.xyz) / stopDelta,
            new float3(0f),
            new float3(1f));
        float4 color01 = color0 + (color1 - color0) * stopAmount.x;
        float4 color12 = color1 + (color2 - color1) * stopAmount.y;
        float4 color23 = color2 + (color3 - color2) * stopAmount.z;
        float use01 = availableStops.x * after.x * (1f - after.y);
        float use12 = availableStops.x * after.y * (1f - availableStops.y * after.z);
        float use23 = availableStops.y * after.z * (1f - availableStops.z * after.w);
        float use3 = availableStops.z * after.w;
        float4 color = color0 + use01 * (color01 - color0) + use12 * (color12 - color0) +
            use23 * (color23 - color0) + use3 * (color3 - color0);

        float2 localPixel = input.Uv.Value * input.Rect.Value.zw;
        float distance = GetRoundedDistance(input.CornerRadii.Value, localPixel, input.Rect.Value.zw * 0.5f);
        float outerCoverage = Coverage(distance);
        float innerCoverage = min(Coverage(distance + input.OutlineWidth.Value), outerCoverage);
        float4 fill = UiColorMath.Premultiply(color);
        float4 outlinedFill = Over(UiColorMath.Premultiply(input.OutlineColor.Value), fill);
        return innerCoverage * fill + (outerCoverage - innerCoverage) * outlinedFill;
    }

    [VertexShader("solid-image")]
    public static SolidImageRectanglePayload SolidImageRectangleVertex(in SolidImageRectangleVertexContext context, in SolidImageRectanglePayload input)
    {
        SolidImageRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        float4 position = QuadGeometry.ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidImageRectanglePayload
        {
            Position = new Position(position),
            Uv = new Uv0(instance.UvRect.xy + local * instance.UvRect.zw),
            TintColor = new VertexColor(instance.TintColor),
        };
    }

    [FragmentShader("solid-image")]
    public static float4 SolidImageRectangleFragment(in SolidImageRectangleFragmentContext context, in SolidImageRectanglePayload input)
    {
        float4 image = context.Image.Sample<float2, float4>(input.Uv.Value);
        float4 tint = input.TintColor.Value;
        float alpha = image.w * tint.w;
        return new float4(alpha * image.xyz * tint.xyz, alpha);
    }
}
