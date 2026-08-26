using System.Diagnostics.CodeAnalysis;
using DeltaMaths;
using DeltaShader.Abstractions;

namespace DeltaRender.UiShaders;

public static class UiPanel
{
    /// <summary>Push-constant values shared by the generated panel stages.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "The nested type is the public push-constant ABI emitted by DeltaShader.")]
    [SuppressMessage("Usage", "CA1815:Override equals and operator equals on value types", Justification = "The shader ABI parameter struct is not used as a managed value key.")]
    public struct Parameters
    {
        [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Public fields are consumed as the generated shader push-constant ABI.")]
        /// <summary>Viewport resolution in pixels.</summary>
        public float2 Resolution;
        [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Public fields are consumed as the generated shader push-constant ABI.")]
        /// <summary>Panel rectangle in pixel coordinates.</summary>
        public float4 Rect;
        [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Public fields are consumed as the generated shader push-constant ABI.")]
        /// <summary>Panel premultiplied color.</summary>
        public float4 Color;
    }

    /// <summary>Emits the panel triangle-list vertex position.</summary>
    [VertexShader]
    public static void Vertex(
        [VertexIndex] uint vertexIndex,
        [PushConstant] Parameters parameters,
        [Position] out float4 position)
    {
        position = default;
        var local = new float2(0f, 0f);
        if (vertexIndex == 1u || vertexIndex == 2u || vertexIndex == 4u)
        {
            local.x = 1f;
        }
        if (vertexIndex == 2u || vertexIndex == 4u || vertexIndex == 5u)
        {
            local.y = 1f;
        }
        var pixel = new float2(
            parameters.Rect.x + local.x * parameters.Rect.z,
            parameters.Rect.y + local.y * parameters.Rect.w);
        var clip = new float2(
            pixel.x / parameters.Resolution.x * 2f - 1f,
            1f - pixel.y / parameters.Resolution.y * 2f);
        position = new float4(clip.x, clip.y, 0f, 1f);
    }

    /// <summary>Emits the panel fragment color.</summary>
    [FragmentShader]
    public static void Fragment(
        [PushConstant] Parameters parameters,
        [FragmentColor] out float4 color)
    {
        color = parameters.Color;
    }
}
