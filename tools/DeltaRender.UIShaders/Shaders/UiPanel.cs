using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;

namespace Delta.Render.UIShaders;

public static class UiPanel
{
    public struct Parameters
    {
        public float2 Resolution = default;
        public float4 Rect = default;
        public float4 Color = default;

        public Parameters()
        {
        }
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
        var vertexIndex = ShaderBuiltins.VertexIndex;
        var local = QuadGeometry.GetLocal(vertexIndex);

        var pixel = new float2(
            context.Constants.Rect.x + local.x * context.Constants.Rect.z,
            context.Constants.Rect.y + local.y * context.Constants.Rect.w);
        var clip = new float2(
            pixel.x / context.Constants.Resolution.x * 2f - 1f,
            pixel.y / context.Constants.Resolution.y * 2f - 1f);
        return new VertexOutput { Position = new Position(new float4(clip.x, clip.y, 0f, 1f)) };
    }

    [FragmentShader]
    public static float4 Fragment(in FragmentContext context, in VertexOutput input) => context.Constants.Color;
}
