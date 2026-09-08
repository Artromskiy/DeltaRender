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
    Rounded,
    RoundedGlow,
    RoundedOuterShadow,
    AnalyticRounded,
    CachedMaskRounded,
}

internal static class UiVisualShaderContract
{
    private static readonly ShaderAbi SolidVertexAbi = SolidRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidFragmentAbi = SolidRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedVertexAbi = RoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedFragmentAbi = RoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedGlowVertexAbi = RoundedGlowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedGlowFragmentAbi = RoundedGlowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedOuterShadowVertexAbi = RoundedOuterShadowGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedOuterShadowFragmentAbi = RoundedOuterShadowGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi AnalyticVertexAbi = AnalyticRoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi AnalyticFragmentAbi = AnalyticRoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi CachedMaskVertexAbi = CachedMaskRoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi CachedMaskFragmentAbi = CachedMaskRoundedRectangleGraphicsShaderProgram.FragmentAbi;

    internal static int MaxPushConstantSize { get; } = GetMaxPushConstantSize();

    internal static ShaderBinding CachedMaskTextureBinding { get; } = GetCachedMaskTextureBinding();

    private static int GetMaxPushConstantSize()
    {
        var size = SolidVertexAbi.PushConstants[0].Size;
        size = Maths.Max(size, RoundedVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedGlowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, RoundedOuterShadowVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, AnalyticVertexAbi.PushConstants[0].Size);
        size = Maths.Max(size, CachedMaskVertexAbi.PushConstants[0].Size);
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
        if (path is UiVisualShaderPath.AnalyticEffect or UiVisualShaderPath.GlowEffect or UiVisualShaderPath.OuterShadowEffect or UiVisualShaderPath.CachedMask)
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
                UiRectangleShaderKind.Rounded => "rounded",
                UiRectangleShaderKind.RoundedGlow => "rounded-glow",
                UiRectangleShaderKind.RoundedOuterShadow => "rounded-outer-shadow",
                UiRectangleShaderKind.AnalyticRounded => "analytic-rounded-effect",
                UiRectangleShaderKind.CachedMaskRounded => "cached-mask-rounded",
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

    internal static int PackFrame(
        UiRectangleShaderKind shaderKind,
        PixelExtent viewport,
        Span<byte> destination)
    {
        var frame = new UiFrameConstants(new float2(viewport.Width, viewport.Height));
        return shaderKind switch
        {
            UiRectangleShaderKind.Solid => SolidRectangleGraphicsShaderProgram.PackSolidRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.Rounded => RoundedRectangleGraphicsShaderProgram.PackRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedGlow => RoundedGlowGraphicsShaderProgram.PackGlowRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.RoundedOuterShadow => RoundedOuterShadowGraphicsShaderProgram.PackOuterShadowRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.AnalyticRounded => AnalyticRoundedRectangleGraphicsShaderProgram.PackAnalyticRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.CachedMaskRounded => CachedMaskRoundedRectangleGraphicsShaderProgram.PackCachedMaskRoundedRectangleVertexFrame(in frame, destination),
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
