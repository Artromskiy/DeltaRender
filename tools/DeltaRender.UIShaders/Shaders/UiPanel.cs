using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;

namespace Delta.Render.UIShaders;

public static class UiPanel
{
    public struct Parameters
    {
        public float2 Resolution;
        public float4 Rect;
        public float4 Color;
    }

    [Interstage]
    public struct VertexOutput
    {
        public Position Position;

    }

    public readonly struct VertexContext
    {
        [PushConstant]
        public readonly Parameters Constants;
    }

    public readonly struct FragmentContext
    {
        [PushConstant]
        public readonly Parameters Constants;
    }

    [VertexShader]
    public static VertexOutput Vertex(in VertexContext context, in VertexOutput input)
    {
        var local = QuadGeometry.GetLocal(ShaderBuiltins.VertexIndex);

        var position = QuadGeometry.ToClipPosition(
            context.Constants.Rect,
            local,
            context.Constants.Resolution);
        return new VertexOutput { Position = new Position(position) };
    }

    [FragmentShader]
    public static float4 Fragment(in FragmentContext context, in VertexOutput input) => context.Constants.Color;
}
