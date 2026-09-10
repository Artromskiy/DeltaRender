using System.Collections.Generic;
using System.Globalization;
using Delta;

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
    string? GpuValue,
    int OutputIndex = 0);

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

        var isHalf = expected.Type.StartsWith("half", StringComparison.Ordinal);
        var isDouble = expected.Type.StartsWith("double", StringComparison.Ordinal) ||
            expected.Type.StartsWith("dvec", StringComparison.Ordinal);
        var isFloat = isHalf || expected.Type.StartsWith("float", StringComparison.Ordinal);
        double? absolute = null;
        double? relative = null;
        long? ulp = null;
        var laneCount = isDouble
            ? expected.Words.Length / 2
            : expected.Words.Length;
        if (isDouble && expected.Words.Length % 2 != 0)
        {
            mismatches.Add(new MismatchDetail(-1, expected.Words.Length.ToString(CultureInfo.InvariantCulture), actual.Length.ToString(CultureInfo.InvariantCulture), null, null, null, null, null));
            return new ComparisonResult(false, mismatches);
        }

        for (var lane = 0; lane < laneCount; lane++)
        {
            var expectedWord = expected.Words[lane];
            var actualWord = actual[lane];
            var expectedDoubleWord = isDouble ? ReadDoubleWord(expected.Words, lane) : 0;
            var actualDoubleWord = isDouble ? ReadDoubleWord(actual, lane) : 0;
            var accepted = isDouble
                ? AcceptDouble(expectedDoubleWord, actualDoubleWord, absoluteTolerance, relativeTolerance, maxUlps, out absolute, out relative, out ulp)
                : isFloat && (isHalf
                    ? AcceptHalf(expectedWord, actualWord, absoluteTolerance, relativeTolerance, maxUlps > 1 ? maxUlps : 1, out absolute, out relative, out ulp)
                    : AcceptFloat(expectedWord, actualWord, absoluteTolerance, relativeTolerance, maxUlps, out absolute, out relative, out ulp));
            if ((isDouble || isFloat) && accepted)
            {
                continue;
            }

            if (!isDouble && !isFloat && expectedWord == actualWord)
            {
                continue;
            }

            mismatches.Add(new MismatchDetail(
                lane,
                isDouble ? $"0x{expectedDoubleWord:x16}" : $"0x{expectedWord:x8}",
                isDouble ? $"0x{actualDoubleWord:x16}" : $"0x{actualWord:x8}",
                isDouble || isFloat ? absolute : null,
                isDouble || isFloat ? relative : null,
                isDouble || isFloat ? ulp : null,
                isDouble ? DecodeDouble(expectedDoubleWord) : isFloat ? Decode(expectedWord) : null,
                isDouble ? DecodeDouble(actualDoubleWord) : isFloat ? Decode(actualWord) : null));
        }

        return new ComparisonResult(mismatches.Count == 0, mismatches);
    }

    private static bool AcceptQuaternion(uint[] expected, uint[] actual)
    {
        var angularToleranceRadians = Maths.Radians(0.0001);
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

        var normalizedDot = Maths.Abs(dot / Maths.Sqrt(expectedLengthSquared * actualLengthSquared));
        normalizedDot = Maths.Clamp(normalizedDot, -1, 1);
        return 2 * Maths.Acos(normalizedDot) <= angularToleranceRadians;
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
        absolute = Maths.Abs((double)cpu - gpu);
        relative = absolute / Maths.Max(Maths.Abs((double)cpu), Maths.Abs((double)gpu));
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

        var ulpDistance = Ordered(cpuWord) - Ordered(gpuWord);
        ulp = ulpDistance >= 0 ? ulpDistance : -ulpDistance;
        if (cpuWord == gpuWord)
        {
            return true;
        }

        return absolute <= absoluteTolerance || relative <= relativeTolerance || ulp <= maxUlps;
    }

    private static bool AcceptDouble(
        ulong cpuWord,
        ulong gpuWord,
        double absoluteTolerance,
        double relativeTolerance,
        long maxUlps,
        out double? absolute,
        out double? relative,
        out long? ulp)
    {
        var cpu = BitConverter.UInt64BitsToDouble(cpuWord);
        var gpu = BitConverter.UInt64BitsToDouble(gpuWord);
        absolute = Maths.Abs(cpu - gpu);
        relative = absolute / Maths.Max(Maths.Abs(cpu), Maths.Abs(gpu));
        if (double.IsNaN(cpu) || double.IsNaN(gpu))
        {
            ulp = null;
            return double.IsNaN(cpu) && double.IsNaN(gpu);
        }

        if (double.IsInfinity(cpu) || double.IsInfinity(gpu))
        {
            ulp = null;
            return cpu == gpu;
        }

        if (cpuWord == gpuWord)
        {
            ulp = 0;
            return true;
        }

        var orderedDistance = OrderedDouble(cpuWord) >= OrderedDouble(gpuWord)
            ? OrderedDouble(cpuWord) - OrderedDouble(gpuWord)
            : OrderedDouble(gpuWord) - OrderedDouble(cpuWord);
        ulp = orderedDistance > long.MaxValue ? long.MaxValue : (long)orderedDistance;
        return absolute <= absoluteTolerance || relative <= relativeTolerance ||
            maxUlps >= 0 && ulp <= maxUlps;
    }

    private static bool AcceptHalf(
        uint cpuWord,
        uint gpuWord,
        double absoluteTolerance,
        double relativeTolerance,
        long maxUlps,
        out double? absolute,
        out double? relative,
        out long? ulp)
    {
        var cpu = (float)BitConverter.UInt16BitsToHalf((ushort)cpuWord);
        var gpu = (float)BitConverter.UInt16BitsToHalf((ushort)gpuWord);
        absolute = Maths.Abs((double)cpu - gpu);
        relative = absolute / Maths.Max(Maths.Abs((double)cpu), Maths.Abs((double)gpu));
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

        var cpuBits = (ushort)cpuWord;
        var gpuBits = (ushort)gpuWord;
        var ulpDistance = OrderedHalf(cpuBits) - OrderedHalf(gpuBits);
        ulp = ulpDistance >= 0 ? ulpDistance : -ulpDistance;
        return cpuBits == gpuBits || absolute <= absoluteTolerance || relative <= relativeTolerance || ulp <= maxUlps;
    }

    private static long Ordered(uint bits)
    {
        var signed = (int)bits;
        return signed < 0 ? (long)int.MinValue - signed : signed;
    }

    private static int OrderedHalf(ushort bits)
    {
        var signed = (short)bits;
        return signed < 0 ? short.MinValue - signed : signed;
    }

    private static string Decode(uint bits)
        => BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture);

    private static ulong ReadDoubleWord(uint[] words, int lane)
        => words[checked(lane * 2)] | ((ulong)words[checked(lane * 2 + 1)] << 32);

    private static ulong OrderedDouble(ulong bits)
        => (bits & (1UL << 63)) != 0 ? ~bits + 1 : (1UL << 63) | bits;

    private static string DecodeDouble(ulong bits)
        => BitConverter.UInt64BitsToDouble(bits).ToString("R", CultureInfo.InvariantCulture);
}
