using System.Collections.Generic;
using System.Globalization;

namespace Delta.Render.MathConformance;

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
