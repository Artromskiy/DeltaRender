using Delta.Maths;
using Delta.Shader.Abstractions;

namespace Delta.Render.UiShaders;

public static class UiPanel
{
    public struct Parameters
    {
        public float2 Resolution;
        public float4 Rect;
        public float4 Color;
    }

    [VertexShader]
    public static void Vertex(
        [VertexIndex] uint vertexIndex,
        [PushConstant] Parameters parameters,
        [Position] out float4 position)
    {
        position = default;
        var local = new float2(0f, 0f);
        if (vertexIndex == 1u || vertexIndex == 4u)
        {
            local = new float2(1f, 0f);
        }
        if (vertexIndex == 2u || vertexIndex == 4u)
        {
            local = new float2(1f, 1f);
        }
        if (vertexIndex == 3u || vertexIndex == 5u)
        {
            local = new float2(0f, 1f);
        }

        var pixel = new float2(
            parameters.Rect.x + local.x * parameters.Rect.z,
            parameters.Rect.y + local.y * parameters.Rect.w);
        var clip = new float2(
            pixel.x / parameters.Resolution.x * 2f - 1f,
            1f - pixel.y / parameters.Resolution.y * 2f);
        position = new float4(clip.x, clip.y, 0f, 1f);
    }

    [FragmentShader]
    public static void Fragment(
        [PushConstant] Parameters parameters,
        [FragmentColor] out float4 color)
    {
        color = parameters.Color;
    }
}
