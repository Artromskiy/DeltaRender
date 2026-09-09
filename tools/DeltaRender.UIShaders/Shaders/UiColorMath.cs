using Delta;

namespace Delta.Render.UIShaders;

internal static class UiColorMath
{
    public static float4 Premultiply(float4 color)
    {
        return new float4(color.w * color.xyz, color.w);
    }

    public static float4 Premultiply(float4 color, float coverage)
    {
        float alpha = color.w * coverage;
        return new float4(alpha * color.xyz, alpha);
    }
}
