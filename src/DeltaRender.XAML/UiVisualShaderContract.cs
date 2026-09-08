using Delta;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Render.UIShaders;
using Delta.XAML.Contract;
using ShaderEffectLayer = Delta.Render.UIShaders.UiEffectLayerParameters;
using ShaderEffectParameters = Delta.Render.UIShaders.UiEffectParameters;

namespace Delta.Render.XAML;


internal enum UiRectangleShaderKind : byte
{
    Solid,
    SolidStroke,
    SolidGlow,
    SolidOuterShadow,
    Rounded,
    RoundedGlow,
    RoundedOuterShadow,
    RoundedInsetShadow,
    RoundedStrokeOuterShadow,
    RoundedStrokeGlow,
    AnalyticRounded,
    CachedMaskRounded,
    SolidLinearGradient,
    SolidImage,
}

internal static class UiVisualShaderContract
{
    private static readonly ShaderAbi SolidVertexAbi = SolidRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidFragmentAbi = SolidRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi SolidStrokeVertexAbi = SolidStrokeGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidStrokeFragmentAbi = SolidStrokeGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi SolidGlowVertexAbi = SolidGlowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidGlowFragmentAbi = SolidGlowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi SolidOuterShadowVertexAbi = SolidOuterShadowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidOuterShadowFragmentAbi = SolidOuterShadowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedVertexAbi = RoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedFragmentAbi = RoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedGlowVertexAbi = RoundedGlowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedGlowFragmentAbi = RoundedGlowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedOuterShadowVertexAbi = RoundedOuterShadowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedOuterShadowFragmentAbi = RoundedOuterShadowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedInsetShadowVertexAbi = RoundedInsetShadowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedInsetShadowFragmentAbi = RoundedInsetShadowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedStrokeOuterShadowVertexAbi = RoundedStrokeOuterShadowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedStrokeOuterShadowFragmentAbi = RoundedStrokeOuterShadowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedStrokeGlowVertexAbi = RoundedStrokeGlowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedStrokeGlowFragmentAbi = RoundedStrokeGlowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi AnalyticVertexAbi = AnalyticRoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi AnalyticFragmentAbi = AnalyticRoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi CachedMaskVertexAbi = CachedMaskRoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi CachedMaskFragmentAbi = CachedMaskRoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi LinearGradientVertexAbi = SolidLinearGradientGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi LinearGradientFragmentAbi = SolidLinearGradientGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi ImageVertexAbi = SolidImageGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi ImageFragmentAbi = SolidImageGraphicsShaderProgram.FragmentAbi;

    internal static int MaxPushConstantSize { get; } = GetMaxPushConstantSize();

    internal static ShaderBinding CachedMaskTextureBinding { get; } = GetCachedMaskTextureBinding();
    internal static ShaderBinding ImageTextureBinding { get; } = GetImageTextureBinding();

    private static int GetMaxPushConstantSize()
    {
        var size = SolidVertexAbi.PushConstants[0].Size;
        size = Maths.Max(size, SolidStrokeVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SolidGlowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, SolidOuterShadowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedGlowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedOuterShadowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedInsetShadowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedStrokeOuterShadowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedStrokeGlowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, AnalyticVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, CachedMaskVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, LinearGradientVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, ImageVertexAbi.PushConstants[0].Size);
        return checked((int)size);
    }

    private static ShaderBinding GetCachedMaskTextureBinding()
    {
        foreach (var resource in CachedMaskFragmentAbi.Resources)
        {
            if (resource.Kind == ShaderResourceKind.SampledTexture &&
                resource.Stages.HasFlag(ShaderStageMask.Fragment))
            {
                return resource.Binding;
            }
        }

        throw new InvalidOperationException("The cached-mask fragment ABI must expose a sampled fragment texture.");
    }

    private static ShaderBinding GetImageTextureBinding()
    {
        foreach (var resource in ImageFragmentAbi.Resources)
        {
            if (resource.Kind == ShaderResourceKind.SampledTexture &&
                resource.Stages.HasFlag(ShaderStageMask.Fragment))
            {
                return resource.Binding;
            }
        }

        throw new InvalidOperationException("The image fragment ABI must expose a sampled fragment texture.");
    }

    internal static bool TryDescribe(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        out UiRectangleShaderKind shaderKind,
        out uint pushConstantSize,
        out string diagnostic)
        => TryDescribe(program, visualKind, UiVisualShaderPath.Standard, out shaderKind, out pushConstantSize, out diagnostic);

    internal static bool TryDescribe(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        UiVisualShaderPath path,
        out UiRectangleShaderKind shaderKind,
        out uint pushConstantSize,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        shaderKind = default;
        pushConstantSize = 0;
        diagnostic = string.Empty;

        ShaderAbi expectedVertex;
        ShaderAbi expectedFragment;
        if (path is UiVisualShaderPath.SolidStrokeEffect or UiVisualShaderPath.SolidGlowEffect or UiVisualShaderPath.SolidOuterShadowEffect)
        {
            if (visualKind != UiVisualKind.SolidRectangle)
            {
                diagnostic = $"Visual kind {visualKind} cannot use the selected solid effect UI artifact.";
                return false;
            }

            if (path == UiVisualShaderPath.SolidStrokeEffect)
            {
                shaderKind = UiRectangleShaderKind.SolidStroke;
                expectedVertex = SolidStrokeVertexAbi;
                expectedFragment = SolidStrokeFragmentAbi;
            }
            else
            {
                if (path == UiVisualShaderPath.SolidGlowEffect)
                {
                    shaderKind = UiRectangleShaderKind.SolidGlow;
                    expectedVertex = SolidGlowVertexAbi;
                    expectedFragment = SolidGlowFragmentAbi;
                }
                else
                {
                    shaderKind = UiRectangleShaderKind.SolidOuterShadow;
                    expectedVertex = SolidOuterShadowVertexAbi;
                    expectedFragment = SolidOuterShadowFragmentAbi;
                }
            }
        }
        else if (path is UiVisualShaderPath.SolidLinearGradient or UiVisualShaderPath.SolidImage)
        {
            var expectedKind = path == UiVisualShaderPath.SolidImage ? UiVisualKind.Image : UiVisualKind.SolidRectangle;
            if (visualKind != expectedKind)
            {
                diagnostic = $"Visual kind {visualKind} cannot use the selected generated resource artifact.";
                return false;
            }

            if (path == UiVisualShaderPath.SolidLinearGradient)
            {
                shaderKind = UiRectangleShaderKind.SolidLinearGradient;
                expectedVertex = LinearGradientVertexAbi;
                expectedFragment = LinearGradientFragmentAbi;
            }
            else
            {
                shaderKind = UiRectangleShaderKind.SolidImage;
                expectedVertex = ImageVertexAbi;
                expectedFragment = ImageFragmentAbi;
            }
        }
        else if (path is UiVisualShaderPath.AnalyticEffect or UiVisualShaderPath.GlowEffect or UiVisualShaderPath.OuterShadowEffect or UiVisualShaderPath.InsetShadowEffect or UiVisualShaderPath.RoundedStrokeOuterShadowEffect or UiVisualShaderPath.RoundedStrokeGlowEffect or UiVisualShaderPath.CachedMask)
        {
            if (visualKind is not (UiVisualKind.RoundedRectangle or UiVisualKind.Border))
            {
                diagnostic = $"Visual kind {visualKind} cannot use the selected effect UI artifact.";
                return false;
            }

            if (path == UiVisualShaderPath.CachedMask)
            {
                shaderKind = UiRectangleShaderKind.CachedMaskRounded;
                expectedVertex = CachedMaskVertexAbi;
                expectedFragment = CachedMaskFragmentAbi;
            }
            else if (path == UiVisualShaderPath.GlowEffect)
            {
                shaderKind = UiRectangleShaderKind.RoundedGlow;
                expectedVertex = RoundedGlowVertexAbi;
                expectedFragment = RoundedGlowFragmentAbi;
            }
            else if (path == UiVisualShaderPath.OuterShadowEffect)
            {
                shaderKind = UiRectangleShaderKind.RoundedOuterShadow;
                expectedVertex = RoundedOuterShadowVertexAbi;
                expectedFragment = RoundedOuterShadowFragmentAbi;
            }
            else if (path == UiVisualShaderPath.InsetShadowEffect)
            {
                shaderKind = UiRectangleShaderKind.RoundedInsetShadow;
                expectedVertex = RoundedInsetShadowVertexAbi;
                expectedFragment = RoundedInsetShadowFragmentAbi;
            }
            else if (path == UiVisualShaderPath.RoundedStrokeOuterShadowEffect)
            {
                shaderKind = UiRectangleShaderKind.RoundedStrokeOuterShadow;
                expectedVertex = RoundedStrokeOuterShadowVertexAbi;
                expectedFragment = RoundedStrokeOuterShadowFragmentAbi;
            }
            else if (path == UiVisualShaderPath.RoundedStrokeGlowEffect)
            {
                shaderKind = UiRectangleShaderKind.RoundedStrokeGlow;
                expectedVertex = RoundedStrokeGlowVertexAbi;
                expectedFragment = RoundedStrokeGlowFragmentAbi;
            }
            else
            {
                shaderKind = UiRectangleShaderKind.AnalyticRounded;
                expectedVertex = AnalyticVertexAbi;
                expectedFragment = AnalyticFragmentAbi;
            }
        }
        else
        {
            switch (visualKind)
            {
                case UiVisualKind.SolidRectangle:
                    shaderKind = UiRectangleShaderKind.Solid;
                    expectedVertex = SolidVertexAbi;
                    expectedFragment = SolidFragmentAbi;
                    break;
                case UiVisualKind.RoundedRectangle:
                case UiVisualKind.Border:
                    shaderKind = UiRectangleShaderKind.Rounded;
                    expectedVertex = RoundedVertexAbi;
                    expectedFragment = RoundedFragmentAbi;
                    break;
                default:
                    diagnostic = $"Visual kind {visualKind} has no supported generated DeltaRender.UIShaders artifact.";
                    return false;
            }
        }

        var vertex = program.Vertex;
        var fragment = program.Fragment;
        if (vertex is null || fragment is null ||
            !string.Equals(vertex.EntryPoint, "main", StringComparison.Ordinal) ||
            !string.Equals(fragment.EntryPoint, "main", StringComparison.Ordinal) ||
            !SameAbi(vertex.Abi, expectedVertex) || !SameAbi(fragment.Abi, expectedFragment))
        {
            var shaderName = shaderKind switch
            {
                UiRectangleShaderKind.Solid => "solid",
                UiRectangleShaderKind.SolidStroke => "solid-stroke",
                UiRectangleShaderKind.SolidGlow => "solid-glow",
                UiRectangleShaderKind.SolidOuterShadow => "solid-outer-shadow",
                UiRectangleShaderKind.Rounded => "rounded",
                UiRectangleShaderKind.RoundedGlow => "rounded-glow",
                UiRectangleShaderKind.RoundedOuterShadow => "rounded-outer-shadow",
                UiRectangleShaderKind.RoundedInsetShadow => "rounded-inset-shadow",
                UiRectangleShaderKind.RoundedStrokeOuterShadow => "rounded-stroke-outer-shadow",
                UiRectangleShaderKind.RoundedStrokeGlow => "rounded-stroke-glow",
                UiRectangleShaderKind.AnalyticRounded => "analytic-rounded-effect",
                UiRectangleShaderKind.CachedMaskRounded => "cached-mask-rounded",
                UiRectangleShaderKind.SolidLinearGradient => "solid-linear-gradient",
                UiRectangleShaderKind.SolidImage => "solid-image",
                _ => "unknown",
            };
            diagnostic = $"Visual kind {visualKind} requires the matching generated DeltaShader.UI {shaderName}-rectangle ABI.";
            return false;
        }

        pushConstantSize = expectedVertex.PushConstants[0].Size;
        return true;
    }

    internal static bool TryDescribeInstance(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        out UiRectangleShaderKind shaderKind,
        out ShaderBinding instanceBinding,
        out uint instanceStride,
        out uint framePushConstantSize,
        out uint framePushConstantOffset,
        out string diagnostic)
        => TryDescribeInstance(
            program,
            visualKind,
            UiVisualShaderPath.Standard,
            out shaderKind,
            out instanceBinding,
            out instanceStride,
            out framePushConstantSize,
            out framePushConstantOffset,
            out diagnostic);

    internal static bool TryDescribeInstance(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        UiVisualShaderPath path,
        out UiRectangleShaderKind shaderKind,
        out ShaderBinding instanceBinding,
        out uint instanceStride,
        out uint framePushConstantSize,
        out uint framePushConstantOffset,
        out string diagnostic)
    {
        if (!TryDescribe(program, visualKind, path, out shaderKind, out _, out diagnostic))
        {
            instanceBinding = default;
            instanceStride = 0;
            framePushConstantSize = 0;
            framePushConstantOffset = 0;
            return false;
        }

        var resources = program.Vertex.Abi.Resources;
        ShaderResourceBinding? instanceResource = null;
        foreach (var resource in resources)
        {
            if (resource.Kind != ShaderResourceKind.StorageBuffer ||
                !resource.Stages.HasFlag(ShaderStageMask.Vertex) ||
                (resource.Access & ShaderResourceAccess.Write) != 0)
            {
                continue;
            }

            if (instanceResource is not null)
            {
                instanceBinding = default;
                instanceStride = 0;
                framePushConstantSize = 0;
                framePushConstantOffset = 0;
                diagnostic = "The UI vertex ABI contains more than one read-only storage-buffer instance resource.";
                return false;
            }

            instanceResource = resource;
        }

        if (instanceResource is not { } resolvedResource || resolvedResource.Layout.ArrayStride == 0)
        {
            instanceBinding = default;
            instanceStride = 0;
            framePushConstantSize = 0;
            framePushConstantOffset = 0;
            diagnostic = "The UI vertex ABI does not expose a resolved read-only instance storage buffer.";
            return false;
        }

        if (program.Vertex.Abi.PushConstants.Count != 1 || program.Fragment.Abi.PushConstants.Count != 0)
        {
            instanceBinding = default;
            instanceStride = 0;
            framePushConstantSize = 0;
            framePushConstantOffset = 0;
            diagnostic = "The UI graphics ABI must expose one vertex frame push-constant range and no fragment push-constant range.";
            return false;
        }

        var vertexPush = program.Vertex.Abi.PushConstants[0];
        if (vertexPush.Size == 0)
        {
            instanceBinding = default;
            instanceStride = 0;
            framePushConstantSize = 0;
            framePushConstantOffset = 0;
            diagnostic = "The UI vertex ABI must expose a non-empty frame push-constant range.";
            return false;
        }

        instanceBinding = resolvedResource.Binding;
        instanceStride = resolvedResource.Layout.ArrayStride;
        framePushConstantSize = vertexPush.Size;
        framePushConstantOffset = vertexPush.Offset;
        return true;
    }

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        Span<byte> destination)
    {
        return shaderKind switch
        {
            UiRectangleShaderKind.Solid => SolidRectangleGraphicsShaderProgram.PackSolidRectangleVertexInstancesElement(
                new SolidRectangleParameters(visual.Bounds, visual.Paint.FillColor),
                destination),
            UiRectangleShaderKind.Rounded => RoundedRectangleGraphicsShaderProgram.PackRoundedRectangleVertexInstancesElement(
                new RoundedRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.StrokeColor,
                    visual.Paint.CornerRadii,
                    visual.Paint.StrokeWidth),
                destination),
            UiRectangleShaderKind.SolidImage => SolidImageGraphicsShaderProgram.PackSolidImageRectangleVertexInstancesElement(
                new SolidImageRectangleParameters(visual.Bounds, visual.Paint.FillColor, new float4(0f, 0f, 1f, 1f)),
                destination),
            _ => throw new ArgumentOutOfRangeException(nameof(shaderKind), shaderKind, "Unknown UI rectangle shader kind."),
        };
    }

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, default, 1f, destination);

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, 1f, destination);

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        float dpiScale,
        Span<byte> destination)
    {
        if (shaderKind == UiRectangleShaderKind.CachedMaskRounded)
        {
            return CachedMaskRoundedRectangleGraphicsShaderProgram.PackCachedMaskRoundedRectangleVertexInstancesElement(
                new CachedMaskRoundedRectangleParameters(
                    visual.Bounds,
                    maskUvRect,
                    visual.Paint.FillColor),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.RoundedGlow)
        {
            var glow = effectResource.Parameters.Glow;
            return RoundedGlowGraphicsShaderProgram.PackGlowRoundedRectangleVertexInstancesElement(
                new GlowRoundedRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.CornerRadii,
                    ToShaderEffectLayer(glow, effectResource.Parameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.RoundedOuterShadow)
        {
            var shadow = effectResource.Parameters.OuterShadow;
            return RoundedOuterShadowGraphicsShaderProgram.PackOuterShadowRoundedRectangleVertexInstancesElement(
                new OuterShadowRoundedRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.CornerRadii,
                    ToShaderEffectLayer(shadow, effectResource.Parameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.SolidStroke)
        {
            var stroke = effectResource.Parameters.StrokeOrOutline;
            return SolidStrokeGraphicsShaderProgram.PackSolidStrokeRectangleVertexInstancesElement(
                new SolidStrokeRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    stroke.Color,
                    stroke.Width * (effectResource.Parameters.Units == PaintUnits.Logical ? dpiScale : 1f)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.SolidGlow)
        {
            var glow = effectResource.Parameters.Glow;
            return SolidGlowGraphicsShaderProgram.PackSolidGlowRectangleVertexInstancesElement(
                new SolidGlowRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    ToShaderEffectLayer(glow, effectResource.Parameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.SolidOuterShadow)
        {
            var shadow = effectResource.Parameters.OuterShadow;
            return SolidOuterShadowGraphicsShaderProgram.PackSolidOuterShadowRectangleVertexInstancesElement(
                new SolidOuterShadowRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    ToShaderEffectLayer(shadow, effectResource.Parameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.RoundedStrokeOuterShadow)
        {
            var effectParameters = effectResource.Parameters;
            return RoundedStrokeOuterShadowGraphicsShaderProgram.PackRoundedStrokeOuterShadowVertexInstancesElement(
                new RoundedStrokeOuterShadowParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.CornerRadii,
                    ToShaderEffectLayer(effectParameters.StrokeOrOutline, effectParameters.Units, dpiScale),
                    ToShaderEffectLayer(effectParameters.OuterShadow, effectParameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.RoundedStrokeGlow)
        {
            var effectParameters = effectResource.Parameters;
            return RoundedStrokeGlowGraphicsShaderProgram.PackRoundedStrokeGlowVertexInstancesElement(
                new RoundedStrokeGlowParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.CornerRadii,
                    ToShaderEffectLayer(effectParameters.StrokeOrOutline, effectParameters.Units, dpiScale),
                    ToShaderEffectLayer(effectParameters.Glow, effectParameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind == UiRectangleShaderKind.RoundedInsetShadow)
        {
            var shadow = effectResource.Parameters.InsetShadow;
            return RoundedInsetShadowGraphicsShaderProgram.PackInsetShadowRoundedRectangleVertexInstancesElement(
                new InsetShadowRoundedRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.CornerRadii,
                    ToShaderEffectLayer(shadow, effectResource.Parameters.Units, dpiScale)),
                destination);
        }

        if (shaderKind != UiRectangleShaderKind.AnalyticRounded)
        {
            return PackInstance(shaderKind, in visual, destination);
        }

        var parameters = effectResource.Parameters;
        var effects = new ShaderEffectParameters(
            ToShaderEffectLayer(parameters.StrokeOrOutline, parameters.Units, dpiScale),
            ToShaderEffectLayer(parameters.OuterShadow, parameters.Units, dpiScale),
            ToShaderEffectLayer(parameters.InsetShadow, parameters.Units, dpiScale),
            ToShaderEffectLayer(parameters.Glow, parameters.Units, dpiScale));
        return AnalyticRoundedRectangleGraphicsShaderProgram.PackAnalyticRoundedRectangleVertexInstancesElement(
            new AnalyticRoundedRectangleParameters(
                visual.Bounds,
                visual.Paint.FillColor,
                visual.Paint.CornerRadii,
                effects),
            destination);
    }

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        float dpiScale,
        UiLinearGradientResource? gradientResource,
        Span<byte> destination)
    {
        if (shaderKind == UiRectangleShaderKind.SolidLinearGradient)
        {
            if (gradientResource is not { } gradient || gradient.Stops.Count is < 2 or > 4)
            {
                throw new InvalidOperationException("A linear-gradient visual requires a registered two-to-four-stop resource.");
            }

            var stop0 = gradient.Stops[0];
            var stop1 = gradient.Stops[1];
            var stop2 = gradient.Stops.Count > 2 ? gradient.Stops[2] : stop1;
            var stop3 = gradient.Stops.Count > 3 ? gradient.Stops[3] : stop2;
            var start = ToPhysical(gradient.Start, gradient.Units, dpiScale);
            var end = ToPhysical(gradient.End, gradient.Units, dpiScale);
            return SolidLinearGradientGraphicsShaderProgram.PackSolidLinearGradientVertexInstancesElement(
                new SolidLinearGradientParameters(
                    visual.Bounds,
                    new float4(start.x, start.y, end.x, end.y),
                    stop0.Color,
                    stop1.Color,
                    stop2.Color,
                    stop3.Color,
                    new float4(stop0.Position, stop1.Position, stop2.Position, stop3.Position),
                    gradient.Stops.Count),
                destination);
        }

        return PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, dpiScale, destination);
    }

    internal static int PackInstance(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, destination);

    internal static int MaxInstanceCount(UiRectangleShaderKind shaderKind) => 1;

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        uint instanceStride,
        Span<byte> destination)
    {
        return PackInstance(shaderKind, in visual, destination);
    }

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, destination);

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, destination);

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        uint instanceStride,
        Span<byte> destination)
    {
        return PackInstance(shaderKind, in visual, destination);
    }

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        in UiEffectResource effectResource,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, destination);

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, destination);

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        float dpiScale,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, dpiScale, destination);

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        in UiEffectResource effectResource,
        in float4 maskUvRect,
        float dpiScale,
        UiLinearGradientResource? gradientResource,
        uint instanceStride,
        Span<byte> destination)
        => PackInstance(shaderKind, in visual, in effectResource, in maskUvRect, dpiScale, gradientResource, destination);

    internal static int PackFrame(
        UiRectangleShaderKind shaderKind,
        PixelExtent viewport,
        Span<byte> destination)
    {
        var frame = new UiFrameConstants(new float2(viewport.Width, viewport.Height));
        return shaderKind switch
        {
            UiRectangleShaderKind.Solid => SolidRectangleGraphicsShaderProgram.PackSolidRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.SolidStroke => SolidStrokeGraphicsShaderProgram.PackSolidStrokeRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.SolidGlow => SolidGlowGraphicsShaderProgram.PackSolidGlowRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.SolidOuterShadow => SolidOuterShadowGraphicsShaderProgram.PackSolidOuterShadowRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.Rounded => RoundedRectangleGraphicsShaderProgram.PackRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedGlow => RoundedGlowGraphicsShaderProgram.PackGlowRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedOuterShadow => RoundedOuterShadowGraphicsShaderProgram.PackOuterShadowRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedInsetShadow => RoundedInsetShadowGraphicsShaderProgram.PackInsetShadowRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedStrokeOuterShadow => RoundedStrokeOuterShadowGraphicsShaderProgram.PackRoundedStrokeOuterShadowVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedStrokeGlow => RoundedStrokeGlowGraphicsShaderProgram.PackRoundedStrokeGlowVertexFrame(in frame, destination),
            UiRectangleShaderKind.AnalyticRounded => AnalyticRoundedRectangleGraphicsShaderProgram.PackAnalyticRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.CachedMaskRounded => CachedMaskRoundedRectangleGraphicsShaderProgram.PackCachedMaskRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.SolidLinearGradient => SolidLinearGradientGraphicsShaderProgram.PackSolidLinearGradientVertexFrame(in frame, destination),
            UiRectangleShaderKind.SolidImage => SolidImageGraphicsShaderProgram.PackSolidImageRectangleVertexFrame(in frame, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(shaderKind), shaderKind, "Unknown UI rectangle shader kind."),
        };
    }

    internal static bool UsesShaderClip(UiRectangleShaderKind shaderKind) => false;

    private static ShaderEffectLayer ToShaderEffectLayer(UiEffectLayer layer, PaintUnits units, float dpiScale)
    {
        var scale = units switch
        {
            PaintUnits.Logical => dpiScale,
            PaintUnits.Device => 1f,
            _ => throw new ArgumentOutOfRangeException(nameof(units), units, "Unknown paint unit system."),
        };
        return new(
            layer.Color,
            layer.Offset * scale,
            layer.Width * scale,
            layer.BlurRadius * scale,
            layer.Spread * scale,
            layer.Intensity);
    }

    private static float2 ToPhysical(float2 value, PaintUnits units, float dpiScale)
        => units == PaintUnits.Logical ? value * dpiScale : value;



    private static bool SameAbi(ShaderAbi actual, ShaderAbi expected)
        => actual.Stage == expected.Stage &&
           actual.RequiredCapabilities == expected.RequiredCapabilities &&
           actual.WorkgroupSize == expected.WorkgroupSize &&
           SameResources(actual.Resources, expected.Resources) &&
           SamePushConstants(actual.PushConstants, expected.PushConstants) &&
           SameInterfaces(actual.Inputs, expected.Inputs) &&
           SameInterfaces(actual.Outputs, expected.Outputs) &&
           SameVertexInputs(actual.VertexInputs, expected.VertexInputs) &&
           SameVertexBuffers(actual.VertexBuffers, expected.VertexBuffers) &&
           SameSpecializationConstants(actual.SpecializationConstants, expected.SpecializationConstants);

    private static bool SameResources(IReadOnlyList<ShaderResourceBinding> actual, IReadOnlyList<ShaderResourceBinding> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            var left = actual[index];
            var right = expected[index];
            if (left.Binding != right.Binding || left.Kind != right.Kind || left.Access != right.Access ||
                left.Stages != right.Stages || left.DescriptorCount != right.DescriptorCount ||
                !SameLayout(left.Layout, right.Layout))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SamePushConstants(IReadOnlyList<ShaderPushConstantRange> actual, IReadOnlyList<ShaderPushConstantRange> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            var left = actual[index];
            var right = expected[index];
            if (left.Offset != right.Offset || left.Size != right.Size || left.Stages != right.Stages ||
                !SameLayout(left.Layout, right.Layout))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameInterfaces(IReadOnlyList<ShaderInterfaceVariable> actual, IReadOnlyList<ShaderInterfaceVariable> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameVertexInputs(IReadOnlyList<ShaderVertexInput> actual, IReadOnlyList<ShaderVertexInput> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameVertexBuffers(IReadOnlyList<ShaderVertexBufferLayout> actual, IReadOnlyList<ShaderVertexBufferLayout> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameSpecializationConstants(
        IReadOnlyList<ShaderSpecializationConstant> actual,
        IReadOnlyList<ShaderSpecializationConstant> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            var left = actual[index];
            var right = expected[index];
            if (left is null || right is null || left.Id != right.Id || left.Type != right.Type ||
                !left.DefaultValue.SequenceEqual(right.DefaultValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameLayout(ShaderAbiLayout actual, ShaderAbiLayout expected)
    {
        if (actual.Size != expected.Size || actual.Alignment != expected.Alignment ||
            actual.ArrayStride != expected.ArrayStride || actual.MatrixStride != expected.MatrixStride ||
            actual.Members.Count != expected.Members.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Members.Count; index++)
        {
            var left = actual.Members[index];
            var right = expected.Members[index];
            if (left.Type != right.Type || left.Offset != right.Offset || left.Size != right.Size ||
                left.Alignment != right.Alignment || left.ArrayStride != right.ArrayStride ||
                left.MatrixStride != right.MatrixStride || !SameNestedLayout(left.NestedLayout, right.NestedLayout))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameNestedLayout(ShaderAbiLayout? actual, ShaderAbiLayout? expected)
        => actual is null || expected is null ? actual is null && expected is null : SameLayout(actual, expected);

}
