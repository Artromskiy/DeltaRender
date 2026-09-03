using Delta;
using static Delta.maths;
using Delta.Shader;

namespace Delta.Render.SquareShaders;

public static class Square
{
    public struct PushConstants
    {
        public float2 Resolution;
        public float Time;

        public PushConstants()
        {
        }
    }

    [Interstage]
    public struct Varying
    {
        public Position Position;
        public Uv0 Uv;
        public VertexColor Color;

        public Varying()
        {
        }
    }

    public readonly struct VertexContext
    {
        [PushConstant]
        public readonly PushConstants Constants;
    }

    public readonly struct FragmentContext
    {
        [PushConstant]
        public readonly PushConstants Constants;
    }

    [VertexShader("square")]
    public static Varying SquareVertex(in VertexContext context, in Varying input)
    {
        var vertexIndex = ShaderBuiltins.VertexIndex;
        var top = new float4(0.58f, 0.58f, 0.58f, 1f);
        var left = new float4(0.30f, 0.30f, 0.30f, 1f);
        var right = new float4(0.44f, 0.44f, 0.44f, 1f);

        if (vertexIndex == 0u)
        {
            return new Varying { Position = new float4(-0.52f, 0.30f, 0f, 1f), Uv = new float2(0f, 0f), Color = top };
        }

        if (vertexIndex == 1u)
        {
            return new Varying { Position = new float4(0f, 0.62f, 0f, 1f), Uv = new float2(0.5f, 1f), Color = top };
        }

        if (vertexIndex == 2u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(0.5f, 0f), Color = top };
        }

        if (vertexIndex == 3u)
        {
            return new Varying { Position = new float4(0f, 0.62f, 0f, 1f), Uv = new float2(0.5f, 1f), Color = top };
        }

        if (vertexIndex == 4u)
        {
            return new Varying { Position = new float4(0.52f, 0.30f, 0f, 1f), Uv = new float2(1f, 0f), Color = top };
        }

        if (vertexIndex == 5u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(0.5f, 0f), Color = top };
        }

        if (vertexIndex == 6u)
        {
            return new Varying { Position = new float4(-0.52f, 0.30f, 0f, 1f), Uv = new float2(0f, 1f), Color = left };
        }

        if (vertexIndex == 7u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(1f, 1f), Color = left };
        }

        if (vertexIndex == 8u)
        {
            return new Varying { Position = new float4(-0.52f, -0.30f, 0f, 1f), Uv = new float2(0f, 0f), Color = left };
        }

        if (vertexIndex == 9u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(1f, 1f), Color = left };
        }

        if (vertexIndex == 10u)
        {
            return new Varying { Position = new float4(0f, -0.62f, 0f, 1f), Uv = new float2(1f, 0f), Color = left };
        }

        if (vertexIndex == 11u)
        {
            return new Varying { Position = new float4(-0.52f, -0.30f, 0f, 1f), Uv = new float2(0f, 0f), Color = left };
        }

        if (vertexIndex == 12u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(0f, 1f), Color = right };
        }

        if (vertexIndex == 13u)
        {
            return new Varying { Position = new float4(0.52f, 0.30f, 0f, 1f), Uv = new float2(1f, 1f), Color = right };
        }

        if (vertexIndex == 14u)
        {
            return new Varying { Position = new float4(0.52f, -0.30f, 0f, 1f), Uv = new float2(1f, 0f), Color = right };
        }

        if (vertexIndex == 15u)
        {
            return new Varying { Position = new float4(0f, -0.02f, 0f, 1f), Uv = new float2(0f, 1f), Color = right };
        }

        if (vertexIndex == 16u)
        {
            return new Varying { Position = new float4(0f, -0.62f, 0f, 1f), Uv = new float2(0f, 0f), Color = right };
        }

        return new Varying { Position = new float4(0.52f, -0.30f, 0f, 1f), Uv = new float2(1f, 0f), Color = right };
    }

    [FragmentShader("square")]
    public static float4 SquareFragment(in FragmentContext context, in Varying input)
    {
        return input.Color;
    }
}
