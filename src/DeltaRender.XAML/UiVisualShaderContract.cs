using Delta.Maths;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;
using Delta.Shader.UI;
using Delta.XAML.Contract;

namespace Delta.Render.XAML;

internal enum UiRectangleShaderKind : byte
{
    Solid,
    Rounded,
    RoundedSlice,
    ClipAwareSolid,
    ClipAwareRounded,
    ClipAwareRoundedSlice,
}

internal static class UiVisualShaderContract
{
    private static readonly ShaderAbi SolidVertexAbi = SolidRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidFragmentAbi = SolidRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedVertexAbi = RoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedFragmentAbi = RoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedSliceVertexAbi = RoundedRectangleSliceGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedSliceFragmentAbi = RoundedRectangleSliceGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi ClipAwareSolidVertexAbi = ClipAwareSolidRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi ClipAwareSolidFragmentAbi = ClipAwareSolidRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi ClipAwareRoundedVertexAbi = ClipAwareRoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi ClipAwareRoundedFragmentAbi = ClipAwareRoundedRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi ClipAwareRoundedSliceVertexAbi = ClipAwareRoundedRectangleSliceGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi ClipAwareRoundedSliceFragmentAbi = ClipAwareRoundedRectangleSliceGraphicsShaderProgram.FragmentAbi;

    internal static int MaxPushConstantSize { get; } = GetMaxPushConstantSize();

    private static int GetMaxPushConstantSize()
    {
        var size = SolidVertexAbi.PushConstants[0].Size;
        size = Math.Max(size, RoundedVertexAbi.PushConstants[0].Size);
        size = Math.Max(size, RoundedSliceVertexAbi.PushConstants[0].Size);
        size = Math.Max(size, ClipAwareSolidVertexAbi.PushConstants[0].Size);
        size = Math.Max(size, ClipAwareRoundedVertexAbi.PushConstants[0].Size);
        size = Math.Max(size, ClipAwareRoundedSliceVertexAbi.PushConstants[0].Size);
        return checked((int)size);
    }

    internal static bool TryDescribe(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        out UiRectangleShaderKind shaderKind,
        out uint pushConstantSize,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        shaderKind = default;
        pushConstantSize = 0;
        diagnostic = string.Empty;

        if (TryDescribeClipAware(program, visualKind, out shaderKind, out pushConstantSize))
        {
            return true;
        }

        if ((visualKind == UiVisualKind.RoundedRectangle || visualKind == UiVisualKind.Border) &&
            program.Vertex is { } sliceVertex &&
            program.Fragment is { } sliceFragment &&
            string.Equals(sliceVertex.EntryPoint, "main", StringComparison.Ordinal) &&
            string.Equals(sliceFragment.EntryPoint, "main", StringComparison.Ordinal) &&
            SameAbi(sliceVertex.Abi, RoundedSliceVertexAbi) &&
            SameAbi(sliceFragment.Abi, RoundedSliceFragmentAbi))
        {
            shaderKind = UiRectangleShaderKind.RoundedSlice;
            pushConstantSize = RoundedSliceVertexAbi.PushConstants[0].Size;
            return true;
        }

        ShaderAbi expectedVertex;
        ShaderAbi expectedFragment;
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
                diagnostic = $"Visual kind {visualKind} has no supported generated DeltaShader.UI artifact.";
                return false;
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
    {
        if (!TryDescribe(program, visualKind, out shaderKind, out _, out diagnostic))
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
        in PixelRect clip,
        Span<byte> destination)
    {
        var clipRect = new float4(clip.X, clip.Y, clip.Width, clip.Height);
        return shaderKind switch
        {
            UiRectangleShaderKind.ClipAwareSolid => ClipAwareSolidRectangleGraphicsShaderProgram.PackClipAwareSolidRectangleVertexInstancesElement(
                new ClipAwareSolidRectangleParameters(visual.Bounds, visual.Paint.FillColor, clipRect),
                destination),
            UiRectangleShaderKind.ClipAwareRounded => ClipAwareRoundedRectangleGraphicsShaderProgram.PackClipAwareRoundedRectangleVertexInstancesElement(
                new ClipAwareRoundedRectangleParameters(
                    visual.Bounds,
                    visual.Paint.FillColor,
                    visual.Paint.StrokeColor,
                    visual.Paint.CornerRadii,
                    visual.Paint.StrokeWidth,
                    clipRect),
                destination),
            _ => throw new ArgumentOutOfRangeException(nameof(shaderKind), shaderKind, "Unknown clip-aware UI rectangle shader kind."),
        };
    }

    internal static int MaxInstanceCount(UiRectangleShaderKind shaderKind)
        => shaderKind is UiRectangleShaderKind.RoundedSlice or UiRectangleShaderKind.ClipAwareRoundedSlice ? 9 : 1;

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        uint instanceStride,
        Span<byte> destination)
    {
        if (shaderKind != UiRectangleShaderKind.RoundedSlice)
        {
            return PackInstance(shaderKind, in visual, destination);
        }

        return UiRoundedRectangleSlicePacker.Pack(in visual, instanceStride, destination);
    }

    internal static int PackInstances(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        in PixelRect clip,
        uint instanceStride,
        Span<byte> destination)
    {
        if (shaderKind is UiRectangleShaderKind.ClipAwareSolid or UiRectangleShaderKind.ClipAwareRounded)
        {
            return PackInstance(shaderKind, in visual, in clip, destination);
        }

        if (shaderKind == UiRectangleShaderKind.ClipAwareRoundedSlice)
        {
            return UiRoundedRectangleSlicePacker.PackClipAware(in visual, instanceStride, in clip, destination);
        }

        return PackInstances(shaderKind, in visual, instanceStride, destination);
    }

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
            UiRectangleShaderKind.RoundedSlice => RoundedRectangleSliceGraphicsShaderProgram.PackRoundedRectangleSliceVertexFrame(in frame, destination),
            UiRectangleShaderKind.ClipAwareSolid => ClipAwareSolidRectangleGraphicsShaderProgram.PackClipAwareSolidRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.ClipAwareRounded => ClipAwareRoundedRectangleGraphicsShaderProgram.PackClipAwareRoundedRectangleVertexFrame(in frame, destination),
            UiRectangleShaderKind.ClipAwareRoundedSlice => ClipAwareRoundedRectangleSliceGraphicsShaderProgram.PackClipAwareRoundedRectangleSliceVertexFrame(in frame, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(shaderKind), shaderKind, "Unknown UI rectangle shader kind."),
        };
    }

    internal static bool UsesShaderClip(UiRectangleShaderKind shaderKind)
        => shaderKind is UiRectangleShaderKind.ClipAwareSolid or
            UiRectangleShaderKind.ClipAwareRounded or
            UiRectangleShaderKind.ClipAwareRoundedSlice;

    private static bool TryDescribeClipAware(
        IGraphicsShaderProgram program,
        UiVisualKind visualKind,
        out UiRectangleShaderKind shaderKind,
        out uint pushConstantSize)
    {
        shaderKind = default;
        pushConstantSize = 0;
        if (visualKind == UiVisualKind.SolidRectangle && IsProgram(program, ClipAwareSolidVertexAbi, ClipAwareSolidFragmentAbi))
        {
            shaderKind = UiRectangleShaderKind.ClipAwareSolid;
            pushConstantSize = ClipAwareSolidVertexAbi.PushConstants[0].Size;
            return true;
        }

        if (visualKind is not (UiVisualKind.RoundedRectangle or UiVisualKind.Border))
        {
            return false;
        }

        if (IsProgram(program, ClipAwareRoundedSliceVertexAbi, ClipAwareRoundedSliceFragmentAbi))
        {
            shaderKind = UiRectangleShaderKind.ClipAwareRoundedSlice;
            pushConstantSize = ClipAwareRoundedSliceVertexAbi.PushConstants[0].Size;
            return true;
        }

        if (IsProgram(program, ClipAwareRoundedVertexAbi, ClipAwareRoundedFragmentAbi))
        {
            shaderKind = UiRectangleShaderKind.ClipAwareRounded;
            pushConstantSize = ClipAwareRoundedVertexAbi.PushConstants[0].Size;
            return true;
        }

        return false;
    }

    private static bool IsProgram(IGraphicsShaderProgram program, ShaderAbi vertexAbi, ShaderAbi fragmentAbi)
        => program.Vertex is { } vertex &&
           program.Fragment is { } fragment &&
           string.Equals(vertex.EntryPoint, "main", StringComparison.Ordinal) &&
           string.Equals(fragment.EntryPoint, "main", StringComparison.Ordinal) &&
           SameAbi(vertex.Abi, vertexAbi) &&
           SameAbi(fragment.Abi, fragmentAbi);

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
