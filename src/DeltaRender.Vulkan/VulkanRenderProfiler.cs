using System.Diagnostics;
using Delta.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.Vulkan;

internal sealed class VulkanRenderProfiler : IRenderProfiler
{
    private readonly List<PassMeasurement> _passMeasurements = new();
    private readonly RenderProfilingCapabilities _capabilities = new(
        CpuTimings: true,
        GpuTimestamps: false,
        TimestampPeriodNanoseconds: 0,
        TimestampValidBits: 0);
    private RenderProfileReport? _latest;
    private ulong _frameNumber;
    private ProfileDuration _build;
    private ProfileDuration _acquire;
    private ProfileDuration _record;
    private ProfileDuration _submitAndPresent;
    private ProfileDuration _readback;

    public RenderProfilingCapabilities Capabilities => _capabilities;

    public bool TryGetLatest([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RenderProfileReport? report)
    {
        report = _latest;
        return report is not null;
    }

    public bool TryGetCompleted(ulong frameNumber, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RenderProfileReport? report)
    {
        report = _latest;
        return report is not null && report.FrameNumber == frameNumber;
    }

    internal void BeginBuild(ulong frameNumber)
    {
        _frameNumber = frameNumber;
        _build = ProfileDuration.Zero;
        _acquire = ProfileDuration.Zero;
        _record = ProfileDuration.Zero;
        _submitAndPresent = ProfileDuration.Zero;
        _readback = ProfileDuration.Zero;
        _passMeasurements.Clear();
    }

    internal long StartPhase() => Stopwatch.GetTimestamp();

    internal int BeginPass(string name, RenderProfilePassKind kind, out long started)
    {
        started = Stopwatch.GetTimestamp();
        var index = _passMeasurements.Count;
        _passMeasurements.Add(new PassMeasurement(name, kind));
        return index;
    }

    internal void EndPass(int index, long started)
    {
        var measurement = _passMeasurements[index];
        measurement.CpuRecordDuration = Measure(started);
        _passMeasurements[index] = measurement;
    }

    internal void EndBuild(long started) => _build = Measure(started);

    internal void EndAcquire(long started) => _acquire = Measure(started);

    internal void EndRecord(long started) => _record = Measure(started);

    internal void EndSubmitAndPresent(long started) => _submitAndPresent = Measure(started);

    internal void Complete(RenderGraphExecutionStatus status, int resourceCount)
    {
        var passes = new RenderPassProfile[_passMeasurements.Count];
        var rasterCount = 0;
        var computeCount = 0;
        var transferCount = 0;
        for (var index = 0; index < _passMeasurements.Count; index++)
        {
            var measurement = _passMeasurements[index];
            passes[index] = new RenderPassProfile(measurement.Name, measurement.Kind, measurement.CpuRecordDuration, null);
            switch (measurement.Kind)
            {
                case RenderProfilePassKind.Raster:
                    rasterCount++;
                    break;
                case RenderProfilePassKind.Compute:
                    computeCount++;
                    break;
                case RenderProfilePassKind.Transfer:
                    transferCount++;
                    break;
            }
        }

        _latest = new RenderProfileReport(
            _frameNumber,
            status,
            _capabilities,
            new RenderProfileTiming(_build, _acquire, _record, _submitAndPresent, _readback),
            Array.AsReadOnly(passes),
            new RenderProfileCounters(passes.Length, rasterCount, computeCount, transferCount, resourceCount));
    }

    private static ProfileDuration Measure(long started)
        => ProfileDuration.FromStopwatchTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);

    private struct PassMeasurement
    {
        internal PassMeasurement(string name, RenderProfilePassKind kind)
        {
            Name = name;
            Kind = kind;
            CpuRecordDuration = ProfileDuration.Zero;
        }

        internal string Name;
        internal RenderProfilePassKind Kind;
        internal ProfileDuration CpuRecordDuration;
    }
}
