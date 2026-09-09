using Delta;

namespace Delta.Render.Text;

internal static class QuadGeometry
{
    public static float2 GetLocal(uint vertexIndex)
    {
        float x = (22u >> (int)vertexIndex) & 1u;
        float y = (52u >> (int)vertexIndex) & 1u;
        return new float2(x, y);
    }

    public static float4 ToClipPosition(float2 pixel, float2 resolution) =>
        new float4(2f * pixel / resolution - 1f, 0f, 1f);
}
