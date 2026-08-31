using System.Diagnostics.CodeAnalysis;
using Delta.Diagnostics;
using Delta.Render.RenderGraph;

namespace Delta.Render;

/// <summary>
/// Options selected when a frame session is created.
/// </summary>
public readonly record struct RenderSessionOptions
{
    private const int MaxFrameSlots = 8;
    private readonly int _frameSlots;

    public RenderSessionOptions(bool EnableProfiling = false, int FramesInFlight = 1)
    {
        if (FramesInFlight is < 1 or > MaxFrameSlots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FramesInFlight),
                FramesInFlight,
                $"Frames in flight must be between 1 and {MaxFrameSlots}.");
        }

        this.EnableProfiling = EnableProfiling;
        _frameSlots = FramesInFlight;
    }

    public bool EnableProfiling { get; }

    /// <summary>
    /// Number of reusable frame states used by the session for in-flight frame
    /// production. Each slot owns its command resources, fence and staging state.
    /// </summary>
    public int FramesInFlight => _frameSlots == 0 ? 1 : _frameSlots;
}

public readonly record struct RenderProfilingCapabilities(
    bool CpuTimings,
    bool GpuTimestamps,
    double TimestampPeriodNanoseconds,
    uint TimestampValidBits);

public enum RenderProfilePassKind : byte
{
    Raster,
    Compute,
    Transfer,
}

public readonly record struct RenderProfileTiming(
    ProfileDuration Build,
    ProfileDuration Acquire,
    ProfileDuration Record,
    ProfileDuration SubmitAndPresent,
    ProfileDuration Readback);

public readonly record struct RenderProfileCounters(
    int PassCount,
    int RasterPassCount,
    int ComputePassCount,
    int TransferPassCount,
    int ResourceCount);

public sealed class RenderPassProfile
{
    internal RenderPassProfile(
        string name,
        RenderProfilePassKind kind,
        ProfileDuration cpuRecordDuration,
        ProfileDuration? gpuDuration)
    {
        Name = name;
        Kind = kind;
        CpuRecordDuration = cpuRecordDuration;
        GpuDuration = gpuDuration;
    }

    public string Name { get; }

    public RenderProfilePassKind Kind { get; }

    public ProfileDuration CpuRecordDuration { get; }

    public ProfileDuration? GpuDuration { get; }
}

public sealed class RenderProfileReport
{
    internal RenderProfileReport(
        ulong frameNumber,
        RenderGraphExecutionStatus status,
        RenderProfilingCapabilities capabilities,
        RenderProfileTiming timing,
        IReadOnlyList<RenderPassProfile> passes,
        RenderProfileCounters counters)
    {
        FrameNumber = frameNumber;
        Status = status;
        Capabilities = capabilities;
        Timing = timing;
        Passes = passes;
        Counters = counters;
    }

    public ulong FrameNumber { get; }

    public RenderGraphExecutionStatus Status { get; }

    public RenderProfilingCapabilities Capabilities { get; }

    public RenderProfileTiming Timing { get; }

    public IReadOnlyList<RenderPassProfile> Passes { get; }

    public RenderProfileCounters Counters { get; }
}

public interface IRenderProfiler
{
    RenderProfilingCapabilities Capabilities { get; }

    bool TryGetLatest([NotNullWhen(true)] out RenderProfileReport? report);

    bool TryGetCompleted(ulong frameNumber, [NotNullWhen(true)] out RenderProfileReport? report);
}
