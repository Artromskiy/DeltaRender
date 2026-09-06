using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Delta.Render.RenderGraph;

namespace Delta.Render.MathConformance;

internal sealed class ConformanceReport
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
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
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.CapabilityExcluded),
        _cases.Count(caseReport => caseReport.Disposition == ConformanceDisposition.ExternalValidationBlocked));

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

    public void AddExternalValidationBlocked(ConformanceCase testCase, string artifact, string reason)
        => _cases.Add(AttachMetadata(CaseReport.ExternalBlocked(testCase.Id, testCase.Operation, artifact, reason, testCase.Comparison.ToString()), testCase));

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
        await File.WriteAllTextAsync(fullReportPath, JsonSerializer.Serialize(machine, _jsonOptions)).ConfigureAwait(false);
        var text = $"maths-cpu-gpu-conformance cpuCases={CpuCaseCount} artifacts={ArtifactCount} gpuCases={ExecutedGpuCaseCount} passed={Counts.Passed} mismatched={Counts.Mismatched} compiler-blocked={Counts.CompilerBlocked} capability-excluded={Counts.CapabilityExcluded} external-validation-blocked={Counts.ExternalValidationBlocked}{Environment.NewLine}" +
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

internal sealed record ReportCounts(int Passed, int Mismatched, int CompilerBlocked, int CapabilityExcluded, int ExternalValidationBlocked);

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

    public static CaseReport ExternalBlocked(string id, string operation, string artifact, string diagnostic, string comparison)
        => new(id, operation, ConformanceDisposition.ExternalValidationBlocked, artifact, comparison, diagnostic, []);
}
