using System.Diagnostics.CodeAnalysis;
using Delta.Diagnostics;
using Delta.Render.RenderGraph;

namespace Delta.Render;

/// <summary>
/// Options selected when a frame session is created.
/// </summary>
public readonly record struct RenderSessionOptions
{
    private const int MaxFrameSlots = 16;
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
    ProfileDuration Readback)
{
    /// <summary>Time spent waiting for the frame slot fence to become reusable.</summary>
    public ProfileDuration FenceWait { get; init; }

    /// <summary>
    /// CPU time reported by render adapters while preparing layout or shaping data.
    /// This excludes upstream work that does not pass through a reporting adapter.
    /// </summary>
    public ProfileDuration LayoutAndShapingCpu { get; init; }
}

public readonly record struct RenderProfileCounters(
    int PassCount,
    int RasterPassCount,
    int ComputePassCount,
    int TransferPassCount,
    int ResourceCount)
{
    /// <summary>Number of draw and indexed-draw commands emitted by the command writer.</summary>
    public int DrawCallCount { get; init; }

    /// <summary>Number of descriptor-set bind commands emitted by the command writer.</summary>
    public int DescriptorBindCount { get; init; }

    /// <summary>Total bytes copied into upload staging during graph execution.</summary>
    public ulong UploadBytes { get; init; }
}

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

    /// <summary>Records synchronous adapter CPU work for layout and shaping preparation.</summary>
    void RecordLayoutAndShaping(ProfileDuration duration);

    bool TryGetLatest([NotNullWhen(true)] out RenderProfileReport? report);

    bool TryGetCompleted(ulong frameNumber, [NotNullWhen(true)] out RenderProfileReport? report);
}
