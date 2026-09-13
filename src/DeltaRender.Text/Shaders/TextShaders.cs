using Delta;
using Delta.Graphics.Semantics;
using Delta.Shader;

namespace Delta.Render.Text;

public struct GlyphInstance
{
    public float2 PixelMin;
    public float2 PixelMax;
    public float4 UvRect;
    public float4 Color;
}

public struct TextParameters
{
    public float2 Resolution;
    public float4 TextColor;
    public float4 StrokeColor;
    public float StrokeWidth;
    public float DistanceRange;
}

public struct TextGradientParameters
{
    public float2 Resolution;
    public float DistanceRange;
    public float4 GradientLine;
    public float4 Stop0;
    public float4 Stop1;
    public float4 Stop2;
    public float4 Stop3;
    public float4 StopPositions;
    public float StopCount;
    public float Radial;
    public float4 StrokeColor;
    public float StrokeWidth;
}

public struct TextStrokeParameters
{
    public float2 Resolution;
    public float4 TextColor;
    public float4 StrokeColor;
    public float StrokeWidth;
    public float DistanceRange;
}

public struct TextOuterGlowOnlyParameters
{
    public float2 Resolution;
    public float DistanceRange;
    public float4 OuterGlowColor;
    public float OuterGlowRadius;
    public float OuterGlowIntensity;
}

[Interstage]
public struct TextVarying
{
    public Position Position;
    public Uv0 Uv;
    public VertexColor GlyphColor;
    public Uv0 PaintPosition;
    public Uv0 PaintUvScale;
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

public readonly struct TextGradientVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextGradientParameters Parameters;
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

public readonly struct SdfTextGradientFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextGradientParameters Parameters;
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

public readonly struct MsdfTextGradientFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextGradientParameters Parameters;
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
    public float2 Resolution;
    public float DistanceRange;
    public float4 OuterShadowColor;
    public float2 OuterShadowOffset;
    public float OuterShadowWidth;
    public float OuterShadowBlurRadius;
    public float OuterShadowSpread;
    public float OuterShadowIntensity;
}

public struct TextInnerShadowParameters
{
    public float2 Resolution;
    public float DistanceRange;
    public float4 InnerShadowColor;
    public float2 InnerShadowOffset;
    public float InnerShadowWidth;
    public float InnerShadowBlurRadius;
    public float InnerShadowSpread;
    public float InnerShadowIntensity;
}

public struct TextInnerGlowOnlyParameters
{
    public float2 Resolution;
    public float DistanceRange;
    public float4 InnerGlowColor;
    public float InnerGlowRadius;
    public float InnerGlowSpread;
    public float InnerGlowIntensity;
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

public readonly struct SdfTextInnerShadowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextInnerShadowParameters Parameters;
}

public readonly struct SdfTextInnerShadowFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextInnerShadowParameters Parameters;
}

public readonly struct MsdfTextInnerShadowVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextInnerShadowParameters Parameters;
}

public readonly struct MsdfTextInnerShadowFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextInnerShadowParameters Parameters;
}

public readonly struct SdfTextInnerGlowOnlyVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextInnerGlowOnlyParameters Parameters;
}

public readonly struct SdfTextInnerGlowOnlyFragmentContext
{
    [Layout(0, 3)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextInnerGlowOnlyParameters Parameters;
}

public readonly struct MsdfTextInnerGlowOnlyVertexContext
{
    [Layout(0, 0)]
    public readonly ReadOnlyStorageBuffer<GlyphInstance> Glyphs;

    [PushConstant]
    public readonly TextInnerGlowOnlyParameters Parameters;
}

public readonly struct MsdfTextInnerGlowOnlyFragmentContext
{
    [Layout(0, 4)]
    public readonly SampledTexture2D Atlas;

    [PushConstant]
    public readonly TextInnerGlowOnlyParameters Parameters;
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
        new float4(color.w * color.xyz, color.w);

    private static float4 PremultiplyProduct(float4 color, float4 glyphColor)
    {
        float alpha = color.w * glyphColor.w;
        return new float4(alpha * color.xyz * glyphColor.xyz, alpha);
    }

    private static float SignedDistance(float sample, float distanceRange) =>
        (sample - 0.5f) * (2f * distanceRange);

    private static float MsdfSignedDistance(float4 texel, float distanceRange)
    {
        var median = maths.max(
            maths.min(texel.x, texel.y),
            maths.min(maths.max(texel.x, texel.y), texel.z));
        return SignedDistance(median, distanceRange);
    }

    private static float Edge(float distance) =>
        maths.max(intrinsics.fwidth(distance) * 0.5f, 0.0001f);

    private static float Coverage(float distance, float edge) =>
        maths.smoothstep(-edge, edge, distance);

    private static float4 RenderText(
        float signedDistance,
        float4 glyphColor,
        float4 textColor,
        float4 strokeColor,
        float strokeWidth)
    {
        var edge = Edge(signedDistance);
        var fillCoverage = Coverage(signedDistance, edge);
        var width = maths.max(strokeWidth, 0f);
        var outerCoverage = Coverage(signedDistance + width, edge);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        return fillCoverage * PremultiplyProduct(textColor, glyphColor) +
            strokeContribution * PremultiplyProduct(strokeColor, glyphColor);
    }

    private static float4 SampleGradient(
        float2 pixel,
        float4 gradientLine,
        float4 stop0,
        float4 stop1,
        float4 stop2,
        float4 stop3,
        float4 stopPositions,
        float stopCount,
        float radial)
    {
        float t;
        if (radial > 0.5f)
        {
            var radii = maths.max(maths.abs(gradientLine.zw), new float2(0.0001f));
            t = maths.clamp(maths.length((pixel - gradientLine.xy) / radii), 0f, 1f);
        }
        else
        {
            var direction = gradientLine.zw - gradientLine.xy;
            var denominator = maths.max(maths.dot(direction, direction), 0.0001f);
            t = maths.clamp(maths.dot(pixel - gradientLine.xy, direction) / denominator, 0f, 1f);
        }

        if (t <= stopPositions.x)
        {
            return stop0;
        }

        var color = stop0;
        var previous = stopPositions.x;
        if (stopCount > 1f)
        {
            var next = stopPositions.y;
            if (t <= next)
            {
                return maths.lerp(color, stop1, (t - previous) / maths.max(next - previous, 0.0001f));
            }

            color = stop1;
            previous = next;
        }

        if (stopCount > 2f)
        {
            var next = stopPositions.z;
            if (t <= next)
            {
                return maths.lerp(color, stop2, (t - previous) / maths.max(next - previous, 0.0001f));
            }

            color = stop2;
            previous = next;
        }

        if (stopCount > 3f)
        {
            var next = stopPositions.w;
            if (t <= next)
            {
                return maths.lerp(color, stop3, (t - previous) / maths.max(next - previous, 0.0001f));
            }
        }

        return stop3;
    }

    private static float4 RenderGradientText(
        float signedDistance,
        float4 glyphColor,
        float2 pixel,
        float4 gradientLine,
        float4 stop0,
        float4 stop1,
        float4 stop2,
        float4 stop3,
        float4 stopPositions,
        float stopCount,
        float radial,
        float4 strokeColor,
        float strokeWidth)
    {
        var edge = Edge(signedDistance);
        var fillCoverage = Coverage(signedDistance, edge);
        var width = maths.max(strokeWidth, 0f);
        var outerCoverage = Coverage(signedDistance + width, edge);
        var strokeContribution = maths.max(outerCoverage - fillCoverage, 0f);
        var fillColor = SampleGradient(
            pixel,
            gradientLine,
            stop0,
            stop1,
            stop2,
            stop3,
            stopPositions,
            stopCount,
            radial);
        return fillCoverage * PremultiplyProduct(fillColor, glyphColor) +
            strokeContribution * PremultiplyProduct(strokeColor, glyphColor);
    }

    private static float4 RenderOuterGlow(
        float signedDistance,
        float4 glyphColor,
        float4 glowColor,
        float glowRadius,
        float glowIntensity)
    {
        var edge = Edge(signedDistance);
        var fillCoverage = Coverage(signedDistance, edge);
        var radius = maths.max(glowRadius, edge);
        var envelope = maths.smoothstep(-radius - edge, -edge, signedDistance);
        var contribution = maths.max(envelope - fillCoverage, 0f) * maths.max(glowIntensity, 0f);
        return contribution * PremultiplyProduct(glowColor, glyphColor);
    }

    private static float4 RenderOuterShadow(
        float signedDistance,
        float4 glyphColor,
        float4 shadowColor,
        float shadowWidth,
        float shadowBlurRadius,
        float shadowSpread,
        float shadowIntensity)
    {
        var edge = Edge(signedDistance);
        var width = maths.max(shadowWidth + shadowSpread, 0f);
        var blur = maths.max(shadowBlurRadius, edge);
        var outside = maths.max(-signedDistance - width, 0f);
        var coverage = 1f - maths.smoothstep(0f, blur + edge, outside);
        return (coverage * maths.max(shadowIntensity, 0f)) * PremultiplyProduct(shadowColor, glyphColor);
    }

    private static float4 RenderInnerShadow(
        float signedDistance,
        float shiftedDistance,
        float4 glyphColor,
        float4 shadowColor,
        float shadowWidth,
        float shadowBlurRadius,
        float shadowSpread,
        float shadowIntensity)
    {
        var edge = Edge(signedDistance);
        // The base text pass already applies the SDF antialias coverage. Carry
        // the shadow clip through the AA boundary so source-over does not
        // leave a pale pixel between the fill and shadow.
        var shapeCoverage = maths.step(-edge, signedDistance);
        var width = maths.max(shadowWidth, 0f);
        var shiftedEdge = Edge(shiftedDistance);
        var blur = maths.max(shadowBlurRadius, shiftedEdge);
        // Compensate the shifted SDF's antialias band as well; the shadow must
        // meet the glyph boundary even when the offset samples that band.
        var depth = maths.max(shiftedDistance - shadowSpread - shiftedEdge, 0f);
        var coverage = shapeCoverage * (1f - maths.smoothstep(
            width,
            width + blur + shiftedEdge,
            depth));
        return (coverage * maths.max(shadowIntensity, 0f)) * PremultiplyProduct(shadowColor, glyphColor);
    }

    private static float4 RenderInnerGlow(
        float signedDistance,
        float4 glyphColor,
        float4 glowColor,
        float glowRadius,
        float glowSpread,
        float glowIntensity)
    {
        var edge = Edge(signedDistance);
        var shapeCoverage = Coverage(signedDistance, edge);
        var radius = maths.max(glowRadius, edge);
        var spread = maths.max(glowSpread, 0f);
        var coverage = shapeCoverage * (1f - maths.smoothstep(
            spread,
            spread + radius + edge,
            maths.max(signedDistance, 0f)));
        return (coverage * maths.max(glowIntensity, 0f)) * PremultiplyProduct(glowColor, glyphColor);
    }

    [VertexShader("sdf-text")]
    public static TextVarying SdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("sdf-text")]
    public static float4 SdfTextFragment(in SdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = SignedDistance(texel.x, context.Parameters.DistanceRange);
        return RenderText(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.TextColor,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
    }

    [VertexShader("sdf-text-gradient")]
    public static TextVarying SdfTextGradientVertex(
        in TextGradientVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("sdf-text-gradient")]
    public static float4 SdfTextGradientFragment(
        in SdfTextGradientFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        return RenderGradientText(
            SignedDistance(texel.x, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            input.PaintPosition,
            context.Parameters.GradientLine,
            context.Parameters.Stop0,
            context.Parameters.Stop1,
            context.Parameters.Stop2,
            context.Parameters.Stop3,
            context.Parameters.StopPositions,
            context.Parameters.StopCount,
            context.Parameters.Radial,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
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
            ZeroOffset());
    }

    [FragmentShader("sdf-text-stroke")]
    public static float4 SdfTextStrokeFragment(
        in SdfTextStrokeFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = SignedDistance(texel.x, context.Parameters.DistanceRange);
        return RenderText(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.TextColor,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
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
            ZeroOffset());
    }

    [FragmentShader("msdf-text-stroke")]
    public static float4 MsdfTextStrokeFragment(
        in MsdfTextStrokeFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = MsdfSignedDistance(texel, context.Parameters.DistanceRange);
        return RenderText(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.TextColor,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
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
        var signedDistance = SignedDistance(texel.x, context.Parameters.DistanceRange);
        return RenderOuterGlow(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.OuterGlowColor,
            context.Parameters.OuterGlowRadius,
            context.Parameters.OuterGlowIntensity);
    }

    private static TextVarying CreateOffsetTextVarying(
        GlyphInstance glyph,
        uint vertexIndex,
        float2 resolution,
        float2 offset)
    {
        var glyphSize = glyph.PixelMax - glyph.PixelMin;
        return CreateTextVarying(
            glyph,
            vertexIndex,
            resolution,
            glyph.PixelMin + offset,
            glyphSize,
            glyph.UvRect.xy,
            glyph.UvRect.zw - glyph.UvRect.xy);
    }

    private static float2 ZeroOffset() => new float2(0f);

    private static TextVarying CreateExpandedTextVarying(
        GlyphInstance glyph,
        uint vertexIndex,
        float2 resolution,
        float expansion)
    {
        var glyphSize = maths.max(glyph.PixelMax - glyph.PixelMin, new float2(1f));
        var uvMin = glyph.UvRect.xy;
        var uvSize = glyph.UvRect.zw - uvMin;
        var uvPerPixel = uvSize / glyphSize;
        var padding = new float2(expansion);
        var expandedPadding = padding + padding;
        var min = glyph.PixelMin - padding;
        var expandedSize = glyphSize + expandedPadding;
        var expandedUvMin = uvMin - uvPerPixel * padding;
        var expandedUvSize = uvSize + uvPerPixel * expandedPadding;
        return CreateTextVarying(
            glyph,
            vertexIndex,
            resolution,
            min,
            expandedSize,
            expandedUvMin,
            expandedUvSize);
    }

    private static TextVarying CreateTextVarying(
        GlyphInstance glyph,
        uint vertexIndex,
        float2 resolution,
        float2 pixelMin,
        float2 pixelSize,
        float2 uvMin,
        float2 uvSize)
    {
        var local = QuadGeometry.GetLocal(vertexIndex);
        var pixel = pixelMin + local * pixelSize;
        return new TextVarying
        {
            Position = new Position(QuadGeometry.ToClipPosition(pixel, resolution)),
            Uv = uvMin + local * uvSize,
            GlyphColor = glyph.Color,
            PaintPosition = new Uv0(pixel),
            PaintUvScale = new Uv0(uvSize / maths.max(pixelSize, new float2(1f))),
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
        var signedDistance = SignedDistance(texel.x, context.Parameters.DistanceRange);
        return RenderOuterShadow(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.OuterShadowColor,
            context.Parameters.OuterShadowWidth,
            context.Parameters.OuterShadowBlurRadius,
            context.Parameters.OuterShadowSpread,
            context.Parameters.OuterShadowIntensity);
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
        var signedDistance = MsdfSignedDistance(texel, context.Parameters.DistanceRange);
        return RenderOuterShadow(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.OuterShadowColor,
            context.Parameters.OuterShadowWidth,
            context.Parameters.OuterShadowBlurRadius,
            context.Parameters.OuterShadowSpread,
            context.Parameters.OuterShadowIntensity);
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
        var signedDistance = MsdfSignedDistance(texel, context.Parameters.DistanceRange);
        return RenderOuterGlow(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.OuterGlowColor,
            context.Parameters.OuterGlowRadius,
            context.Parameters.OuterGlowIntensity);
    }

    [VertexShader("msdf-text")]
    public static TextVarying MsdfTextVertex(in TextVertexContext context, in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("msdf-text")]
    public static float4 MsdfTextFragment(in MsdfTextFragmentContext context, in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var signedDistance = MsdfSignedDistance(texel, context.Parameters.DistanceRange);
        return RenderText(
            signedDistance,
            input.GlyphColor.Value,
            context.Parameters.TextColor,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
    }

    [VertexShader("msdf-text-gradient")]
    public static TextVarying MsdfTextGradientVertex(
        in TextGradientVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("msdf-text-gradient")]
    public static float4 MsdfTextGradientFragment(
        in MsdfTextGradientFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        return RenderGradientText(
            MsdfSignedDistance(texel, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            input.PaintPosition,
            context.Parameters.GradientLine,
            context.Parameters.Stop0,
            context.Parameters.Stop1,
            context.Parameters.Stop2,
            context.Parameters.Stop3,
            context.Parameters.StopPositions,
            context.Parameters.StopCount,
            context.Parameters.Radial,
            context.Parameters.StrokeColor,
            context.Parameters.StrokeWidth);
    }

    [VertexShader("sdf-text-inner-shadow")]
    public static TextVarying SdfTextInnerShadowVertex(
        in SdfTextInnerShadowVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("sdf-text-inner-shadow")]
    public static float4 SdfTextInnerShadowFragment(
        in SdfTextInnerShadowFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var shiftedTexel = context.Atlas.Sample<float2, float4>(
            input.Uv.Value - context.Parameters.InnerShadowOffset * input.PaintUvScale.Value);
        return RenderInnerShadow(
            SignedDistance(texel.x, context.Parameters.DistanceRange),
            SignedDistance(shiftedTexel.x, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            context.Parameters.InnerShadowColor,
            context.Parameters.InnerShadowWidth,
            context.Parameters.InnerShadowBlurRadius,
            context.Parameters.InnerShadowSpread,
            context.Parameters.InnerShadowIntensity);
    }

    [VertexShader("msdf-text-inner-shadow")]
    public static TextVarying MsdfTextInnerShadowVertex(
        in MsdfTextInnerShadowVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("msdf-text-inner-shadow")]
    public static float4 MsdfTextInnerShadowFragment(
        in MsdfTextInnerShadowFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        var shiftedTexel = context.Atlas.Sample<float2, float4>(
            input.Uv.Value - context.Parameters.InnerShadowOffset * input.PaintUvScale.Value);
        return RenderInnerShadow(
            MsdfSignedDistance(texel, context.Parameters.DistanceRange),
            MsdfSignedDistance(shiftedTexel, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            context.Parameters.InnerShadowColor,
            context.Parameters.InnerShadowWidth,
            context.Parameters.InnerShadowBlurRadius,
            context.Parameters.InnerShadowSpread,
            context.Parameters.InnerShadowIntensity);
    }

    [VertexShader("sdf-text-inner-glow-only")]
    public static TextVarying SdfTextInnerGlowOnlyVertex(
        in SdfTextInnerGlowOnlyVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("sdf-text-inner-glow-only")]
    public static float4 SdfTextInnerGlowOnlyFragment(
        in SdfTextInnerGlowOnlyFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        return RenderInnerGlow(
            SignedDistance(texel.x, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            context.Parameters.InnerGlowColor,
            context.Parameters.InnerGlowRadius,
            context.Parameters.InnerGlowSpread,
            context.Parameters.InnerGlowIntensity);
    }

    [VertexShader("msdf-text-inner-glow-only")]
    public static TextVarying MsdfTextInnerGlowOnlyVertex(
        in MsdfTextInnerGlowOnlyVertexContext context,
        in TextVarying input)
    {
        var glyph = context.Glyphs[ShaderBuiltins.InstanceIndex];
        return CreateOffsetTextVarying(
            glyph,
            ShaderBuiltins.VertexIndex,
            context.Parameters.Resolution,
            ZeroOffset());
    }

    [FragmentShader("msdf-text-inner-glow-only")]
    public static float4 MsdfTextInnerGlowOnlyFragment(
        in MsdfTextInnerGlowOnlyFragmentContext context,
        in TextVarying input)
    {
        var texel = context.Atlas.Sample<float2, float4>(input.Uv.Value);
        return RenderInnerGlow(
            MsdfSignedDistance(texel, context.Parameters.DistanceRange),
            input.GlyphColor.Value,
            context.Parameters.InnerGlowColor,
            context.Parameters.InnerGlowRadius,
            context.Parameters.InnerGlowSpread,
            context.Parameters.InnerGlowIntensity);
    }

}
