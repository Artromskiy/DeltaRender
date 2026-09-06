using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class ShaderArtifactLoader
{
    public static IReadOnlyList<LoadedArtifact> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Artifact directory was not found: {directory}");
        }

        var manifests = Directory.GetFiles(directory, "*.shader.json", SearchOption.AllDirectories);
        var result = new List<LoadedArtifact>(manifests.Length);
        foreach (var manifestPath in manifests.Order(StringComparer.Ordinal))
        {
            if (!File.Exists(GetSpirvPath(manifestPath)))
            {
                continue;
            }

            try
            {
                result.Add(Load(manifestPath));
            }
            catch (Exception exception) when (RunnerFailure.IsReportable(exception))
            {
                throw new InvalidDataException($"Manifest '{manifestPath}' is invalid: {exception}", exception);
            }
        }

        return result;
    }

    private static LoadedArtifact Load(string manifestPath)
    {
        var spirvPath = GetSpirvPath(manifestPath);
        if (!File.Exists(spirvPath))
        {
            throw new FileNotFoundException($"SPIR-V sidecar was not found for '{manifestPath}'.", spirvPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var abiDocument = LoadAbiDocument(manifestPath);
        using (abiDocument)
        {
            var abiRoot = abiDocument is null ? root : RequiredObject(abiDocument.RootElement, "Abi");
            if (!IsComputeStage(abiRoot, "Stage"))
            {
                throw new InvalidDataException($"Only compute artifacts are accepted: '{manifestPath}'.");
            }

            var entryPoint = GetRequiredString(root, "EntryPointName");
            var resources = ParseResources(abiRoot);
            var pushConstants = ParsePushConstants(abiRoot);
            var localSize = ParseWorkgroupSize(abiRoot);
            var requiredCapabilities = ParseRequiredCapabilities(abiRoot);
            var abi = new ShaderAbi(
                ShaderStage.Compute,
                resources,
                pushConstants,
                workgroupSize: localSize,
                requiredCapabilities: requiredCapabilities);
            var artifact = new ShaderArtifact(File.ReadAllBytes(spirvPath), entryPoint, abi);
            var metadata = LoadIdentityMetadata(manifestPath, root);
            return new LoadedArtifact(
                manifestPath,
                artifact,
                metadata.CaseIds,
                metadata.OperationIdentity);
        }
    }

    private static string GetSpirvPath(string manifestPath)
        => manifestPath.EndsWith(".shader.json", StringComparison.Ordinal)
            ? manifestPath[..^".shader.json".Length] + ".spv"
            : throw new InvalidDataException($"Unexpected shader manifest path '{manifestPath}'.");

    private static ShaderResourceBinding[] ParseResources(JsonElement root)
    {
        var values = RequiredArray(root, "Resources");
        var result = new ShaderResourceBinding[values.GetArrayLength()];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var canonical = value.TryGetProperty("Binding", out var bindingValue) && bindingValue.ValueKind == JsonValueKind.Object;
            var category = canonical ? OptionalString(value, "Kind") : OptionalString(value, "Category");
            if (canonical
                ? !string.Equals(category, "StorageBuffer", StringComparison.Ordinal)
                : !string.Equals(category, "storage-buffer", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Conformance artifacts may contain only storage-buffer resources.");
            }

            var readOnly = value.TryGetProperty("ReadOnly", out var readOnlyProperty) &&
                readOnlyProperty.ValueKind == JsonValueKind.True;
            var access = readOnly ? ShaderResourceAccess.Read : ParseAccess(value);
            result[index++] = new ShaderResourceBinding(
                canonical
                    ? new ShaderBinding(GetRequiredUInt32(bindingValue, "Set"), GetRequiredUInt32(bindingValue, "Binding"))
                    : new ShaderBinding(GetRequiredUInt32(value, "Set"), GetRequiredUInt32(value, "Binding")),
                ShaderResourceKind.StorageBuffer,
                access,
                ShaderStageMask.Compute,
                canonical ? ParseLayout(RequiredObject(value, "Layout")) : ParseLayout(value));
        }

        return result;
    }

    private static ShaderPushConstantRange[] ParsePushConstants(JsonElement root)
    {
        var values = RequiredArray(root, "PushConstants");
        var result = new ShaderPushConstantRange[values.GetArrayLength()];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var layout = value.TryGetProperty("Layout", out var layoutValue) && layoutValue.ValueKind == JsonValueKind.Object
                ? ParseLayout(layoutValue)
                : ParseLayout(value);
            var offset = OptionalUInt32(value, "Offset");
            result[index++] = new ShaderPushConstantRange(offset, layout.Size, ShaderStageMask.Compute, layout);
        }

        return result;
    }

    private static ShaderAbiLayout ParseLayout(JsonElement value)
    {
        var members = value.TryGetProperty("Members", out var membersProperty) &&
            membersProperty.ValueKind == JsonValueKind.Array
            ? ParseMembers(membersProperty)
            : [];
        return new ShaderAbiLayout(
            GetRequiredUInt32(value, "Size"),
            GetRequiredUInt32(value, "Alignment"),
            OptionalUInt32(value, "ArrayStride"),
            OptionalUInt32(value, "MatrixStride"),
            members);
    }

    private static ShaderAbiMember[] ParseMembers(JsonElement values)
    {
        var result = new ShaderAbiMember[values.GetArrayLength()];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var nested = value.TryGetProperty("Members", out var nestedProperty) &&
                nestedProperty.ValueKind == JsonValueKind.Array &&
                nestedProperty.GetArrayLength() != 0
                ? ParseLayout(value)
                : null;
            result[index++] = new ShaderAbiMember(
                ParseMemberValueType(value),
                GetRequiredUInt32(value, "Offset"),
                GetRequiredUInt32(value, "Size"),
                GetRequiredUInt32(value, "Alignment"),
                OptionalUInt32(value, "ArrayStride"),
                OptionalUInt32(value, "MatrixStride"),
                nested);
        }

        return result;
    }

    private static ShaderValueType ParseValueType(string type) => type switch
    {
        "bool" => new ShaderValueType(ShaderValueKind.Boolean, 32),
        "int" => new ShaderValueType(ShaderValueKind.SignedInteger, 32),
        "uint" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32),
        "float" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32),
        "double" => new ShaderValueType(ShaderValueKind.FloatingPoint, 64),
        "vec2" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 2),
        "vec3" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 3),
        "vec4" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 4),
        "ivec2" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 2),
        "ivec3" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 3),
        "ivec4" => new ShaderValueType(ShaderValueKind.SignedInteger, 32, 4),
        "uvec2" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 2),
        "uvec3" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 3),
        "uvec4" => new ShaderValueType(ShaderValueKind.UnsignedInteger, 32, 4),
        "dvec2" => new ShaderValueType(ShaderValueKind.FloatingPoint, 64, 2),
        "dvec3" => new ShaderValueType(ShaderValueKind.FloatingPoint, 64, 3),
        "dvec4" => new ShaderValueType(ShaderValueKind.FloatingPoint, 64, 4),
        "mat2" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 2, 2),
        "mat3" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 3, 3),
        "mat4" => new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 4, 4),
        _ when type.StartsWith("DeltaStruct_", StringComparison.Ordinal) => ShaderValueType.Structure,
        _ => throw new InvalidDataException($"Unsupported ShaderAbi type '{type}'.")
    };

    private static ShaderResourceAccess ParseAccess(JsonElement value)
    {
        if (!value.TryGetProperty("Access", out var access))
        {
            throw new InvalidDataException("Storage-buffer access is missing.");
        }

        if (access.ValueKind == JsonValueKind.String)
        {
            return access.GetString() switch
            {
                "ReadOnly" or "Read" => ShaderResourceAccess.Read,
                "Write" => ShaderResourceAccess.Write,
                "ReadWrite" => ShaderResourceAccess.ReadWrite,
                _ => throw new InvalidDataException("Storage-buffer access is invalid.")
            };
        }

        return access.TryGetUInt32(out var numericAccess) ? numericAccess switch
        {
            1 => ShaderResourceAccess.Read,
            2 => ShaderResourceAccess.Write,
            3 => ShaderResourceAccess.ReadWrite,
            _ => throw new InvalidDataException("Storage-buffer access is invalid.")
        } : throw new InvalidDataException("Storage-buffer access is invalid.");
    }

    private static ShaderValueType ParseMemberValueType(JsonElement value)
        => value.TryGetProperty("GlslType", out var glslType)
            ? ParseValueType(GetRequiredString(value, "GlslType"))
            : ParseCanonicalValueType(RequiredObject(value, "Type"));

    private static ShaderValueType ParseCanonicalValueType(JsonElement value)
    {
        var kind = GetRequiredString(value, "Kind");
        var bitWidth = GetRequiredUInt32(value, "BitWidth");
        var vectorSize = GetRequiredUInt32(value, "VectorSize");
        var columns = GetRequiredUInt32(value, "Columns");
        return kind switch
        {
            "Boolean" => new ShaderValueType(ShaderValueKind.Boolean, bitWidth, vectorSize, columns),
            "SignedInteger" => new ShaderValueType(ShaderValueKind.SignedInteger, bitWidth, vectorSize, columns),
            "UnsignedInteger" => new ShaderValueType(ShaderValueKind.UnsignedInteger, bitWidth, vectorSize, columns),
            "FloatingPoint" => new ShaderValueType(ShaderValueKind.FloatingPoint, bitWidth, vectorSize, columns),
            "Structure" => ShaderValueType.Structure,
            _ => throw new InvalidDataException($"Unsupported canonical ShaderAbi kind '{kind}'.")
        };
    }

    private static ShaderWorkgroupSize ParseWorkgroupSize(JsonElement root)
        => root.TryGetProperty("WorkgroupSize", out var value) && value.ValueKind == JsonValueKind.Object
            ? new ShaderWorkgroupSize(
                GetRequiredUInt32(value, "X"),
                GetRequiredUInt32(value, "Y"),
                GetRequiredUInt32(value, "Z"))
            : new ShaderWorkgroupSize(
                GetRequiredUInt32(root, "LocalSizeX"),
                GetRequiredUInt32(root, "LocalSizeY"),
                GetRequiredUInt32(root, "LocalSizeZ"));

    private static ShaderCapabilities ParseRequiredCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("RequiredCapabilities", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return ShaderCapabilities.None;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var numeric))
        {
            return (ShaderCapabilities)numeric;
        }

        var text = value.GetString();
        if (string.Equals(text, "None", StringComparison.Ordinal))
        {
            return ShaderCapabilities.None;
        }

        return text is not null &&
            Enum.TryParse(text, ignoreCase: true, out ShaderCapabilities capabilities)
            ? capabilities
            : throw new InvalidDataException("Artifact required capabilities are invalid.");
    }

    private static JsonDocument? LoadAbiDocument(string manifestPath)
    {
        var metadataPath = manifestPath[..^".shader.json".Length] + ".abi.json";
        return File.Exists(metadataPath) ? JsonDocument.Parse(File.ReadAllText(metadataPath)) : null;
    }

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Artifact property '{name}' must be an object.");
        }

        return property;
    }

    private static (string[] CaseIds, string? OperationIdentity) LoadIdentityMetadata(string manifestPath, JsonElement root)
    {
        var metadataPath = manifestPath[..^".shader.json".Length] + ".abi.json";
        if (!File.Exists(metadataPath))
        {
            return (ParseCaseIds(root), OptionalString(root, "OperationIdentity"));
        }

        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var metadata = document.RootElement;
        var caseId = OptionalString(metadata, "CaseId");
        return (caseId is null ? ParseCaseIds(root) : [caseId], OptionalString(metadata, "OperationIdentity"));
    }

    private static string[] ParseCaseIds(JsonElement root)
    {
        if (!root.TryGetProperty("CaseIds", out var values) && !root.TryGetProperty("caseIds", out values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Artifact CaseIds must be an array.");
        }

        return values.EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException("Artifact CaseIds contains null.")).ToArray();
    }

    private static JsonElement RequiredArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Artifact property '{name}' must be an array.");
        }

        return property;
    }

    private static string GetRequiredString(JsonElement value, string name)
        => OptionalString(value, name) ?? throw new InvalidDataException($"Artifact property '{name}' must be a non-empty string.");

    private static string? OptionalString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static uint GetRequiredUInt32(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw new InvalidDataException($"Artifact property '{name}' must be an unsigned integer.");
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt32(out var result))
        {
            return result;
        }

        if (property.ValueKind == JsonValueKind.String &&
            uint.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        throw new InvalidDataException($"Artifact property '{name}' must be an unsigned integer.");
    }

    private static uint OptionalUInt32(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        return GetRequiredUInt32(value, name);
    }

    private static ulong OptionalUInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out var result))
        {
            return result;
        }

        return property.ValueKind == JsonValueKind.String &&
            ulong.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result)
            ? result
            : throw new InvalidDataException($"Artifact property '{name}' must be an unsigned integer or null.");
    }

    private static bool IsComputeStage(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw new InvalidDataException($"Artifact property '{name}' must identify a compute stage.");
        }

        return property.ValueKind == JsonValueKind.String
            ? string.Equals(property.GetString(), "Compute", StringComparison.Ordinal)
            : property.TryGetUInt32(out var stage) && stage == 0;
    }
}
