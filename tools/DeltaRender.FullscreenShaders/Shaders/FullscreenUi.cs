using Delta;
using Delta.Graphics.Semantics;
using static Delta.maths;
using Delta.Shader;

namespace Delta.Render.FullscreenShaders;

public static class FullscreenUi
{
    public struct UiPushConstants
    {
        public float2 Resolution;
        public float Time;
    }

    [Interstage]
    public struct UiVarying
    {
        public Position Position;
        public Uv0 Uv;
    }

    public readonly struct VertexContext
    {
        [PushConstant]
        public readonly UiPushConstants Constants;
    }

    public readonly struct FragmentContext
    {
        [PushConstant]
        public readonly UiPushConstants Constants;
    }

    [VertexShader("fullscreen-ui")]
    public static UiVarying Vertex(in VertexContext context, in UiVarying input)
    {
        var local = FullscreenTriangleGeometry.GetLocal(ShaderBuiltins.VertexIndex);
        return new UiVarying
        {
            Position = new Position(FullscreenTriangleGeometry.GetPosition(local)),
            Uv = FullscreenTriangleGeometry.GetUv(local)
        };
    }

    [FragmentShader("fullscreen-ui")]
    public static float4 Fragment(in FragmentContext context, in UiVarying input)
    {
        var fragmentCoord = new float2(ShaderBuiltins.FragmentCoord.X, ShaderBuiltins.FragmentCoord.Y);
        var p = 2f * fragmentCoord / context.Constants.Resolution - new float2(1f);
        var halfSize = new float2(0.55f, 0.32f);
        var q = maths.abs(p) - halfSize + 0.12f;
        var distance = maths.length(maths.max(q, new float2(0f))) + maths.min(maths.max(q.x, q.y), 0f) - 0.12f;
        var edge = intrinsics.fwidth(distance);
        var mask = 1f - maths.smoothstep(-edge, edge, distance);
        var tint = 0.5f + 0.5f * maths.sin(context.Constants.Time);
        return new float4(0.08f + 0.2f * mask, 0.12f + 0.4f * mask, 0.2f + 0.5f * tint * mask, 1f);
    }
}
