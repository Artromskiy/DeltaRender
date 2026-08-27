using System.Text.Json;
using Delta.Shader.Contract;

namespace Delta.Render.HeadlessShaderPlayground;

internal static class ShaderManifestFixtureLoader
{
    public static IGraphicsShaderProgram LoadGraphicsProgram(
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        string vertexManifestPath,
        string fragmentManifestPath)
    {
        ArgumentNullException.ThrowIfNull(vertexSpirv);
        ArgumentNullException.ThrowIfNull(fragmentSpirv);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentManifestPath);

        var vertex = LoadArtifact(vertexSpirv, vertexManifestPath, ShaderStage.Vertex);
        var fragment = LoadArtifact(fragmentSpirv, fragmentManifestPath, ShaderStage.Fragment);
        return new GraphicsShaderProgram(vertex, fragment);
    }

    private static ShaderArtifact LoadArtifact(
        ReadOnlySpan<byte> spirv,
        string manifestPath,
        ShaderStage expectedStage)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Shader manifest sidecar is required for the headless fixture: {manifestPath}",
                manifestPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var stage = ParseStage(GetRequiredInt32(root, "Stage"));
        if (stage != expectedStage)
        {
            throw new InvalidDataException($"Manifest '{manifestPath}' declares {stage}, expected {expectedStage}.");
        }

        var entryPoint = GetRequiredString(root, "EntryPointName");
        var resources = GetRequiredArray(root, "Resources");
        if (resources.GetArrayLength() != 0)
        {
            throw new NotSupportedException(
                $"The headless fixture loader accepts descriptor-free graphics pairs; '{manifestPath}' declares resources.");
        }

        var abi = new ShaderAbi(
            stage,
            pushConstants: ParsePushConstants(root, stage),
            inputs: ParseInterfaces(root, "Inputs"),
            outputs: ParseInterfaces(root, "Outputs"));
        return new ShaderArtifact(spirv, entryPoint, abi);
    }

    private static ShaderPushConstantRange[] ParsePushConstants(JsonElement root, ShaderStage stage)
    {
        var blocks = GetRequiredArray(root, "PushConstants");
        var result = new ShaderPushConstantRange[blocks.GetArrayLength()];
        var stageMask = stage == ShaderStage.Vertex ? ShaderStageMask.Vertex : ShaderStageMask.Fragment;
        var index = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            var layout = ParseLayout(block);
            result[index++] = new ShaderPushConstantRange(0, layout.Size, stageMask, layout);
        }

        return result;
    }

    private static ShaderAbiLayout ParseLayout(JsonElement value)
    {
        var membersValue = value.TryGetProperty("Members", out var membersProperty)
            ? membersProperty
            : default;
        var members = membersValue.ValueKind == JsonValueKind.Array
            ? ParseMembers(membersValue)
            : [];

        return new ShaderAbiLayout(
            GetRequiredUInt32(value, "Size"),
            GetRequiredUInt32(value, "Alignment"),
            GetOptionalUInt32(value, "ArrayStride"),
            GetOptionalUInt32(value, "MatrixStride"),
            members);
    }

    private static ShaderAbiMember[] ParseMembers(JsonElement value)
    {
        var result = new ShaderAbiMember[value.GetArrayLength()];
        var index = 0;
        foreach (var member in value.EnumerateArray())
        {
            var nestedMembers = member.TryGetProperty("Members", out var nestedProperty)
                ? nestedProperty
                : default;
            var nestedLayout = nestedMembers.ValueKind == JsonValueKind.Array && nestedMembers.GetArrayLength() != 0
                ? ParseLayout(member)
                : null;
            var hostRepresentation = member.TryGetProperty("HostRepresentation", out var hostProperty) &&
                hostProperty.ValueKind == JsonValueKind.String
                ? hostProperty.GetString()
                : null;
            if (!string.Equals(hostRepresentation, "std430", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The headless fixture loader requires std430 ABI metadata.");
            }

            result[index++] = new ShaderAbiMember(
                ParseValueType(GetRequiredString(member, "GlslType")),
                GetRequiredUInt32(member, "Offset"),
                GetRequiredUInt32(member, "Size"),
                GetRequiredUInt32(member, "Alignment"),
                GetOptionalUInt32(member, "ArrayStride"),
                GetOptionalUInt32(member, "MatrixStride"),
                nestedLayout);
        }

        return result;
    }

    private static ShaderInterfaceVariable[] ParseInterfaces(JsonElement root, string propertyName)
    {
        var values = GetRequiredArray(root, propertyName);
        var result = new ShaderInterfaceVariable[values.GetArrayLength()];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            uint? location = null;
            if (value.TryGetProperty("Location", out var locationProperty) &&
                locationProperty.ValueKind != JsonValueKind.Null)
            {
                location = locationProperty.GetUInt32();
            }

            result[index++] = new ShaderInterfaceVariable(
                ParseValueType(GetRequiredString(value, "GlslType")),
                location,
                ParseBuiltin(value));
        }

        return result;
    }

    private static ShaderBuiltin ParseBuiltin(JsonElement value)
    {
        if (!value.TryGetProperty("Builtin", out var builtinProperty) ||
            builtinProperty.ValueKind == JsonValueKind.Null)
        {
            return ShaderBuiltin.None;
        }

        var builtin = GetRequiredString(value, "Builtin");
        return builtin switch
        {
            "Position" => ShaderBuiltin.Position,
            "VertexIndex" => ShaderBuiltin.VertexIndex,
            "FragmentPosition" => ShaderBuiltin.FragmentCoordinate,
            "FragmentColor" => ShaderBuiltin.None,
            _ => throw new InvalidDataException($"Unsupported graphics builtin '{builtin}'.")
        };
    }

    private static ShaderValueType ParseValueType(string glslType)
    {
        return glslType switch
        {
            "float" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32),
            "vec2" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 2),
            "vec3" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 3),
            "vec4" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 4),
            "int" => new ShaderValueType(ShaderValueKind.SignedInteger, 32),
            "ivec2" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 2),
            "ivec3" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 3),
            "ivec4" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 4),
            "uint" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32),
            "uvec2" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 2),
            "uvec3" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 3),
            "uvec4" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 4),
            _ when glslType.StartsWith("DeltaStruct_", StringComparison.Ordinal) => ShaderValueType.Structure,
            _ => throw new InvalidDataException($"Unsupported fixture GLSL type '{glslType}'.")
        };
    }

    private static ShaderStage ParseStage(int value) => value switch
    {
        1 => ShaderStage.Vertex,
        2 => ShaderStage.Fragment,
        _ => throw new InvalidDataException($"Unsupported graphics fixture stage value {value}.")
    };

    private static JsonElement GetRequiredArray(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Manifest property '{propertyName}' must be an array.");
        }

        return property;
    }

    private static string GetRequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } result ||
            string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Manifest property '{propertyName}' must be a non-empty string.");
        }

        return result;
    }

    private static int GetRequiredInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"Manifest property '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static uint GetRequiredUInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || !property.TryGetUInt32(out var result))
        {
            throw new InvalidDataException($"Manifest property '{propertyName}' must be an unsigned integer.");
        }

        return result;
    }

    private static uint GetOptionalUInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        return property.TryGetUInt32(out var result)
            ? result
            : throw new InvalidDataException($"Manifest property '{propertyName}' must be an unsigned integer or null.");
    }
}
