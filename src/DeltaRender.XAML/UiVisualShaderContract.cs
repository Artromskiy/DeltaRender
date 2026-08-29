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
}

internal static class UiVisualShaderContract
{
    private static readonly ShaderAbi SolidVertexAbi = SolidRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi SolidFragmentAbi = SolidRectangleGraphicsShaderProgram.FragmentAbi;
    private static readonly ShaderAbi RoundedVertexAbi = RoundedRectangleGraphicsShaderProgram.VertexAbi;
    private static readonly ShaderAbi RoundedFragmentAbi = RoundedRectangleGraphicsShaderProgram.FragmentAbi;

    internal static int MaxPushConstantSize { get; } = checked((int)Math.Max(
        SolidVertexAbi.PushConstants[0].Size,
        RoundedVertexAbi.PushConstants[0].Size));

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

    internal static int Pack(
        UiRectangleShaderKind shaderKind,
        in UiVisualDraw visual,
        PixelExtent viewport,
        Span<byte> destination)
    {
        var resolution = new float2(viewport.Width, viewport.Height);
        return shaderKind switch
        {
            UiRectangleShaderKind.Solid => SolidRectangleGraphicsShaderProgram.PackSolidRectangleVertexParameters(
                new SolidRectangleParameters
                {
                    Resolution = resolution,
                    Rect = visual.Bounds,
                    Color = visual.Paint.FillColor,
                },
                destination),
            UiRectangleShaderKind.Rounded => RoundedRectangleGraphicsShaderProgram.PackRoundedRectangleVertexParameters(
                new RoundedRectangleParameters
                {
                    Resolution = resolution,
                    Rect = visual.Bounds,
                    FillColor = visual.Paint.FillColor,
                    BorderColor = visual.Paint.StrokeColor,
                    CornerRadii = visual.Paint.CornerRadii,
                    BorderWidth = visual.Paint.StrokeWidth,
                },
                destination),
            _ => throw new ArgumentOutOfRangeException(nameof(shaderKind), shaderKind, "Unknown UI rectangle shader kind."),
        };
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
