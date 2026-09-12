using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Delta;
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
            var catalog = ArtifactCatalog.Load(options.ArtifactsPath);
            var artifacts = LoadArtifacts(bundle, options, report);
            if (artifacts is null)
            {
                return await FinishAsync(options, report).ConfigureAwait(false);
            }

            var assignments = CaseAssignment.Assign(bundle.Cases, artifacts, catalog, report);
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
        var renderer = new VulkanRenderer(new VulkanRendererOptions());
        await using var rendererScope = renderer.ConfigureAwait(false);
        var session = renderer.CreateComputeSession();
        await using var sessionScope = session.ConfigureAwait(false);
        report.Device = new DeviceReport(session.Capabilities);
        var runner = new VulkanCaseRunner(session, report, renderer.EnabledShaderCapabilities);
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
            report.Counts.CompilerBlocked == 0
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
            Get(args, "--cases") ?? Path.Combine("..", "DeltaMaths", "tests", "DeltaMaths" + ".Conformance", "shader-conformance.json"),
            Get(args, "--artifacts") ?? Path.Combine("..", "DeltaShader", "artifacts", "maths-conformance"),
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
    CapabilityExcluded,
    ExternalValidationBlocked
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
    IReadOnlyList<CaseValue> Outputs,
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

            var outputs = value.TryGetProperty("outputs", out var outputsElement)
                ? ParseValues(outputsElement, "outputs")
                : [];

            var operation = RequiredObject(value, "operation");
            var comparison = RequiredObject(value, "comparison");
            cases.Add(new ConformanceCase(
                RequiredString(value, "id"),
                RequiredString(operation, "identity"),
                inputs,
                outputs,
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

    private static CaseValue[] ParseValues(JsonElement values, string propertyName)
    {
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Property '{propertyName}' must be an array.");
        }

        var result = new CaseValue[values.GetArrayLength()];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            result[index++] = ParseValue(value);
        }

        return result;
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

internal sealed record CaseAssignment(LoadedArtifact Artifact, IReadOnlyList<ConformanceCase> Cases)
{
    public static IReadOnlyList<CaseAssignment> Assign(
        IReadOnlyList<ConformanceCase> cases,
        IReadOnlyList<LoadedArtifact> artifacts,
        ArtifactCatalog? catalog,
        ConformanceReport report)
    {
        var assignments = new List<CaseAssignment>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        if (catalog is not null)
        {
            foreach (var testCase in cases)
            {
                if (catalog.TryGet(testCase.Id, out var entry) &&
                    string.Equals(entry.Status, "capability-blocked", StringComparison.Ordinal))
                {
                    report.AddCapabilityExcluded(
                        testCase,
                        entry.ArtifactPath ?? "<producer-index>",
                        entry.Diagnostic ?? "Producer marked this case capability-blocked.");
                    assigned.Add(testCase.Id);
                }
            }
        }

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
                if (catalog is not null && catalog.TryGet(testCase.Id, out var entry) &&
                    string.Equals(entry.Status, "capability-blocked", StringComparison.Ordinal))
                {
                    report.AddCapabilityExcluded(
                        testCase,
                        entry.ArtifactPath ?? "<producer-index>",
                        entry.Diagnostic ?? "Producer marked this case capability-blocked.");
                }
                else if (catalog is not null && catalog.TryGet(testCase.Id, out entry) &&
                    IsExternalValidationBlocked(entry.Status))
                {
                    report.AddExternalValidationBlocked(
                        testCase,
                        entry.ArtifactPath ?? "<producer-index>",
                        entry.Diagnostic ?? "Producer marked this case external-validation-blocked.");
                }
                else
                {
                    report.AddCompilerBlocked(testCase, "No artifact metadata matched this case identity.");
                }
            }
        }

        return assignments;
    }

    private static bool IsExternalValidationBlocked(string status)
        => string.Equals(status, "glslang-diagnostic", StringComparison.Ordinal) ||
            string.Equals(status, "external-validation-blocked", StringComparison.Ordinal);

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
    private readonly ShaderCapabilities _supportedCapabilities;

    public VulkanCaseRunner(
        IRenderFrameSession session,
        ConformanceReport report,
        ShaderCapabilities supportedCapabilities)
    {
        _session = session;
        _report = report;
        _supportedCapabilities = supportedCapabilities;
    }

    public async Task ExecuteAsync(CaseAssignment assignment)
    {
        var artifact = assignment.Artifact;
        var cases = assignment.Cases;
        if (!Validate(artifact, cases, out var inputResourceIndices, out var outputResourceIndices))
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
            var inputIndex = 0;
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
                    (ulong)Maths.Max(1, bytes),
                    RenderBufferUsage.Storage | RenderBufferUsage.TransferDestination | RenderBufferUsage.TransferSource);
                buffers[resourceIndex] = _session.CreateBuffer(in description);
                createdBufferCount++;
                if (inputIndex < inputResourceIndices.Length && inputResourceIndices[inputIndex] == resourceIndex)
                {
                    uploads[resourceIndex] = new byte[bytes];
                    for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
                    {
                        ShaderAbiValueCodec.Pack(cases[caseIndex].Inputs[inputIndex], resource.Layout, uploads[resourceIndex].AsSpan(checked((int)((ulong)caseIndex * stride)), checked((int)stride)));
                    }

                    inputIndex++;
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

            var outputData = new byte[outputResourceIndices.Length][];
            for (var outputIndex = 0; outputIndex < outputResourceIndices.Length; outputIndex++)
            {
                var outputResource = resources[outputResourceIndices[outputIndex]];
                var outputStride = outputResource.Layout.ArrayStride == 0 ? outputResource.Layout.Size : outputResource.Layout.ArrayStride;
                outputData[outputIndex] = new byte[checked((int)(outputStride * (ulong)cases.Count))];
                if (graph.CopyReadback(feature.Readbacks[outputIndex], outputData[outputIndex]) != outputData[outputIndex].Length)
                {
                    throw new InvalidOperationException("Output readback failed.");
                }
            }

            _report.ExecutedGpuCaseCount += cases.Count;
            for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
            {
                var testCase = cases[caseIndex];
                var expectedOutputs = GetExpectedOutputs(testCase, outputResourceIndices.Length);
                var mismatches = new List<MismatchDetail>();
                for (var outputIndex = 0; outputIndex < outputResourceIndices.Length; outputIndex++)
                {
                    var outputResource = resources[outputResourceIndices[outputIndex]];
                    var outputStride = outputResource.Layout.ArrayStride == 0 ? outputResource.Layout.Size : outputResource.Layout.ArrayStride;
                    var actual = ShaderAbiValueCodec.Read(
                        expectedOutputs[outputIndex],
                        outputResource.Layout,
                        outputData[outputIndex].AsSpan(checked((int)((ulong)caseIndex * outputStride)), checked((int)outputStride)));
                    var comparison = ValueComparer.Compare(expectedOutputs[outputIndex], actual, testCase.Comparison, testCase.AbsoluteTolerance, testCase.RelativeTolerance, testCase.MaxUlps);
                    foreach (var mismatch in comparison.Mismatches)
                    {
                        mismatches.Add(mismatch with { OutputIndex = outputIndex });
                    }
                }

                _report.AddComparison(testCase, artifact.Path, new ComparisonResult(mismatches.Count == 0, mismatches));
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

    private bool Validate(
        LoadedArtifact artifact,
        IReadOnlyList<ConformanceCase> cases,
        out int[] inputResourceIndices,
        out int[] outputResourceIndices)
    {
        var abi = artifact.Artifact.Abi;
        var resources = abi.Resources;
        inputResourceIndices = GetResourceIndices(resources, includeWrites: false);
        outputResourceIndices = GetResourceIndices(resources, includeWrites: true);
        var missingCapabilities = abi.RequiredCapabilities & ~_supportedCapabilities;
        if (missingCapabilities != ShaderCapabilities.None)
        {
            ReportCapabilityExcluded(cases, artifact.Path, $"Missing Vulkan shader capabilities: {missingCapabilities}.");
            return false;
        }

        var invalidCasePayload = false;
        foreach (var testCase in cases)
        {
            if (testCase.Inputs.Count < inputResourceIndices.Length || GetExpectedOutputCount(testCase) != outputResourceIndices.Length)
            {
                invalidCasePayload = true;
                break;
            }
        }

        if (inputResourceIndices.Length == 0 || outputResourceIndices.Length == 0 || invalidCasePayload)
        {
            ReportCompilerBlocked(cases, artifact.Path, "The artifact storage buffers do not match the case input/output payloads.");
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

    private static int[] GetResourceIndices(IReadOnlyList<ShaderResourceBinding> resources, bool includeWrites)
    {
        var result = new List<int>(resources.Count);
        for (var index = 0; index < resources.Count; index++)
        {
            var hasWrites = resources[index].Access.HasFlag(ShaderResourceAccess.Write);
            if (hasWrites == includeWrites)
            {
                result.Add(index);
            }
        }

        return result.ToArray();
    }

    private static int GetExpectedOutputCount(ConformanceCase testCase)
        => (string.Equals(testCase.Expected.Type, "void", StringComparison.Ordinal) ? 0 : 1) + testCase.Outputs.Count;

    private static CaseValue[] GetExpectedOutputs(ConformanceCase testCase, int outputCount)
    {
        if (GetExpectedOutputCount(testCase) != outputCount)
        {
            throw new InvalidDataException($"Case '{testCase.Id}' does not provide the expected number of output values.");
        }

        var result = new CaseValue[outputCount];
        var index = 0;
        if (!string.Equals(testCase.Expected.Type, "void", StringComparison.Ordinal))
        {
            result[index++] = testCase.Expected;
        }

        foreach (var output in testCase.Outputs)
        {
            result[index++] = output;
        }

        return result;
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
