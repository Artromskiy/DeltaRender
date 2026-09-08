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
    public float4 StrokeColor = default;
    public float StrokeWidth = default;
    public float DistanceRange = default;

    public TextParameters()
    {
    }
}

public struct TextStrokeParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float4 StrokeColor = default;
    public float StrokeWidth = default;
    public float DistanceRange = default;

    public TextStrokeParameters()
    {
    }
}

public struct TextOuterGlowParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float DistanceRange = default;
    public float4 OuterGlowColor = default;
    public float OuterGlowRadius = default;
    public float OuterGlowIntensity = default;

    public TextOuterGlowParameters()
    {
    }
}

public struct TextEffectParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float4 StrokeColor = default;
    public float StrokeWidth = default;
    public float DistanceRange = default;
    public float4 OuterGlowColor = default;
    public float OuterGlowRadius = default;
    public float OuterGlowIntensity = default;
    public float4 OuterShadowColor = default;
    public float2 OuterShadowOffset = default;
    public float OuterShadowWidth = default;
    public float OuterShadowBlurRadius = default;
    public float OuterShadowSpread = default;
    public float OuterShadowIntensity = default;

    public TextEffectParameters()
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

public readonly struct GlyphPixelSize
{
    public readonly float2 Value;

    public GlyphPixelSize(float2 value) => Value = value;
}

public readonly struct GlyphUvSize
{
    public readonly float2 Value;

    public GlyphUvSize(float2 value) => Value = value;
}

[Interstage]
public struct TextEffectVarying
{
    public Position Position;
    public Uv0 Uv;
    public VertexColor GlyphColor;
    public GlyphPixelSize PixelSize;
    public GlyphUvSize UvSize;
}

public readonly struct TextVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public readonly struct TextStrokeVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextStrokeParameters Parameters;
}

public readonly struct TextOuterGlowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterGlowParameters Parameters;
}

public readonly struct SdfTextFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public readonly struct SdfTextStrokeFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextStrokeParameters Parameters;
}

public readonly struct MsdfTextStrokeVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextStrokeParameters Parameters;
}

public readonly struct MsdfTextStrokeFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextStrokeParameters Parameters;
}

public readonly struct SdfTextOuterGlowFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterGlowParameters Parameters;
}

public readonly struct MsdfTextOuterGlowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterGlowParameters Parameters;
}

public readonly struct MsdfTextOuterGlowFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterGlowParameters Parameters;
}

public struct TextOuterShadowParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float DistanceRange = default;
    public float4 OuterShadowColor = default;
    public float2 OuterShadowOffset = default;
    public float OuterShadowWidth = default;
    public float OuterShadowBlurRadius = default;
    public float OuterShadowSpread = default;
    public float OuterShadowIntensity = default;

    public TextOuterShadowParameters()
    {
    }
}

public readonly struct SdfTextOuterShadowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterShadowParameters Parameters;
}

public readonly struct SdfTextOuterShadowFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterShadowParameters Parameters;
}

public readonly struct MsdfTextOuterShadowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterShadowParameters Parameters;
}

public readonly struct MsdfTextOuterShadowFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterShadowParameters Parameters;
}

public struct TextStrokeOuterShadowOuterGlowParameters
{
    public float2 Resolution = default;
    public float4 TextColor = default;
    public float4 StrokeColor = default;
    public float StrokeWidth = default;
    public float DistanceRange = default;
    public float4 OuterGlowColor = default;
    public float OuterGlowRadius = default;
    public float OuterGlowIntensity = default;
    public float4 OuterShadowColor = default;
    public float2 OuterShadowOffset = default;
    public float OuterShadowWidth = default;
    public float OuterShadowBlurRadius = default;
    public float OuterShadowSpread = default;
    public float OuterShadowIntensity = default;

    public TextStrokeOuterShadowOuterGlowParameters()
    {
    }
}

public readonly struct SdfTextStrokeOuterShadowOuterGlowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextStrokeOuterShadowOuterGlowParameters Parameters;
}

public readonly struct SdfTextStrokeOuterShadowOuterGlowFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextStrokeOuterShadowOuterGlowParameters Parameters;
}

public readonly struct MsdfTextStrokeOuterShadowOuterGlowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextStrokeOuterShadowOuterGlowParameters Parameters;
}

public readonly struct MsdfTextStrokeOuterShadowOuterGlowFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextStrokeOuterShadowOuterGlowParameters Parameters;
}

public readonly struct MsdfTextFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public readonly struct TextEffectVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextEffectParameters Parameters;
}

public readonly struct SdfTextEffectFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextEffectParameters Parameters;
}

public readonly struct MsdfTextEffectFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextEffectParameters Parameters;
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
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.StrokeColor * input.GlyphColor.Value * strokeContribution;
    }

    [VertexShader("sdf-text-stroke")]
    public static TextVarying SdfTextStrokeVertex(
        in TextStrokeVertexContext context,
        in TextVarying input)
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

    [FragmentShader("sdf-text-stroke")]
    public static float4 SdfTextStrokeFragment(
        in SdfTextStrokeFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.StrokeColor * input.GlyphColor.Value * strokeContribution;
    }

    [VertexShader("msdf-text-stroke")]
    public static TextVarying MsdfTextStrokeVertex(
        in MsdfTextStrokeVertexContext context,
        in TextVarying input)
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

    [FragmentShader("msdf-text-stroke")]
    public static float4 MsdfTextStrokeFragment(
        in MsdfTextStrokeFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.StrokeColor * input.GlyphColor.Value * strokeContribution;
    }

    [VertexShader("sdf-text-outer-glow")]
    public static TextEffectVarying SdfTextOuterGlowVertex(
        in TextOuterGlowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("sdf-text-outer-glow")]
    public static float4 SdfTextOuterGlowFragment(
        in SdfTextOuterGlowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - fillCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("sdf-text-outer-shadow")]
    public static TextEffectVarying SdfTextOuterShadowVertex(
        in SdfTextOuterShadowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("sdf-text-outer-shadow")]
    public static float4 SdfTextOuterShadowFragment(
        in SdfTextOuterShadowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowDistance = (shadowTexel.x - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - fillCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("msdf-text-outer-shadow")]
    public static TextEffectVarying MsdfTextOuterShadowVertex(
        in MsdfTextOuterShadowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("msdf-text-outer-shadow")]
    public static float4 MsdfTextOuterShadowFragment(
        in MsdfTextOuterShadowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowMedian = maths.max(
            maths.min(shadowTexel.x, shadowTexel.y),
            maths.min(maths.max(shadowTexel.x, shadowTexel.y), shadowTexel.z));
        var shadowDistance = (shadowMedian - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - fillCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("msdf-text-outer-glow")]
    public static TextEffectVarying MsdfTextOuterGlowVertex(
        in MsdfTextOuterGlowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("msdf-text-outer-glow")]
    public static float4 MsdfTextOuterGlowFragment(
        in MsdfTextOuterGlowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - fillCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
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
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return context.Parameters.TextColor * input.GlyphColor.Value * fillCoverage +
            context.Parameters.StrokeColor * input.GlyphColor.Value * strokeContribution;
    }

    [VertexShader("sdf-text-stroke-outer-shadow-outer-glow")]
    public static TextEffectVarying SdfTextStrokeOuterShadowOuterGlowVertex(
        in SdfTextStrokeOuterShadowOuterGlowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("sdf-text-stroke-outer-shadow-outer-glow")]
    public static float4 SdfTextStrokeOuterShadowOuterGlowFragment(
        in SdfTextStrokeOuterShadowOuterGlowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, strokeWidth + edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -strokeWidth - edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - outerCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowDistance = (shadowTexel.x - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - outerCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.StrokeColor * glyphColor * strokeContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("msdf-text-stroke-outer-shadow-outer-glow")]
    public static TextEffectVarying MsdfTextStrokeOuterShadowOuterGlowVertex(
        in MsdfTextStrokeOuterShadowOuterGlowVertexContext context,
        in TextVarying input)
    {
        uint instanceIndex = ShaderBuiltins.InstanceIndex;
        uint vertexIndex = ShaderBuiltins.VertexIndex;
        var glyph = context.Glyphs[instanceIndex];
        var min = glyph.PixelMin;
        var max = glyph.PixelMax;
        var uvMin = new float2(glyph.UvRect.x, glyph.UvRect.y);
        var uvMax = new float2(glyph.UvRect.z, glyph.UvRect.w);
        var pixelSize = new GlyphPixelSize(max - min);
        var uvSize = new GlyphUvSize(uvMax - uvMin);

        if (vertexIndex == 0u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = pixelSize,
                UvSize = uvSize
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = pixelSize,
            UvSize = uvSize
        };
    }

    [FragmentShader("msdf-text-stroke-outer-shadow-outer-glow")]
    public static float4 MsdfTextStrokeOuterShadowOuterGlowFragment(
        in MsdfTextStrokeOuterShadowOuterGlowFragmentContext context,
        in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, strokeWidth + edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -strokeWidth - edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - outerCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowMedian = maths.max(
            maths.min(shadowTexel.x, shadowTexel.y),
            maths.min(maths.max(shadowTexel.x, shadowTexel.y), shadowTexel.z));
        var shadowDistance = (shadowMedian - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - outerCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.StrokeColor * glyphColor * strokeContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("sdf-text-stroke-outer-glow")]
    public static TextEffectVarying SdfTextStrokeOuterGlowVertex(in TextEffectVertexContext context, in TextVarying input)
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
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = new GlyphPixelSize(max - min),
            UvSize = new GlyphUvSize(uvMax - uvMin)
        };
    }

    [FragmentShader("sdf-text-stroke-outer-glow")]
    public static float4 SdfTextStrokeOuterGlowFragment(in SdfTextEffectFragmentContext context, in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, strokeWidth + edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -strokeWidth - edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - outerCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowDistance = (shadowTexel.x - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - outerCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.StrokeColor * glyphColor * strokeContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }

    [VertexShader("msdf-text-stroke-outer-glow")]
    public static TextEffectVarying MsdfTextStrokeOuterGlowVertex(in TextEffectVertexContext context, in TextVarying input)
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
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = uvMin,
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 1u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 2u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 3u)
        {
            return new TextEffectVarying
            {
                Position = new float4((min.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMin.x, uvMax.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }
        else if (vertexIndex == 4u)
        {
            return new TextEffectVarying
            {
                Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (min.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
                Uv = new float2(uvMax.x, uvMin.y),
                GlyphColor = glyph.Color,
                PixelSize = new GlyphPixelSize(max - min),
                UvSize = new GlyphUvSize(uvMax - uvMin)
            };
        }

        return new TextEffectVarying
        {
            Position = new float4((max.x / context.Parameters.Resolution.x) * 2f - 1f, (max.y / context.Parameters.Resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uvMax,
            GlyphColor = glyph.Color,
            PixelSize = new GlyphPixelSize(max - min),
            UvSize = new GlyphUvSize(uvMax - uvMin)
        };
    }

    [FragmentShader("msdf-text-stroke-outer-glow")]
    public static float4 MsdfTextStrokeOuterGlowFragment(in MsdfTextEffectFragmentContext context, in TextEffectVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, strokeWidth + edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -strokeWidth - edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - outerCoverage, 0f) * maths.max(context.Parameters.OuterGlowIntensity, 0f);
        var glyphSize = new float2(
            maths.max(input.PixelSize.Value.x, 1f),
            maths.max(input.PixelSize.Value.y, 1f));
        var shadowUvOffset = context.Parameters.OuterShadowOffset / glyphSize * input.UvSize.Value;
        var shadowTexel = context.Atlas.Sample<float2, float4>(input.Uv.Value + shadowUvOffset);
        var shadowMedian = maths.max(
            maths.min(shadowTexel.x, shadowTexel.y),
            maths.min(maths.max(shadowTexel.x, shadowTexel.y), shadowTexel.z));
        var shadowDistance = (shadowMedian - 0.5f) * context.Parameters.DistanceRange;
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-shadowDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * (1f - outerCoverage) *
            maths.max(context.Parameters.OuterShadowIntensity, 0f);
        var glyphColor = input.GlyphColor.Value;
        return context.Parameters.OuterShadowColor * glyphColor * shadowContribution +
            context.Parameters.OuterGlowColor * glyphColor * outerGlowContribution +
            context.Parameters.StrokeColor * glyphColor * strokeContribution +
            context.Parameters.TextColor * glyphColor * fillCoverage;
    }
}
