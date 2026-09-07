using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;

namespace Delta.Render.Text;

public struct GlyphInstance
{
    public float2 PixelMin = default;
    public float2 PixelMax = default;
    public float4 UvRect = default;
    public float4 Color = default;

    public GlyphInstance()
    {
    }
}

public struct TextParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float4 OutlineColor = default;
    public float OutlineWidth = default;
    public float DistanceRange = default;

    public TextParameters()
    {
    }
}

[Interstage]
public struct TextVarying
{
    public Position Position;
    public Uv0 Uv;
    public VertexColor GlyphColor;
}

public readonly struct TextVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public readonly struct SdfTextFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public readonly struct MsdfTextFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public static class TextShaders
{
    [VertexShader("sdf-text")]
    public static TextVarying SdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);

        if (vertexIndex == 0u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color
            };
        }

        return new TextVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color
        };
    }

    [FragmentShader("sdf-text")]
    public static float4 SdfTextFragment(in SdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outlineWidth = maths.max(context.Parameters.OutlineWidth, 0f);
        var outerCoverage = maths.smoothstep(-outlineWidth - edge, -outlineWidth + edge, signedDistance);
        var outlineContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.OutlineColor * input.GlyphColor.Value * outlineContribution;
    }

    [VertexShader("msdf-text")]
    public static TextVarying MsdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);

        if (vertexIndex == 0u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color
            };
        }

        return new TextVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color
        };
    }

    [FragmentShader("msdf-text")]
    public static float4 MsdfTextFragment(in MsdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = median - 0.5f;
        signedDistance *= context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outlineWidth = maths.max(context.Parameters.OutlineWidth, 0f);
        var outerCoverage = maths.smoothstep(-outlineWidth - edge, -outlineWidth + edge, signedDistance);
        var outlineContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.OutlineColor * input.GlyphColor.Value * outlineContribution;
    }
}
