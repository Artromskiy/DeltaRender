using System;
using System.Collections.Generic;
using Delta;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Render.Text;
using Delta.Text.Contract;

namespace Delta.Render.Text;

internal readonly record struct TextShaderLayout(
    GlyphImageEncoding Encoding,
    RenderTextureFormat AtlasFormat,
    int AtlasBytesPerPixel,
    ShaderBinding InstanceBinding,
    uint InstanceStride,
    ShaderBinding AtlasBinding,
    uint PushConstantSize);

internal static class TextShaderPacking
{
    internal static uint MaxPushConstantSize { get; } = GetMaxPushConstantSize();

    private static uint GetMaxPushConstantSize()
    {
        var size = SdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Size;
        size = Maths.Max(size, MsdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextStrokeGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextOuterGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextOuterGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextStrokeGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextOuterShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextOuterShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextInnerShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextInnerShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextInnerGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextInnerGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SdfTextGradientGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, MsdfTextGradientGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        return size;
    }

    internal static TextShaderLayout Resolve(IGraphicsShaderProgram program, GlyphImageMode mode)
        => Resolve(program, mode, TextShaderPath.Standard);

    internal static TextShaderLayout Resolve(
        IGraphicsShaderProgram program,
        GlyphImageMode mode,
        TextShaderPath path)
    {
        ArgumentNullException.ThrowIfNull(program);
        var imageFormat = DescribeImageFormat(mode);
        var instanceBinding = FindBinding(
            program,
            ShaderResourceKind.StorageBuffer,
            ShaderStageMask.Vertex,
            "vertex instance buffer");
        var atlasBinding = FindTextureBinding(program, "fragment atlas texture");
        var pushConstantSize = FindPushConstantSize(program);
        var expectedPushConstantSize = path == TextShaderPath.Gradient
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextGradientGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextGradientGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.Stroke
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextStrokeGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextStrokeGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.OuterGlowOnly
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOuterGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextOuterGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.OuterShadow
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOuterShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextOuterShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.InnerShadow
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextInnerShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextInnerShadowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.InnerGlowOnly
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextInnerGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextInnerGlowOnlyGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : mode == GlyphImageMode.Msdf
                ? MsdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Size;
        if (pushConstantSize != expectedPushConstantSize)
        {
            throw new ArgumentException("The text shader push-constant range does not match its generated shader path.", nameof(program));
        }

        return new TextShaderLayout(
            imageFormat.Encoding,
            imageFormat.Format,
            imageFormat.BytesPerPixel,
            instanceBinding,
            FindInstanceStride(program, instanceBinding),
            atlasBinding,
            pushConstantSize);
    }

    internal static int PackInstances(
        GlyphImageMode mode,
        ReadOnlySpan<GlyphInstance> values,
        Span<byte> destination)
        => PackInstances(TextShaderPath.Standard, mode, values, destination);

    internal static int PackInstances(
        TextShaderPath path,
        GlyphImageMode mode,
        ReadOnlySpan<GlyphInstance> values,
        Span<byte> destination)
        => path == TextShaderPath.Stroke
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextStrokeGraphicsShaderProgram.PackMsdfTextStrokeVertexGlyphsElements(values, destination)
                : SdfTextStrokeGraphicsShaderProgram.PackSdfTextStrokeVertexGlyphsElements(values, destination)
            : path == TextShaderPath.OuterGlowOnly
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOuterGlowOnlyGraphicsShaderProgram.PackMsdfTextOuterGlowOnlyVertexGlyphsElements(values, destination)
                : SdfTextOuterGlowOnlyGraphicsShaderProgram.PackSdfTextOuterGlowOnlyVertexGlyphsElements(values, destination)
            : path == TextShaderPath.OuterShadow
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOuterShadowGraphicsShaderProgram.PackMsdfTextOuterShadowVertexGlyphsElements(values, destination)
                : SdfTextOuterShadowGraphicsShaderProgram.PackSdfTextOuterShadowVertexGlyphsElements(values, destination)
            : mode == GlyphImageMode.Msdf
            ? MsdfTextGraphicsShaderProgram.PackMsdfTextVertexGlyphsElements(values, destination)
            : SdfTextGraphicsShaderProgram.PackSdfTextVertexGlyphsElements(values, destination);

    internal static int PackTextParameters(
        GlyphImageMode mode,
        PixelExtent viewport,
        float distanceRange,
        Span<byte> destination)
        => PackTextParameters(TextShaderPath.Standard, mode, viewport, distanceRange, TextEffectValues.Empty, destination);

    internal static int PackTextParameters(
        TextShaderPath path,
        GlyphImageMode mode,
        PixelExtent viewport,
        float distanceRange,
        in TextEffectValues effects,
        Span<byte> destination)
        => PackTextParameters(path, mode, viewport, distanceRange, effects, TextGradientValues.Empty, destination);

    internal static int PackTextParameters(
        TextShaderPath path,
        GlyphImageMode mode,
        PixelExtent viewport,
        float distanceRange,
        in TextEffectValues effects,
        in TextGradientValues gradient,
        Span<byte> destination)
    {
        if (path == TextShaderPath.Gradient)
        {
            if (!gradient.IsValid)
            {
                throw new ArgumentException("The gradient text path requires a valid gradient payload.", nameof(gradient));
            }

            var gradientParameters = new TextGradientParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                DistanceRange = distanceRange,
                GradientLine = new float4(gradient.Line.X, gradient.Line.Y, gradient.Line.Z, gradient.Line.W),
                Stop0 = new float4(gradient.Stop0.X, gradient.Stop0.Y, gradient.Stop0.Z, gradient.Stop0.W),
                Stop1 = new float4(gradient.Stop1.X, gradient.Stop1.Y, gradient.Stop1.Z, gradient.Stop1.W),
                Stop2 = new float4(gradient.Stop2.X, gradient.Stop2.Y, gradient.Stop2.Z, gradient.Stop2.W),
                Stop3 = new float4(gradient.Stop3.X, gradient.Stop3.Y, gradient.Stop3.Z, gradient.Stop3.W),
                StopPositions = new float4(gradient.StopPositions.X, gradient.StopPositions.Y, gradient.StopPositions.Z, gradient.StopPositions.W),
                StopCount = gradient.StopCount,
                Radial = gradient.Radial,
                StrokeColor = new float4(effects.StrokeColor.X, effects.StrokeColor.Y, effects.StrokeColor.Z, effects.StrokeColor.W),
                StrokeWidth = effects.StrokeWidth,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextGradientGraphicsShaderProgram.PackMsdfTextGradientVertexParameters(in gradientParameters, destination)
                : SdfTextGradientGraphicsShaderProgram.PackSdfTextGradientVertexParameters(in gradientParameters, destination);
        }

        if (path == TextShaderPath.Stroke)
        {
            var strokeParameters = new TextStrokeParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                TextColor = new float4(1, 1, 1, 1),
                StrokeColor = new float4(effects.StrokeColor.X, effects.StrokeColor.Y, effects.StrokeColor.Z, effects.StrokeColor.W),
                StrokeWidth = effects.StrokeWidth,
                DistanceRange = distanceRange,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextStrokeGraphicsShaderProgram.PackMsdfTextStrokeVertexParameters(in strokeParameters, destination)
                : SdfTextStrokeGraphicsShaderProgram.PackSdfTextStrokeVertexParameters(in strokeParameters, destination);
        }

        if (path == TextShaderPath.OuterGlowOnly)
        {
            var outerGlowParameters = new TextOuterGlowOnlyParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                DistanceRange = distanceRange,
                OuterGlowColor = new float4(effects.OuterGlowColor.X, effects.OuterGlowColor.Y, effects.OuterGlowColor.Z, effects.OuterGlowColor.W),
                OuterGlowRadius = effects.OuterGlowRadius,
                OuterGlowIntensity = effects.OuterGlowIntensity,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextOuterGlowOnlyGraphicsShaderProgram.PackMsdfTextOuterGlowOnlyVertexParameters(in outerGlowParameters, destination)
                : SdfTextOuterGlowOnlyGraphicsShaderProgram.PackSdfTextOuterGlowOnlyVertexParameters(in outerGlowParameters, destination);
        }

        if (path == TextShaderPath.OuterShadow)
        {
            var shadowParameters = new TextOuterShadowParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                DistanceRange = distanceRange,
                OuterShadowColor = new float4(effects.OuterShadowColor.X, effects.OuterShadowColor.Y, effects.OuterShadowColor.Z, effects.OuterShadowColor.W),
                OuterShadowOffset = new float2(effects.OuterShadowOffset.X, effects.OuterShadowOffset.Y),
                OuterShadowWidth = effects.OuterShadowWidth,
                OuterShadowBlurRadius = effects.OuterShadowBlurRadius,
                OuterShadowSpread = effects.OuterShadowSpread,
                OuterShadowIntensity = effects.OuterShadowIntensity,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextOuterShadowGraphicsShaderProgram.PackMsdfTextOuterShadowVertexParameters(in shadowParameters, destination)
                : SdfTextOuterShadowGraphicsShaderProgram.PackSdfTextOuterShadowVertexParameters(in shadowParameters, destination);
        }

        if (path == TextShaderPath.InnerShadow)
        {
            var hasGradient = gradient.IsValid;
            var shadowParameters = new TextInnerShadowParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                DistanceRange = distanceRange,
                GradientLine = hasGradient
                    ? new float4(gradient.Line.X, gradient.Line.Y, gradient.Line.Z, gradient.Line.W)
                    : default,
                Stop0 = hasGradient
                    ? new float4(gradient.Stop0.X, gradient.Stop0.Y, gradient.Stop0.Z, gradient.Stop0.W)
                    : default,
                Stop1 = hasGradient
                    ? new float4(gradient.Stop1.X, gradient.Stop1.Y, gradient.Stop1.Z, gradient.Stop1.W)
                    : default,
                Stop2 = hasGradient
                    ? new float4(gradient.Stop2.X, gradient.Stop2.Y, gradient.Stop2.Z, gradient.Stop2.W)
                    : default,
                Stop3 = hasGradient
                    ? new float4(gradient.Stop3.X, gradient.Stop3.Y, gradient.Stop3.Z, gradient.Stop3.W)
                    : default,
                StopPositions = hasGradient
                    ? new float4(
                        gradient.StopPositions.X,
                        gradient.StopPositions.Y,
                        gradient.StopPositions.Z,
                        gradient.StopPositions.W)
                    : default,
                StopCount = hasGradient ? gradient.StopCount : 0,
                Radial = hasGradient ? gradient.Radial : 0,
                StrokeColor = new float4(
                    effects.StrokeColor.X,
                    effects.StrokeColor.Y,
                    effects.StrokeColor.Z,
                    effects.StrokeColor.W),
                StrokeWidth = effects.StrokeWidth,
                InnerShadowColor = new float4(effects.InnerShadowColor.X, effects.InnerShadowColor.Y, effects.InnerShadowColor.Z, effects.InnerShadowColor.W),
                InnerShadowOffset = new float2(effects.InnerShadowOffset.X, effects.InnerShadowOffset.Y),
                InnerShadowWidth = effects.InnerShadowWidth,
                InnerShadowBlurRadius = effects.InnerShadowBlurRadius,
                InnerShadowSpread = effects.InnerShadowSpread,
                InnerShadowIntensity = effects.InnerShadowIntensity,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextInnerShadowGraphicsShaderProgram.PackMsdfTextInnerShadowVertexParameters(in shadowParameters, destination)
                : SdfTextInnerShadowGraphicsShaderProgram.PackSdfTextInnerShadowVertexParameters(in shadowParameters, destination);
        }

        if (path == TextShaderPath.InnerGlowOnly)
        {
            var glowParameters = new TextInnerGlowOnlyParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                DistanceRange = distanceRange,
                InnerGlowColor = new float4(effects.InnerGlowColor.X, effects.InnerGlowColor.Y, effects.InnerGlowColor.Z, effects.InnerGlowColor.W),
                InnerGlowRadius = effects.InnerGlowRadius,
                InnerGlowSpread = effects.InnerGlowSpread,
                InnerGlowIntensity = effects.InnerGlowIntensity,
            };
            return mode == GlyphImageMode.Msdf
                ? MsdfTextInnerGlowOnlyGraphicsShaderProgram.PackMsdfTextInnerGlowOnlyVertexParameters(in glowParameters, destination)
                : SdfTextInnerGlowOnlyGraphicsShaderProgram.PackSdfTextInnerGlowOnlyVertexParameters(in glowParameters, destination);
        }

        var parameters = new TextParameters
        {
            Resolution = new float2(viewport.Width, viewport.Height),
            TextColor = new float4(1, 1, 1, 1),
            StrokeColor = default,
            StrokeWidth = 0,
            DistanceRange = distanceRange,
        };

        return mode == GlyphImageMode.Msdf
            ? MsdfTextGraphicsShaderProgram.PackMsdfTextVertexParameters(in parameters, destination)
            : SdfTextGraphicsShaderProgram.PackSdfTextVertexParameters(in parameters, destination);
    }

    private static (GlyphImageEncoding Encoding, RenderTextureFormat Format, int BytesPerPixel) DescribeImageFormat(GlyphImageMode mode)
        => mode switch
        {
            GlyphImageMode.Coverage => (GlyphImageEncoding.CoverageR8, RenderTextureFormat.R8Unorm, 1),
            GlyphImageMode.Sdf => (GlyphImageEncoding.SdfR8, RenderTextureFormat.R8Unorm, 1),
            GlyphImageMode.Msdf => (GlyphImageEncoding.MsdfRgb8, RenderTextureFormat.Rgba8Unorm, 4),
            GlyphImageMode.Color => (GlyphImageEncoding.ColorRgba8PremultipliedSrgb, RenderTextureFormat.Rgba8Srgb, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The text feature requires a concrete glyph image mode."),
        };

    private static uint FindInstanceStride(IGraphicsShaderProgram program, ShaderBinding binding)
    {
        ShaderResourceBinding? found = null;
        foreach (var resource in program.Vertex.Abi.Resources)
        {
            if (resource.Binding != binding || resource.Kind != ShaderResourceKind.StorageBuffer ||
                !resource.Stages.HasFlag(ShaderStageMask.Vertex) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible instance buffer.", nameof(program));
            }

            found = resource;
        }

        if (found is null || found.Layout.ArrayStride == 0)
        {
            throw new ArgumentException("The shader manifest has no resolved instance-buffer array stride.", nameof(program));
        }

        return found.Layout.ArrayStride;
    }

    private static uint FindPushConstantSize(IGraphicsShaderProgram program)
    {
        var vertexPushConstants = program.Vertex.Abi.PushConstants;
        var fragmentPushConstants = program.Fragment.Abi.PushConstants;
        if (vertexPushConstants.Count != 1 || fragmentPushConstants.Count != 1)
        {
            throw new ArgumentException("The text shader manifest must expose one push-constant range per graphics stage.", nameof(program));
        }

        var vertex = vertexPushConstants[0];
        var fragment = fragmentPushConstants[0];
        if (vertex.Offset != fragment.Offset || vertex.Size != fragment.Size || vertex.Size == 0)
        {
            throw new ArgumentException("The text shader stages must expose matching non-empty push-constant ranges.", nameof(program));
        }

        return vertex.Size;
    }

    private static ShaderBinding FindBinding(
        IGraphicsShaderProgram program,
        ShaderResourceKind kind,
        ShaderStageMask stage,
        string description)
    {
        var found = FindBinding(program.Vertex.Abi.Resources, kind, stage);
        var fragmentFound = FindBinding(program.Fragment.Abi.Resources, kind, stage);
        if (found.HasValue && fragmentFound.HasValue && found.Value != fragmentFound.Value)
        {
            throw new ArgumentException($"The shader manifest contains multiple {description} resources.", nameof(program));
        }

        found ??= fragmentFound;
        if (!found.HasValue)
        {
            throw new ArgumentException($"The shader manifest has no {description} resource.", nameof(program));
        }

        return found.Value;
    }

    private static ShaderBinding FindTextureBinding(IGraphicsShaderProgram program, string description)
    {
        var found = FindTextureBinding(program.Vertex.Abi.Resources);
        var fragmentFound = FindTextureBinding(program.Fragment.Abi.Resources);
        if (found.HasValue && fragmentFound.HasValue && found.Value != fragmentFound.Value)
        {
            throw new ArgumentException($"The shader manifest contains multiple {description} resources.", nameof(program));
        }

        found ??= fragmentFound;
        if (!found.HasValue)
        {
            throw new ArgumentException($"The shader manifest has no {description} resource.", nameof(program));
        }

        return found.Value;
    }

    private static ShaderBinding? FindBinding(
        IReadOnlyList<ShaderResourceBinding> resources,
        ShaderResourceKind kind,
        ShaderStageMask stage)
    {
        ShaderBinding? found = null;
        foreach (var resource in resources)
        {
            if (resource.Kind != kind || !resource.Stages.HasFlag(stage) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found.HasValue && found.Value != resource.Binding)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible text resource.");
            }

            found = resource.Binding;
        }

        return found;
    }

    private static ShaderBinding? FindTextureBinding(IReadOnlyList<ShaderResourceBinding> resources)
    {
        ShaderBinding? found = null;
        foreach (var resource in resources)
        {
            if ((resource.Kind != ShaderResourceKind.SampledTexture && resource.Kind != ShaderResourceKind.CombinedTextureSampler) ||
                !resource.Stages.HasFlag(ShaderStageMask.Fragment) || (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (found.HasValue && found.Value != resource.Binding)
            {
                throw new ArgumentException("The shader manifest contains more than one compatible atlas resource.");
            }

            found = resource.Binding;
        }

        return found;
    }
}
