using Delta;

namespace Delta.Render.FullscreenShaders;

internal static class FullscreenTriangleGeometry
{
    public static float2 GetLocal(uint vertexIndex)
    {
        float x = (2u >> (int)vertexIndex) & 1u;
        float y = (4u >> (int)vertexIndex) & 1u;
        return new float2(x, y);
    }

    public static float4 GetPosition(float2 local) =>
        new float4(4f * local - 1f, 0f, 1f);

    public static float2 GetUv(float2 local) => 2f * local;
}
