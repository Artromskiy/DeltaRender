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

public struct TextOuterGlowOnlyParameters
{
    public float2 Resolution = default;
    public float DistanceRange = default;
    public float4 OuterGlowColor = default;
    public float OuterGlowRadius = default;
    public float OuterGlowIntensity = default;

    public TextOuterGlowOnlyParameters()
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

public readonly struct TextStrokeVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextStrokeParameters Parameters;
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

public readonly struct SdfTextOuterGlowOnlyVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterGlowOnlyParameters Parameters;
}

public readonly struct SdfTextOuterGlowOnlyFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterGlowOnlyParameters Parameters;
}

public readonly struct MsdfTextOuterGlowOnlyVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextOuterGlowOnlyParameters Parameters;
}

public readonly struct MsdfTextOuterGlowOnlyFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextOuterGlowOnlyParameters Parameters;
}

public struct TextOuterShadowParameters
{
    public float2 Resolution = default;
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

public readonly struct MsdfTextFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextParameters Parameters;
}

public static class TextShaders
{
    private static float4 Premultiply(float4 color) =>
        new float4(color.xyz * color.w, color.w);

    [VertexShader("sdf-text")]
    public static TextVarying SdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            new float2(0f));
    }

    [FragmentShader("sdf-text")]
    public static float4 SdfTextFragment(in SdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return Premultiply(context.Parameters.TextColor * input.GlyphColor.Value) * fillCoverage +
            Premultiply(context.Parameters.StrokeColor * input.GlyphColor.Value) * strokeContribution;
    }

    [VertexShader("sdf-text-stroke")]
    public static TextVarying SdfTextStrokeVertex(
        in TextStrokeVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            new float2(0f));
    }

    [FragmentShader("sdf-text-stroke")]
    public static float4 SdfTextStrokeFragment(
        in SdfTextStrokeFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return Premultiply(context.Parameters.TextColor * input.GlyphColor.Value) * fillCoverage +
            Premultiply(context.Parameters.StrokeColor * input.GlyphColor.Value) * strokeContribution;
    }

    [VertexShader("msdf-text-stroke")]
    public static TextVarying MsdfTextStrokeVertex(
        in MsdfTextStrokeVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            new float2(0f));
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
        var signedDistance = (median - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return Premultiply(context.Parameters.TextColor * input.GlyphColor.Value) * fillCoverage +
            Premultiply(context.Parameters.StrokeColor * input.GlyphColor.Value) * strokeContribution;
    }

    [VertexShader("sdf-text-outer-glow-only")]
    public static TextVarying SdfTextOuterGlowOnlyVertex(
        in SdfTextOuterGlowOnlyVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateExpandedTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            maths.max(context.Parameters.OuterGlowRadius, 0f));
    }

    [FragmentShader("sdf-text-outer-glow-only")]
    public static float4 SdfTextOuterGlowOnlyFragment(
        in SdfTextOuterGlowOnlyFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - fillCoverage, 0f) *
            maths.max(context.Parameters.OuterGlowIntensity, 0f);
        return Premultiply(context.Parameters.OuterGlowColor * input.GlyphColor.Value) * outerGlowContribution;
    }

    private static TextVarying CreateOffsetTextVarying(
        GlyphInstance glyph,
        uint vertexIndex,
        float2 resolution,
        float2 offset)
    {
        var min = glyph.PixelMin + offset;
        var max = glyph.PixelMax + offset;
        var local = QuadGeometry.GetLocal(vertexIndex);
        var pixel = min + local * (max - min);
        var uv = glyph.UvRect.xy + local * (glyph.UvRect.zw - glyph.UvRect.xy);

        return new TextVarying
        {
            Position = new float4((pixel.x / resolution.x) * 2f - 1f, (pixel.y / resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uv,
            GlyphColor = glyph.Color
        };
    }

    private static TextVarying CreateExpandedTextVarying(
        GlyphInstance glyph,
        uint vertexIndex,
        float2 resolution,
        float expansion)
    {
        var glyphSize = glyph.PixelMax - glyph.PixelMin;
        glyphSize = new float2(maths.max(glyphSize.x, 1f), maths.max(glyphSize.y, 1f));
        var uvMin = glyph.UvRect.xy;
        var uvMax = glyph.UvRect.zw;
        var uvPerPixel = (uvMax - uvMin) / glyphSize;
        var min = glyph.PixelMin - new float2(expansion);
        var max = glyph.PixelMax + new float2(expansion);
        var expandedUvMin = uvMin - uvPerPixel * expansion;
        var expandedUvMax = uvMax + uvPerPixel * expansion;
        var local = QuadGeometry.GetLocal(vertexIndex);
        var pixel = min + local * (max - min);
        var uv = expandedUvMin + local * (expandedUvMax - expandedUvMin);

        return new TextVarying
        {
            Position = new float4((pixel.x / resolution.x) * 2f - 1f, (pixel.y / resolution.y) * 2f - 1f, 0f, 1f),
            Uv = uv,
            GlyphColor = glyph.Color
        };
    }

    [VertexShader("sdf-text-outer-shadow")]
    public static TextVarying SdfTextOuterShadowVertex(
        in SdfTextOuterShadowVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            context.Parameters.OuterShadowOffset);
    }

    [FragmentShader("sdf-text-outer-shadow")]
    public static float4 SdfTextOuterShadowFragment(
        in SdfTextOuterShadowFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = (texel.x - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-signedDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * maths.max(context.Parameters.OuterShadowIntensity, 0f);
        return Premultiply(context.Parameters.OuterShadowColor * input.GlyphColor.Value) * shadowContribution;
    }

    [VertexShader("msdf-text-outer-shadow")]
    public static TextVarying MsdfTextOuterShadowVertex(
        in MsdfTextOuterShadowVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            context.Parameters.OuterShadowOffset);
    }

    [FragmentShader("msdf-text-outer-shadow")]
    public static float4 MsdfTextOuterShadowFragment(
        in MsdfTextOuterShadowFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var shadowWidth = maths.max(
            context.Parameters.OuterShadowWidth + context.Parameters.OuterShadowSpread,
            0f);
        var shadowBlur = maths.max(context.Parameters.OuterShadowBlurRadius, edge);
        var shadowOutside = maths.max(-signedDistance - shadowWidth, 0f);
        var shadowCoverage = 1f - maths.smoothstep(0f, shadowBlur + edge, shadowOutside);
        var shadowContribution = shadowCoverage * maths.max(context.Parameters.OuterShadowIntensity, 0f);
        return Premultiply(context.Parameters.OuterShadowColor * input.GlyphColor.Value) * shadowContribution;
    }


    [VertexShader("msdf-text-outer-glow-only")]
    public static TextVarying MsdfTextOuterGlowOnlyVertex(
        in MsdfTextOuterGlowOnlyVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateExpandedTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            maths.max(context.Parameters.OuterGlowRadius, 0f));
    }

    [FragmentShader("msdf-text-outer-glow-only")]
    public static float4 MsdfTextOuterGlowOnlyFragment(
        in MsdfTextOuterGlowOnlyFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = (median - 0.5f) * (2f * context.Parameters.DistanceRange);
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var outerGlowRadius = maths.max(context.Parameters.OuterGlowRadius, edge);
        var outerGlowEnvelope = maths.smoothstep(-outerGlowRadius - edge, -edge, signedDistance);
        var outerGlowContribution = maths.max(outerGlowEnvelope - fillCoverage, 0f) *
            maths.max(context.Parameters.OuterGlowIntensity, 0f);
        return Premultiply(context.Parameters.OuterGlowColor * input.GlyphColor.Value) * outerGlowContribution;
    }

    [VertexShader("msdf-text")]
    public static TextVarying MsdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            new float2(0f));
    }

    [FragmentShader("msdf-text")]
    public static float4 MsdfTextFragment(in MsdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        var signedDistance = median - 0.5f;
        signedDistance *= 2f * context.Parameters.DistanceRange;
        var edge = maths.max(intrinsics.fwidth(signedDistance) * 0.5f, 0.0001f);
        var fillCoverage = maths.smoothstep(-edge, edge, signedDistance);
        var strokeWidth = maths.max(context.Parameters.StrokeWidth, 0f);
        var outerCoverage = maths.smoothstep(-strokeWidth - edge, -strokeWidth + edge, signedDistance);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return Premultiply(context.Parameters.TextColor * input.GlyphColor.Value) * fillCoverage +
            Premultiply(context.Parameters.StrokeColor * input.GlyphColor.Value) * strokeContribution;
    }

}
