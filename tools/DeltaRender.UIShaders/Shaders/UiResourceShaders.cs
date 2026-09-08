using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;
using static Delta.maths;

namespace Delta.Render.UIShaders;

public readonly struct SolidLinearGradientParameters
{
    public readonly float4 Rect;
    public readonly float4 GradientLine;
    public readonly float4 Stop0Color;
    public readonly float4 Stop1Color;
    public readonly float4 Stop2Color;
    public readonly float4 Stop3Color;
    public readonly float4 StopPositions;
    public readonly float StopCount;

    public SolidLinearGradientParameters(float4 rect, float4 gradientLine, float4 stop0Color, float4 stop1Color, float4 stop2Color, float4 stop3Color, float4 stopPositions, float stopCount)
    {
        Rect = rect;
        GradientLine = gradientLine;
        Stop0Color = stop0Color;
        Stop1Color = stop1Color;
        Stop2Color = stop2Color;
        Stop3Color = stop3Color;
        StopPositions = stopPositions;
        StopCount = stopCount;
    }
}

[Interstage]
public struct SolidLinearGradientPayload
{
    public Position Position;
    public Uv0 Uv;
    public SegmentRect Rect;
    public SegmentRect GradientLine;
    public VertexColor Stop0Color;
    public VertexColor Stop1Color;
    public VertexColor Stop2Color;
    public VertexColor Stop3Color;
    public VertexColor StopPositions;
    public BorderWidth StopCount;
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
    private static float2 GetQuadLocal(uint vertexIndex)
        => vertexIndex switch
        {
            0u => new float2(0f, 0f),
            1u => new float2(1f, 0f),
            2u => new float2(1f, 1f),
            3u => new float2(0f, 0f),
            4u => new float2(1f, 1f),
            _ => new float2(0f, 1f),
        };

    private static float2 ToClipPosition(float4 rect, float2 local, float2 resolution)
    {
        float2 pixel = rect.xy + local * rect.zw;
        return (pixel / resolution) * 2f - 1f;
    }

    [VertexShader("solid-linear-gradient")]
    public static SolidLinearGradientPayload SolidLinearGradientVertex(in SolidLinearGradientVertexContext context, in SolidLinearGradientPayload input)
    {
        SolidLinearGradientParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidLinearGradientPayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(local),
            Rect = new SegmentRect(instance.Rect),
            GradientLine = new SegmentRect(instance.GradientLine),
            Stop0Color = new VertexColor(instance.Stop0Color),
            Stop1Color = new VertexColor(instance.Stop1Color),
            Stop2Color = new VertexColor(instance.Stop2Color),
            Stop3Color = new VertexColor(instance.Stop3Color),
            StopPositions = new VertexColor(instance.StopPositions),
            StopCount = new BorderWidth(instance.StopCount),
        };
    }

    [FragmentShader("solid-linear-gradient")]
    public static float4 SolidLinearGradientFragment(in SolidLinearGradientFragmentContext context, in SolidLinearGradientPayload input)
    {
        float2 pixel = input.Rect.Value.xy + input.Uv.Value * input.Rect.Value.zw;
        float2 delta = input.GradientLine.Value.zw - input.GradientLine.Value.xy;
        float t = clamp(dot(pixel - input.GradientLine.Value.xy, delta) / max(dot(delta, delta), 0.0001f), 0f, 1f);
        float4 color = input.Stop3Color.Value;
        if (input.StopCount.Value <= 1f || t <= input.StopPositions.Value.x)
        {
            color = input.Stop0Color.Value;
        }
        else if (t <= input.StopPositions.Value.y)
        {
            float amount = (t - input.StopPositions.Value.x) / max(input.StopPositions.Value.y - input.StopPositions.Value.x, 0.0001f);
            color = input.Stop0Color.Value + (input.Stop1Color.Value - input.Stop0Color.Value) * amount;
        }
        else if (input.StopCount.Value <= 2f || t <= input.StopPositions.Value.z)
        {
            float amount = (t - input.StopPositions.Value.y) / max(input.StopPositions.Value.z - input.StopPositions.Value.y, 0.0001f);
            color = input.Stop1Color.Value + (input.Stop2Color.Value - input.Stop1Color.Value) * amount;
        }
        else if (input.StopCount.Value <= 3f || t <= input.StopPositions.Value.w)
        {
            float amount = (t - input.StopPositions.Value.z) / max(input.StopPositions.Value.w - input.StopPositions.Value.z, 0.0001f);
            color = input.Stop2Color.Value + (input.Stop3Color.Value - input.Stop2Color.Value) * amount;
        }

        return new float4(color.xyz * color.w, color.w);
    }

    [VertexShader("solid-image")]
    public static SolidImageRectanglePayload SolidImageRectangleVertex(in SolidImageRectangleVertexContext context, in SolidImageRectanglePayload input)
    {
        SolidImageRectangleParameters instance = context.Instances[ShaderBuiltins.InstanceIndex];
        float2 local = GetQuadLocal(ShaderBuiltins.VertexIndex);
        float2 clip = ToClipPosition(instance.Rect, local, context.Frame.Resolution);
        return new SolidImageRectanglePayload
        {
            Position = new float4(clip.x, clip.y, 0f, 1f),
            Uv = new Uv0(instance.UvRect.xy + local * instance.UvRect.zw),
            TintColor = new VertexColor(instance.TintColor),
        };
    }

    [FragmentShader("solid-image")]
    public static float4 SolidImageRectangleFragment(in SolidImageRectangleFragmentContext context, in SolidImageRectanglePayload input)
    {
        float4 image = context.Image.Sample<float2, float4>(input.Uv.Value);
        float4 tint = input.TintColor.Value;
        return new float4(image.xyz * tint.xyz * image.w * tint.w, image.w * tint.w);
    }
}
