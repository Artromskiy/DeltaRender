using System.Diagnostics.CodeAnalysis;
using Delta.Maths;
using Delta.Shader;

namespace Delta.Render.FullscreenShaders;

public static class FullscreenUi
{
    [SuppressMessage("Design", "CA1051", Justification = "Public fields are the declared shader-visible push-constant ABI.")]
    [SuppressMessage("Design", "CA1815", Justification = "Field-only shader ABI record; equality is not part of the serialized layout contract.")]
    public struct UiPushConstants
    {
        public float2 Resolution = default;
        public float Time = default;

        public UiPushConstants()
        {
        }
    }

    [VertexShader]
    public static void Vertex(
        [VertexIndex] uint vertexIndex,
        [Position] out float4 position,
        [ShaderVarying(0)] out float2 uv)
    {
        position = default;
        uv = default;

        if (vertexIndex == 0u)
        {
            position = new float4(-1f, -1f, 0f, 1f);
            uv = new float2(0f, 0f);
        }
        if (vertexIndex == 1u)
        {
            position = new float4(3f, -1f, 0f, 1f);
            uv = new float2(2f, 0f);
        }
        if (vertexIndex == 2u)
        {
            position = new float4(-1f, 3f, 0f, 1f);
            uv = new float2(0f, 2f);
        }
    }

    [FragmentShader]
    public static void Fragment(
        [FragmentCoord] float2 fragmentCoord,
        [PushConstant] UiPushConstants constants,
        [ShaderVarying(0)] float2 uv,
        [FragmentColor] out float4 color)
    {
        var p = (fragmentCoord / constants.Resolution) * 2f - new float2(1f, 1f);
        var halfSize = new float2(0.55f, 0.32f);
        var q = maths.abs(p) - halfSize + 0.12f;
        var distance = maths.length(maths.max(q, new float2(0f, 0f))) + maths.min(maths.max(q.x, q.y), 0f) - 0.12f;
        var edge = ShaderIntrinsics.fwidth(distance);
        var mask = 1f - maths.smoothStep(-edge, edge, distance);
        var tint = 0.5f + 0.5f * maths.sin(constants.Time);
        color = new float4(0.08f + 0.2f * mask, 0.12f + 0.4f * mask, 0.2f + 0.5f * tint * mask, 1f);
    }
}
