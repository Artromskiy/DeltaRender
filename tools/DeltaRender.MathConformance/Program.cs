using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class Program
{
    private static Task<int> Main(string[] args)
        => ConformanceOrchestrator.RunAsync(args);
}

internal static class ConformanceOrchestrator
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = RunnerOptions.Parse(args);
        var report = new ConformanceReport(options.CasesPath, options.ArtifactsPath);

        try
        {
            var bundle = CaseBundle.Load(options.CasesPath);
            SetBundleMetadata(report, bundle);
            var artifacts = LoadArtifacts(bundle, options, report);
            if (artifacts is null)
            {
                return await FinishAsync(options, report).ConfigureAwait(false);
            }

            var assignments = CaseAssignment.Assign(bundle.Cases, artifacts, report);
            if (assignments.Count != 0)
            {
                await ExecuteAssignmentsAsync(assignments, report).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (RunnerFailure.IsReportable(exception))
        {
            report.AddCompilerBlocked("<runner>", "<orchestrator>", exception.ToString());
        }

        return await FinishAsync(options, report).ConfigureAwait(false);
    }

    private static void SetBundleMetadata(ConformanceReport report, CaseBundle bundle)
    {
        report.MathsCheckpoint = bundle.MathsCheckpoint;
        report.ShaderCheckpoint = bundle.ShaderCheckpoint;
        report.CpuCaseCount = bundle.Cases.Count;
    }

    private static IReadOnlyList<LoadedArtifact>? LoadArtifacts(
        CaseBundle bundle,
        RunnerOptions options,
        ConformanceReport report)
    {
        IReadOnlyList<LoadedArtifact> artifacts;
        try
        {
            artifacts = ShaderArtifactLoader.LoadDirectory(options.ArtifactsPath);
        }
        catch (Exception exception) when (RunnerFailure.IsReportable(exception))
        {
            foreach (var testCase in bundle.Cases)
            {
                report.AddCompilerBlocked(
                    testCase,
                    "<artifact-directory>",
                    $"Could not load final ShaderArtifact sidecars: {exception.Message}");
            }

            return null;
        }

        report.ArtifactCount = artifacts.Count;
        if (artifacts.Count == 0)
        {
            foreach (var testCase in bundle.Cases)
            {
                report.AddCompilerBlocked(testCase, "No final compute artifact was found.");
            }

            return null;
        }

        return artifacts;
    }

    private static async Task ExecuteAssignmentsAsync(
        IReadOnlyList<CaseAssignment> assignments,
        ConformanceReport report)
    {
        await using var renderer = new VulkanRenderer(new VulkanRendererOptions());
        await using var session = renderer.CreateComputeSession();
        report.Device = new DeviceReport(session.Capabilities);
        var runner = new VulkanCaseRunner(session, report);
        foreach (var assignment in assignments)
        {
            foreach (var testCase in assignment.Cases)
            {
                await runner.ExecuteAsync(assignment with { Cases = [testCase] }).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> FinishAsync(RunnerOptions options, ConformanceReport report)
    {
        await report.WriteAsync(options).ConfigureAwait(false);
        return report.Counts.Mismatched == 0 &&
            report.Counts.CompilerBlocked == 0 &&
            report.Counts.CapabilityExcluded == 0
            ? 0
            : 2;
    }
}

internal static class RunnerFailure
{
    public static bool IsReportable(Exception exception)
        => exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or JsonException or NotSupportedException or UnauthorizedAccessException;
}

internal sealed record RunnerOptions(
    string CasesPath,
    string ArtifactsPath,
    string ReportPath,
    string TextReportPath)
{
    public static RunnerOptions Parse(string[] args)
    {
        return new RunnerOptions(
            Get(args, "--cases") ?? Path.Combine("artifacts", "math-conformance", "cases.json"),
            Get(args, "--artifacts") ?? Path.Combine("artifacts", "math-conformance", "shaders"),
            Get(args, "--report") ?? Path.Combine("artifacts", "math-conformance", "render-report.json"),
            Get(args, "--text-report") ?? Path.Combine("artifacts", "math-conformance", "render-report.txt"));
    }

    private static string? Get(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal enum ConformanceDisposition
{
    Passed,
    Mismatched,
    CompilerBlocked,
    CapabilityExcluded
}

internal enum ComparisonProfile
{
    Unknown,
    Exact,
    FloatDiscrete,
    FloatBasic,
    FloatTranscendental,
    QuaternionEquivalent
}

internal sealed record CaseValue(string Type, uint[] Words);

internal sealed record ConformanceCase(
    string Id,
    string Operation,
    IReadOnlyList<CaseValue> Inputs,
    CaseValue Expected,
    ComparisonProfile Comparison,
    double AbsoluteTolerance,
    double RelativeTolerance,
    long MaxUlps,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> Stages,
    string? Artifact);

internal sealed record CaseBundle(
    string? MathsCheckpoint,
    string? ShaderCheckpoint,
    IReadOnlyList<ConformanceCase> Cases)
{
    public static CaseBundle Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Case bundle was not found: {path}", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var casesElement = RequiredArray(root, "cases");
        var cases = new List<ConformanceCase>(casesElement.GetArrayLength());
        foreach (var value in casesElement.EnumerateArray())
        {
            var inputsElement = RequiredArray(value, "inputs");
            var inputs = new CaseValue[inputsElement.GetArrayLength()];
            var inputIndex = 0;
            foreach (var input in inputsElement.EnumerateArray())
            {
                inputs[inputIndex++] = ParseValue(input);
            }

            var operation = RequiredObject(value, "operation");
            var comparison = RequiredObject(value, "comparison");
            cases.Add(new ConformanceCase(
                RequiredString(value, "id"),
                RequiredString(operation, "identity"),
                inputs,
                ParseValue(RequiredObject(value, "expected")),
                ParseProfile(RequiredString(comparison, "name")),
                OptionalDouble(comparison, "absoluteTolerance"),
                OptionalDouble(comparison, "relativeTolerance"),
                OptionalInt64(comparison, "maxUlps"),
                ParseStringArray(value, "requiredCapabilities", "capabilities"),
                ParseStringArray(value, "stages"),
                OptionalString(value, "artifact")));
        }

        return new CaseBundle(
            OptionalString(root, "mathsCheckpoint"),
            OptionalString(root, "shaderCheckpoint"),
            cases);
    }

    private static CaseValue ParseValue(JsonElement value)
    {
        var wordsElement = RequiredArray(value, "words");
        var words = new uint[wordsElement.GetArrayLength()];
        var index = 0;
        foreach (var word in wordsElement.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.String ||
                word.GetString() is not { } text ||
                !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out words[index]))
            {
                throw new InvalidDataException("Case words must be hexadecimal uint32 strings.");
            }

            index++;
        }

        return new CaseValue(RequiredString(value, "type"), words);
    }

    private static ComparisonProfile ParseProfile(string value) => value switch
    {
        "Exact" => ComparisonProfile.Exact,
        "FloatDiscrete" => ComparisonProfile.FloatDiscrete,
        "FloatBasic" => ComparisonProfile.FloatBasic,
        "FloatTranscendental" => ComparisonProfile.FloatTranscendental,
        "QuaternionEquivalent" => ComparisonProfile.QuaternionEquivalent,
        _ => throw new InvalidDataException($"Unsupported comparison profile '{value}'.")
    };

    private static List<string> ParseStringArray(JsonElement value, params string[] names)
    {
        JsonElement values = default;
        var found = false;
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out values))
            {
                found = true;
                break;
            }
        }

        if (!found || values.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Case capabilities must be an array.");
        }

        var result = new List<string>(values.GetArrayLength());
        foreach (var item in values.EnumerateArray())
        {
            result.Add(item.GetString() ?? throw new InvalidDataException("A case metadata value is null."));
        }

        return result;
    }

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Property '{name}' must be an object.");
        }

        return property;
    }

    private static JsonElement RequiredArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Property '{name}' must be an array.");
        }

        return property;
    }

    private static string RequiredString(JsonElement value, string name)
        => OptionalString(value, name) ?? throw new InvalidDataException($"Property '{name}' must be a non-empty string.");

    private static string? OptionalString(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } result &&
            !string.IsNullOrWhiteSpace(result)
            ? result
            : null;
    }

    private static double OptionalDouble(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetDouble(out var result)
            ? result
            : 0d;

    private static long OptionalInt64(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result)
            ? result
            : 0L;
}

internal sealed record LoadedArtifact(
    string Path,
    ShaderArtifact Artifact,
    IReadOnlyList<string> CaseIds,
    string? OperationIdentity);

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
        var spirvPath = manifestPath.EndsWith(".shader.json", StringComparison.Ordinal)
            ? manifestPath[..^".shader.json".Length] + ".spv"
            : throw new InvalidDataException($"Unexpected shader manifest path '{manifestPath}'.");
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

internal sealed record CaseAssignment(LoadedArtifact Artifact, IReadOnlyList<ConformanceCase> Cases)
{
    public static IReadOnlyList<CaseAssignment> Assign(
        IReadOnlyList<ConformanceCase> cases,
        IReadOnlyList<LoadedArtifact> artifacts,
        ConformanceReport report)
    {
        var assignments = new List<CaseAssignment>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var selected = cases.Where(testCase => Matches(testCase, artifact)).ToArray();
            if (selected.Length == 0)
            {
                report.UnmatchedArtifacts.Add(artifact.Path);
                continue;
            }

            assignments.Add(new CaseAssignment(artifact, selected));
            foreach (var testCase in selected)
            {
                assigned.Add(testCase.Id);
            }
        }

        foreach (var testCase in cases)
        {
            if (!assigned.Contains(testCase.Id))
            {
                report.AddCompilerBlocked(testCase, "No artifact metadata matched this case identity.");
            }
        }

        return assignments;
    }

    private static bool Matches(ConformanceCase testCase, LoadedArtifact artifact)
    {
        if (testCase.Artifact is not null)
        {
            return string.Equals(testCase.Artifact, Path.GetFileName(artifact.Path), StringComparison.Ordinal) ||
                string.Equals(testCase.Artifact, artifact.Path, StringComparison.Ordinal);
        }

        return artifact.CaseIds.Contains(testCase.Id, StringComparer.Ordinal) ||
            (artifact.OperationIdentity is not null &&
             string.Equals(artifact.OperationIdentity, testCase.Operation, StringComparison.Ordinal));
    }
}

internal sealed class VulkanCaseRunner
{
    private readonly IRenderFrameSession _session;
    private readonly ConformanceReport _report;

    public VulkanCaseRunner(IRenderFrameSession session, ConformanceReport report)
    {
        _session = session;
        _report = report;
    }

    public async Task ExecuteAsync(CaseAssignment assignment)
    {
        var artifact = assignment.Artifact;
        var cases = assignment.Cases;
        if (!Validate(artifact, cases))
        {
            return;
        }

        var graph = _session.CreateRenderGraph();
        var buffers = Array.Empty<RenderBufferHandle>();
        var createdBufferCount = 0;
        try
        {
            var resources = artifact.Artifact.Abi.Resources;
            var countPushConstants = BuildCountPushConstants(artifact.Artifact);
            buffers = new RenderBufferHandle[resources.Count];
            var uploads = new byte[]?[resources.Count];
            for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                var stride = resource.Layout.ArrayStride == 0 ? resource.Layout.Size : resource.Layout.ArrayStride;
                if (stride == 0 || stride > int.MaxValue || (ulong)cases.Count > ulong.MaxValue / stride)
                {
                    throw new InvalidDataException($"Resource {resource.Binding.Set}:{resource.Binding.Binding} has an invalid array stride.");
                }

                var bytes = checked((int)(stride * (ulong)cases.Count));
                var description = new RenderBufferDescription(
                    (ulong)Math.Max(1, bytes),
                    RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination | RenderBufferUsage.TransferSource);
                buffers[resourceIndex] = _session.CreateBuffer(in description);
                createdBufferCount++;
                if (resourceIndex < resources.Count - 1)
                {
                    uploads[resourceIndex] = new byte[bytes];
                    for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
                    {
                        ShaderAbiValueCodec.Pack(cases[caseIndex].Inputs[resourceIndex], resource.Layout, uploads[resourceIndex].AsSpan(checked((int)((ulong)caseIndex * stride)), checked((int)stride)));
                    }
                }
            }

            var feature = new GraphConformanceFeature(
                artifact.Artifact,
                resources,
                buffers,
                uploads,
                cases.Count,
                countPushConstants.Data,
                countPushConstants.Offset);
            IRenderFeature[] features = [feature];
            graph.Build(0, features);
            var execution = graph.Execute();
            if (execution.Status != RenderGraphExecutionStatus.Submitted)
            {
                throw new InvalidOperationException($"RenderGraph execution failed with status {execution.Status}.");
            }

            var outputResource = resources[^1];
            var outputStride = outputResource.Layout.ArrayStride == 0 ? outputResource.Layout.Size : outputResource.Layout.ArrayStride;
            var readback = feature.Readback;
            var outputData = new byte[checked((int)(outputStride * (ulong)cases.Count))];
            if (graph.CopyReadback(readback, outputData) != outputData.Length)
            {
                throw new InvalidOperationException("Output readback failed.");
            }

            _report.ExecutedGpuCaseCount += cases.Count;
            for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
            {
                var testCase = cases[caseIndex];
                var actual = ShaderAbiValueCodec.Read(testCase.Expected, outputResource.Layout, outputData.AsSpan(checked((int)((ulong)caseIndex * outputStride)), checked((int)outputStride)));
                _report.AddComparison(testCase, artifact.Path, ValueComparer.Compare(testCase.Expected, actual, testCase.Comparison, testCase.AbsoluteTolerance, testCase.RelativeTolerance, testCase.MaxUlps));
            }
        }
        catch (Exception exception) when (RunnerFailure.IsReportable(exception))
        {
            foreach (var testCase in cases)
            {
                _report.AddCompilerBlocked(testCase, artifact.Path, exception.Message);
            }
        }
        finally
        {
            for (var index = createdBufferCount - 1; index >= 0; index--)
            {
                _session.Release(buffers[index]);
            }
        }
    }

    private bool Validate(LoadedArtifact artifact, IReadOnlyList<ConformanceCase> cases)
    {
        var abi = artifact.Artifact.Abi;
        if (abi.RequiredCapabilities != ShaderCapabilities.None)
        {
            ReportCapabilityExcluded(cases, artifact.Path, "Required shader capabilities are not available in the current render session.");
            return false;
        }

        var resources = abi.Resources;
        if (resources.Count < 2 || cases.Any(testCase => testCase.Inputs.Count != resources.Count - 1))
        {
            ReportCompilerBlocked(cases, artifact.Path, "The artifact must declare input storage buffers followed by one output storage buffer per case.");
            return false;
        }

        var workgroupSize = abi.WorkgroupSize.X;
        if (workgroupSize == 0 || (uint)cases.Count > uint.MaxValue - workgroupSize + 1)
        {
            ReportCompilerBlocked(cases, artifact.Path, "Artifact workgroup size or case count is invalid.");
            return false;
        }

        return true;
    }

    private static (byte[] Data, uint Offset) BuildCountPushConstants(ShaderArtifact artifact)
    {
        var ranges = artifact.Abi.PushConstants;
        if (ranges.Count != 1)
        {
            throw new InvalidDataException("Stage-1 conformance artifacts must declare exactly one push-constant range for Count.");
        }

        var range = ranges[0];
        if (range.Size != range.Layout.Size || range.Size < sizeof(uint) || range.Layout.Members.Count != 1)
        {
            throw new InvalidDataException("The conformance push-constant ABI must contain one layout-sized Count member.");
        }

        var member = range.Layout.Members[0];
        if (member.Type.Kind != ShaderValueKind.UnsignedInteger ||
            member.Type.BitWidth != 32 ||
            member.Type.VectorSize != 1 ||
            member.Type.Columns != 1 ||
            member.Size != sizeof(uint) ||
            member.Offset > range.Size - sizeof(uint))
        {
            throw new InvalidDataException("The conformance push-constant ABI must contain a 32-bit unsigned Count member.");
        }

        var data = new byte[checked((int)range.Size)];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(checked((int)member.Offset), sizeof(uint)), 1);
        return (data, range.Offset);
    }

    private void ReportCompilerBlocked(IReadOnlyList<ConformanceCase> cases, string artifact, string reason)
    {
        foreach (var testCase in cases)
        {
            _report.AddCompilerBlocked(testCase, artifact, reason);
        }
    }

    private void ReportCapabilityExcluded(IReadOnlyList<ConformanceCase> cases, string artifact, string reason)
    {
        foreach (var testCase in cases)
        {
            _report.AddCapabilityExcluded(testCase, artifact, reason);
        }
    }

}

internal sealed class GraphConformanceFeature : IRenderFeature
{
    private readonly IShaderArtifact _artifact;
    private readonly IReadOnlyList<ShaderResourceBinding> _resources;
    private readonly RenderBufferHandle[] _buffers;
    private readonly byte[]?[] _uploads;
    private readonly int _caseCount;
    private readonly byte[] _pushConstants;
    private readonly uint _pushConstantOffset;

    internal GraphConformanceFeature(
        IShaderArtifact artifact,
        IReadOnlyList<ShaderResourceBinding> resources,
        RenderBufferHandle[] buffers,
        byte[]?[] uploads,
        int caseCount,
        byte[] pushConstants,
        uint pushConstantOffset)
    {
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _caseCount = caseCount;
        _pushConstants = pushConstants ?? throw new ArgumentNullException(nameof(pushConstants));
        _pushConstantOffset = pushConstantOffset;
    }

    internal RenderGraphReadbackHandle Readback { get; private set; }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        var graphBuffers = new RenderGraphBufferHandle[_buffers.Length];
        for (var index = 0; index < _buffers.Length; index++)
        {
            graphBuffers[index] = graph.ImportBuffer(_buffers[index]);
        }

        var transfer = graph.AddTransferPass("conformance-upload", new GraphTransferPass(graphBuffers, _uploads));
        for (var index = 0; index < _resources.Count; index++)
        {
            if (_uploads[index] is { Length: > 0 })
            {
                graph.UseBuffer(transfer, graphBuffers[index], RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            }
        }

        var compute = graph.AddComputePass(
            new ComputePassDescription("conformance-compute", _artifact),
            new GraphComputePass(_artifact, _resources, graphBuffers, _caseCount, _pushConstants, _pushConstantOffset));
        for (var index = 0; index < _resources.Count; index++)
        {
            graph.UseBuffer(compute, graphBuffers[index], ToRenderAccess(_resources[index].Access), RenderPipelineStages.Compute);
        }

        var output = _resources[^1].Layout.ArrayStride == 0 ? _resources[^1].Layout.Size : _resources[^1].Layout.ArrayStride;
        Readback = graph.ReadbackBuffer(graphBuffers[^1], new BufferRange(0, checked(output * (ulong)_caseCount)));
    }

    private static RenderResourceAccess ToRenderAccess(ShaderResourceAccess access)
    {
        var result = RenderResourceAccess.None;
        if (access.HasFlag(ShaderResourceAccess.Read)) result |= RenderResourceAccess.Read;
        if (access.HasFlag(ShaderResourceAccess.Write)) result |= RenderResourceAccess.Write;
        return result;
    }
}

internal sealed class GraphTransferPass(RenderGraphBufferHandle[] buffers, byte[]?[] uploads) : ITransferPass
{
    public void Record(ITransferCommandContext commands)
    {
        for (var index = 0; index < buffers.Length; index++)
        {
            if (uploads[index] is { Length: > 0 } data)
            {
                commands.UploadBuffer(buffers[index], data);
            }
        }
    }
}

internal sealed class GraphComputePass(
    IShaderArtifact artifact,
    IReadOnlyList<ShaderResourceBinding> resources,
    RenderGraphBufferHandle[] buffers,
    int caseCount,
    byte[] pushConstants,
    uint pushConstantOffset) : IComputePass
{
    public void Record(IComputeCommandContext commands)
    {
        for (var index = 0; index < resources.Count; index++)
        {
            commands.BindBuffer(resources[index].Binding, buffers[index]);
        }

        commands.PushConstants(pushConstants, pushConstantOffset);
        var workgroupSize = artifact.Abi.WorkgroupSize.X;
        var groups = checked(((uint)caseCount + workgroupSize - 1) / workgroupSize);
        commands.Dispatch(groups);
    }
}

internal sealed record ComparisonResult(bool Passed, IReadOnlyList<MismatchDetail> Mismatches);

internal sealed record MismatchDetail(
    int Lane,
    string CpuWord,
    string GpuWord,
    double? AbsoluteError,
    double? RelativeError,
    long? UlpDistance,
    string? CpuValue,
    string? GpuValue);

internal static class ValueComparer
{
    public static ComparisonResult Compare(
        CaseValue expected,
        uint[] actual,
        ComparisonProfile profile,
        double absoluteTolerance,
        double relativeTolerance,
        long maxUlps)
    {
        var mismatches = new List<MismatchDetail>();
        if (expected.Words.Length != actual.Length)
        {
            mismatches.Add(new MismatchDetail(-1, expected.Words.Length.ToString(CultureInfo.InvariantCulture), actual.Length.ToString(CultureInfo.InvariantCulture), null, null, null, null, null));
            return new ComparisonResult(false, mismatches);
        }

        if (profile == ComparisonProfile.QuaternionEquivalent &&
            AcceptQuaternion(expected.Words, actual))
        {
            return new ComparisonResult(true, mismatches);
        }

        var isFloat = expected.Type.StartsWith("float", StringComparison.Ordinal) ||
            expected.Type.StartsWith("double", StringComparison.Ordinal);
        double? absolute = null;
        double? relative = null;
        long? ulp = null;
        for (var lane = 0; lane < expected.Words.Length; lane++)
        {
            if (isFloat && AcceptFloat(
                    expected.Words[lane],
                    actual[lane],
                    absoluteTolerance,
                    relativeTolerance,
                    maxUlps,
                    out absolute,
                    out relative,
                    out ulp))
            {
                continue;
            }

            if (!isFloat && expected.Words[lane] == actual[lane])
            {
                continue;
            }

            mismatches.Add(new MismatchDetail(
                lane,
                $"0x{expected.Words[lane]:x8}",
                $"0x{actual[lane]:x8}",
                isFloat ? absolute : null,
                isFloat ? relative : null,
                isFloat ? ulp : null,
                isFloat ? Decode(expected.Words[lane]) : null,
                isFloat ? Decode(actual[lane]) : null));
        }

        return new ComparisonResult(mismatches.Count == 0, mismatches);
    }

    private static bool AcceptQuaternion(uint[] expected, uint[] actual)
    {
        const double angularToleranceRadians = 0.0001 * Math.PI / 180.0;
        if (expected.Length != 4 || actual.Length != 4)
        {
            return false;
        }

        double expectedLengthSquared = 0;
        double actualLengthSquared = 0;
        double dot = 0;
        for (var lane = 0; lane < 4; lane++)
        {
            var expectedValue = BitConverter.UInt32BitsToSingle(expected[lane]);
            var actualValue = BitConverter.UInt32BitsToSingle(actual[lane]);
            if (!float.IsFinite(expectedValue) || !float.IsFinite(actualValue))
            {
                return false;
            }

            expectedLengthSquared += (double)expectedValue * expectedValue;
            actualLengthSquared += (double)actualValue * actualValue;
            dot += (double)expectedValue * actualValue;
        }

        if (expectedLengthSquared <= double.Epsilon || actualLengthSquared <= double.Epsilon)
        {
            return false;
        }

        var normalizedDot = Math.Abs(dot / Math.Sqrt(expectedLengthSquared * actualLengthSquared));
        normalizedDot = Math.Clamp(normalizedDot, -1, 1);
        return 2 * Math.Acos(normalizedDot) <= angularToleranceRadians;
    }

    private static bool AcceptFloat(
        uint cpuWord,
        uint gpuWord,
        double absoluteTolerance,
        double relativeTolerance,
        long maxUlps,
        out double? absolute,
        out double? relative,
        out long? ulp)
    {
        var cpu = BitConverter.UInt32BitsToSingle(cpuWord);
        var gpu = BitConverter.UInt32BitsToSingle(gpuWord);
        absolute = Math.Abs((double)cpu - gpu);
        relative = absolute / Math.Max(Math.Abs((double)cpu), Math.Abs((double)gpu));
        if (float.IsNaN(cpu) || float.IsNaN(gpu))
        {
            ulp = null;
            return float.IsNaN(cpu) && float.IsNaN(gpu);
        }

        if (float.IsInfinity(cpu) || float.IsInfinity(gpu))
        {
            ulp = null;
            return cpu == gpu;
        }

        ulp = Math.Abs(Ordered(cpuWord) - Ordered(gpuWord));
        if (cpuWord == gpuWord)
        {
            return true;
        }

        return absolute <= absoluteTolerance || relative <= relativeTolerance || ulp <= maxUlps;
    }

    private static long Ordered(uint bits)
    {
        var signed = (int)bits;
        return signed < 0 ? (long)int.MinValue - signed : signed;
    }

    private static string Decode(uint bits)
        => BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture);
}

internal sealed class ConformanceReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<CaseReport> _cases = [];

    public ConformanceReport(string casesPath, string artifactsPath)
    {
        CasesPath = casesPath;
        ArtifactsPath = artifactsPath;
    }

    public string CasesPath { get; }
    public string ArtifactsPath { get; }
    public string? MathsCheckpoint { get; set; }
    public string? ShaderCheckpoint { get; set; }
    public int CpuCaseCount { get; set; }
    public int ArtifactCount { get; set; }
    public int ExecutedGpuCaseCount { get; set; }
    public DeviceReport? Device { get; set; }
    public List<string> UnmatchedArtifacts { get; } = [];
    public IReadOnlyList<CaseReport> Cases => _cases;
    public ReportCounts Counts => new(
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.Passed),
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.Mismatched),
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.CompilerBlocked),
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.CapabilityExcluded));

    public void AddCompilerBlocked(ConformanceCase testCase, string reason)
        => AddCompilerBlocked(testCase.Id, testCase.Operation, reason, testCase);

    public void AddCompilerBlocked(string id, string operation, string reason)
        => AddCompilerBlocked(id, operation, reason, null);

    public void AddCompilerBlocked(ConformanceCase testCase, string artifact, string reason)
        => AddCompilerBlocked(testCase.Id, testCase.Operation, reason, testCase, artifact);

    public void AddCompilerBlocked(string id, string operation, string reason, ConformanceCase? testCase = null, string? artifact = null)
        => _cases.Add(AttachMetadata(CaseReport.Blocked(id, operation, artifact, reason, testCase?.Comparison.ToString()), testCase));

    public void AddCapabilityExcluded(ConformanceCase testCase, string artifact, string reason)
        => _cases.Add(AttachMetadata(CaseReport.Excluded(testCase.Id, testCase.Operation, artifact, reason, testCase.Comparison.ToString()), testCase));

    public void AddComparison(ConformanceCase testCase, string artifact, ComparisonResult result)
        => _cases.Add(AttachMetadata(result.Passed
            ? CaseReport.Passed(testCase.Id, testCase.Operation, artifact, testCase.Comparison.ToString())
            : CaseReport.Mismatch(testCase.Id, testCase.Operation, artifact, testCase.Comparison.ToString(), result.Mismatches), testCase));

    private static CaseReport AttachMetadata(CaseReport report, ConformanceCase? testCase)
        => testCase is null
            ? report
            : report with
            {
                RequiredCapabilities = testCase.RequiredCapabilities,
                Stages = testCase.Stages
            };

    public async Task WriteAsync(RunnerOptions options)
    {
        var fullReportPath = Path.GetFullPath(options.ReportPath);
        var fullTextPath = Path.GetFullPath(options.TextReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath) ?? Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTextPath) ?? Environment.CurrentDirectory);
        var machine = new MachineReport(
            1,
            CasesPath,
            ArtifactsPath,
            MathsCheckpoint,
            ShaderCheckpoint,
            CpuCaseCount,
            ArtifactCount,
            ExecutedGpuCaseCount,
            Device,
            Counts,
            UnmatchedArtifacts,
            Cases);
        await File.WriteAllTextAsync(fullReportPath, JsonSerializer.Serialize(machine, JsonOptions)).ConfigureAwait(false);
        var text = $"maths-cpu-gpu-conformance cpuCases={CpuCaseCount} artifacts={ArtifactCount} gpuCases={ExecutedGpuCaseCount} passed={Counts.Passed} mismatched={Counts.Mismatched} compiler-blocked={Counts.CompilerBlocked} capability-excluded={Counts.CapabilityExcluded}{Environment.NewLine}" +
            string.Join(Environment.NewLine, _cases.Select(caseReport => $"{caseReport.Disposition} {caseReport.Id} {caseReport.Operation}: {caseReport.Diagnostic}"));
        await File.WriteAllTextAsync(fullTextPath, text + Environment.NewLine).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(text).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"machine-report={fullReportPath}").ConfigureAwait(false);
    }
}

internal sealed record DeviceReport(
    ulong MaxStorageBufferRange,
    ulong MinStorageBufferOffsetAlignment,
    ulong NonCoherentAtomSize,
    uint MaxComputeWorkGroupSizeX,
    uint MaxComputeWorkGroupCountX,
    uint MaxBoundDescriptorSets)
{
    public DeviceReport(RenderDeviceCapabilities capabilities)
        : this(
            capabilities.MaxStorageBufferRange,
            capabilities.MinStorageBufferOffsetAlignment,
            0,
            capabilities.MaxComputeWorkGroupSizeX,
            capabilities.MaxComputeWorkGroupCountX,
            capabilities.MaxBoundDescriptorSets)
    {
    }
}

internal sealed record ReportCounts(int Passed, int Mismatched, int CompilerBlocked, int CapabilityExcluded);

internal sealed record MachineReport(
    int SchemaVersion,
    string CasesPath,
    string ArtifactsPath,
    string? MathsCheckpoint,
    string? ShaderCheckpoint,
    int CpuCaseCount,
    int ArtifactCount,
    int ExecutedGpuCaseCount,
    DeviceReport? Device,
    ReportCounts Counts,
    IReadOnlyList<string> UnmatchedArtifacts,
    IReadOnlyList<CaseReport> Cases);

internal sealed record CaseReport(
    string Id,
    string Operation,
    ConformanceDisposition Disposition,
    string? Artifact,
    string? Comparison,
    string Diagnostic,
    IReadOnlyList<MismatchDetail> Mismatches)
{
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }
    public IReadOnlyList<string>? Stages { get; init; }

    public static CaseReport Passed(string id, string operation, string artifact, string comparison)
        => new(id, operation, ConformanceDisposition.Passed, artifact, comparison, "CPU/GPU values matched.", []);

    public static CaseReport Mismatch(string id, string operation, string artifact, string comparison, IReadOnlyList<MismatchDetail> mismatches)
        => new(id, operation, ConformanceDisposition.Mismatched, artifact, comparison, "CPU/GPU values differ.", mismatches);

    public static CaseReport Blocked(string id, string operation, string? artifact, string diagnostic, string? comparison)
        => new(id, operation, ConformanceDisposition.CompilerBlocked, artifact, comparison, diagnostic, []);

    public static CaseReport Excluded(string id, string operation, string artifact, string diagnostic, string comparison)
        => new(id, operation, ConformanceDisposition.CapabilityExcluded, artifact, comparison, diagnostic, []);
}
