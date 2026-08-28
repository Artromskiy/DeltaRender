using Delta.Render;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class ComputeDispatchRunner
{
    public static async Task ExecuteAsync(
        IComputeDevice device,
        ConformanceReport report,
        LoadedArtifact artifact,
        IReadOnlyList<ConformanceCase> cases,
        IReadOnlyList<IComputeStorageBuffer> buffers,
        ComputeBufferBinding[] bindings,
        IComputePipeline pipeline)
    {
        uint workgroupSize = artifact.Artifact.Abi.WorkgroupSize.X;
        uint groups = checked(((uint)cases.Count + workgroupSize - 1) / workgroupSize);
        ComputeDispatchResult dispatch = device.Dispatch(pipeline, bindings, groups);
        if (!dispatch.Succeeded)
        {
            ReportCompilerBlocked(report, cases, artifact.Path, dispatch.Error ?? "Dispatch failed without a diagnostic.");
            return;
        }

        ShaderAbiLayout outputLayout = artifact.Artifact.Abi.Resources[^1].Layout;
        ulong outputStride = outputLayout.ArrayStride == 0 ? outputLayout.Size : outputLayout.ArrayStride;
        var outputData = new byte[checked((int)(outputStride * (ulong)cases.Count))];
        if (!device.Readback(buffers[^1], outputData))
        {
            ReportCompilerBlocked(report, cases, artifact.Path, "Output readback failed.");
            return;
        }

        report.ExecutedGpuCaseCount += cases.Count;
        for (var caseIndex = 0; caseIndex < cases.Count; caseIndex++)
        {
            ConformanceCase testCase = cases[caseIndex];
            uint[] actual = ShaderAbiValueCodec.Read(
                testCase.Expected,
                outputLayout,
                outputData.AsSpan(checked((int)((ulong)caseIndex * outputStride)), checked((int)outputStride)));
            ComparisonResult comparison = ValueComparer.Compare(
                testCase.Expected,
                actual,
                testCase.AbsoluteTolerance,
                testCase.RelativeTolerance,
                testCase.MaxUlps);
            report.AddComparison(testCase, artifact.Path, comparison);
        }
    }

    private static void ReportCompilerBlocked(
        ConformanceReport report,
        IReadOnlyList<ConformanceCase> cases,
        string artifact,
        string reason)
    {
        foreach (ConformanceCase testCase in cases)
        {
            report.AddCompilerBlocked(testCase, artifact, reason);
        }
    }
}
