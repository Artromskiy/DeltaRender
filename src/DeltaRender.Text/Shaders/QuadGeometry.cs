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
}
