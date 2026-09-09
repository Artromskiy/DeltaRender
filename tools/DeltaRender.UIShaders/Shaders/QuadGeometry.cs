using Delta;

namespace Delta.Render.UIShaders;

internal static class QuadGeometry
{
    public static float2 GetLocal(uint vertexIndex)
    {
        float x = (22u >> (int)vertexIndex) & 1u;
        float y = (52u >> (int)vertexIndex) & 1u;
        return new float2(x, y);
    }

    public static float4 ToClipPosition(float4 rect, float2 local, float2 resolution) =>
        new float4(2f * (rect.xy + local * rect.zw) / resolution - 1f, 0f, 1f);

    public static float4 ToClipPosition(float2 pixel, float2 resolution) =>
        new float4(2f * pixel / resolution - 1f, 0f, 1f);
}
