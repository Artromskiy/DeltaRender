using Delta.Maths;
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
        [Interstage]
        public readonly VertexOutput Vertex;

        [PushConstant]
        public readonly Parameters Constants;
    }

    public readonly struct FragmentContext
    {
        [Interstage]
        public readonly VertexOutput Fragment;

        [PushConstant]
        public readonly Parameters Constants;
    }

    [VertexShader]
    public static VertexOutput Vertex(in VertexContext context)
    {
        var vertexIndex = ShaderBuiltins.VertexIndex;
        var local = new float2(0f, 0f);
        if (vertexIndex == 1u || vertexIndex == 2u || vertexIndex == 4u)
        {
            local = new float2(1f, local.y);
        }
        if (vertexIndex == 2u || vertexIndex == 4u || vertexIndex == 5u)
        {
            local = new float2(local.x, 1f);
        }

        var pixel = new float2(
            context.Constants.Rect.x + local.x * context.Constants.Rect.z,
            context.Constants.Rect.y + local.y * context.Constants.Rect.w);
        var clip = new float2(
            pixel.x / context.Constants.Resolution.x * 2f - 1f,
            pixel.y / context.Constants.Resolution.y * 2f - 1f);
        return new VertexOutput { Position = new Position(new float4(clip.x, clip.y, 0f, 1f)) };
    }

    [FragmentShader]
    public static float4 Fragment(in FragmentContext context) => context.Constants.Color;
}
