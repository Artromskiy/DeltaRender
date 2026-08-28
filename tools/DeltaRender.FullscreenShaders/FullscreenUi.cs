using System.Diagnostics.CodeAnalysis;
using Delta.Maths;
using Delta.Shader;

namespace Delta.Render.FullscreenShaders;

public static class FullscreenUi
{
    [SuppressMessage("Design", "CA1051", Justification = "Shader ABI fields are emitted by Delta.Shader.")]
    public struct UiPushConstants
    {
        public float2 Resolution = default;
        public float Time = default;

        public UiPushConstants()
        {
        }
    }

    [Interstage]
    public struct UiVarying
    {
        [Position]
        public float4 Position;
        public float2 Uv;

        public UiVarying()
        {
        }
    }

    public readonly struct VertexContext
    {
        [Interstage]
        public readonly UiVarying Vertex;

        [PushConstant]
        public readonly UiPushConstants Constants;
    }

    public readonly struct FragmentContext
    {
        [Interstage]
        public readonly UiVarying Fragment;

        [PushConstant]
        public readonly UiPushConstants Constants;
    }

    [VertexShader("fullscreen-ui")]
    public static UiVarying Vertex(in VertexContext context)
    {
        var vertexIndex = ShaderBuiltins.VertexIndex;
        if (vertexIndex == 0u)
        {
            return new UiVarying { Position = new float4(-1f, -1f, 0f, 1f), Uv = new float2(0f, 0f) };
        }
        if (vertexIndex == 1u)
        {
            return new UiVarying { Position = new float4(3f, -1f, 0f, 1f), Uv = new float2(2f, 0f) };
        }

        return new UiVarying { Position = new float4(-1f, 3f, 0f, 1f), Uv = new float2(0f, 2f) };
    }

    [FragmentShader("fullscreen-ui")]
    public static float4 Fragment(in FragmentContext context)
    {
        var fragmentCoord = new float2(ShaderBuiltins.FragmentCoord.X, ShaderBuiltins.FragmentCoord.Y);
        var p = (fragmentCoord / context.Constants.Resolution) * 2f - new float2(1f, 1f);
        var halfSize = new float2(0.55f, 0.32f);
        var q = maths.abs(p) - halfSize + 0.12f;
        var distance = maths.length(maths.max(q, new float2(0f, 0f))) + maths.min(maths.max(q.x, q.y), 0f) - 0.12f;
        var edge = ShaderIntrinsics.fwidth(distance);
        var mask = 1f - maths.smoothStep(-edge, edge, distance);
        var tint = 0.5f + 0.5f * maths.sin(context.Constants.Time);
        return new float4(0.08f + 0.2f * mask, 0.12f + 0.4f * mask, 0.2f + 0.5f * tint * mask, 1f);
    }
}
