using Delta;
using Delta.Graphics.Semantics;
using static Delta.maths;
using Delta.Shader;

namespace Delta.Render.SquareShaders;

public static class Square
{
    public struct PushConstants
    {
        public float2 Resolution;
        public float Time;
    }

    [Interstage]
    public struct Varying
    {
        public Position Position;
        public Uv0 Uv;
        public VertexColor Color;
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
        var position = new float2(
            0.52f * (Bit(155664u, vertexIndex) - Bit(2369u, vertexIndex)),
            0.30f * Bit(8257u, vertexIndex) +
            0.62f * Bit(10u, vertexIndex) -
            0.02f * Bit(37540u, vertexIndex) -
            0.30f * Bit(149760u, vertexIndex) -
            0.62f * Bit(66560u, vertexIndex));
        var uv = new float2(
            0.5f * Bit(46u, vertexIndex) + Bit(157312u, vertexIndex),
            Bit(45770u, vertexIndex));
        var colorValue = 0.58f * Bit(63u, vertexIndex) +
            0.30f * Bit(4032u, vertexIndex) +
            0.44f * Bit(258048u, vertexIndex);
        var color = new float4(colorValue, colorValue, colorValue, 1f);

        return new Varying
        {
            Position = new float4(position, 0f, 1f),
            Uv = uv,
            Color = color
        };
    }

    private static float Bit(uint mask, uint vertexIndex) =>
        (mask >> (int)vertexIndex) & 1u;

    [FragmentShader("square")]
    public static float4 SquareFragment(in FragmentContext context, in Varying input)
    {
        return input.Color;
    }
}
