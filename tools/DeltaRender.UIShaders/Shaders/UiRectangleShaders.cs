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
}
