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
        size = Math.Max(size, MsdfTextGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Math.Max(size, SdfTextOutlineGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        size = Math.Max(size, SdfTextOutlineGlowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
        return Math.Max(size, MsdfTextOutlineGlowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size);
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
        var expectedPushConstantSize = path == TextShaderPath.Outline
            ? SdfTextOutlineGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
            : path == TextShaderPath.OutlineGlow
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOutlineGlowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
                : SdfTextOutlineGlowGraphicsShaderProgram.VertexAbi.PushConstants[0].Size
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
        => path == TextShaderPath.Outline
            ? SdfTextOutlineGraphicsShaderProgram.PackSdfTextOutlineVertexGlyphsElements(values, destination)
            : path == TextShaderPath.OutlineGlow
            ? mode == GlyphImageMode.Msdf
                ? MsdfTextOutlineGlowGraphicsShaderProgram.PackMsdfTextOutlineGlowVertexGlyphsElements(values, destination)
                : SdfTextOutlineGlowGraphicsShaderProgram.PackSdfTextOutlineGlowVertexGlyphsElements(values, destination)
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
    {
        if (path == TextShaderPath.Outline)
        {
            var outlineParameters = new TextOutlineParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                TextColor = new float4(1, 1, 1, 1),
                OutlineColor = new float4(effects.OutlineColor.X, effects.OutlineColor.Y, effects.OutlineColor.Z, effects.OutlineColor.W),
                OutlineWidth = effects.OutlineWidth,
                DistanceRange = distanceRange,
            };
            return SdfTextOutlineGraphicsShaderProgram.PackSdfTextOutlineVertexParameters(in outlineParameters, destination);
        }

        if (path == TextShaderPath.OutlineGlow)
        {
            var effectParameters = new TextEffectParameters
            {
                Resolution = new float2(viewport.Width, viewport.Height),
                TextColor = new float4(1, 1, 1, 1),
                OutlineColor = new float4(effects.OutlineColor.X, effects.OutlineColor.Y, effects.OutlineColor.Z, effects.OutlineColor.W),
                OutlineWidth = effects.OutlineWidth,
                DistanceRange = distanceRange,
                GlowColor = new float4(effects.GlowColor.X, effects.GlowColor.Y, effects.GlowColor.Z, effects.GlowColor.W),
                GlowRadius = effects.GlowRadius,
                GlowIntensity = effects.GlowIntensity,
                OuterShadowColor = new float4(effects.OuterShadowColor.X, effects.OuterShadowColor.Y, effects.OuterShadowColor.Z, effects.OuterShadowColor.W),
                OuterShadowOffset = new float2(effects.OuterShadowOffset.X, effects.OuterShadowOffset.Y),
                OuterShadowWidth = effects.OuterShadowWidth,
                OuterShadowBlurRadius = effects.OuterShadowBlurRadius,
                OuterShadowSpread = effects.OuterShadowSpread,
                OuterShadowIntensity = effects.OuterShadowIntensity,
            };

            return mode == GlyphImageMode.Msdf
                ? MsdfTextOutlineGlowGraphicsShaderProgram.PackMsdfTextOutlineGlowVertexParameters(in effectParameters, destination)
                : SdfTextOutlineGlowGraphicsShaderProgram.PackSdfTextOutlineGlowVertexParameters(in effectParameters, destination);
        }

        var parameters = new TextParameters
        {
            Resolution = new float2(viewport.Width, viewport.Height),
            TextColor = new float4(1, 1, 1, 1),
            OutlineColor = default,
            OutlineWidth = 0,
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
