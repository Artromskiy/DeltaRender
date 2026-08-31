using System.Diagnostics;
using Delta.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal sealed unsafe class VulkanRenderProfiler : IRenderProfiler, IDisposable
{
    private readonly Vk _api;
    private readonly Device _device;
    private readonly double _timestampPeriodNanoseconds;
    private readonly uint _timestampValidBits;
    private PassMeasurement[] _passMeasurements = [];
    private int _passCount;
    private RenderProfilingCapabilities _capabilities;
    private bool _gpuTimestampsEnabled;
    private QueryPool _queryPool;
    private ulong[] _queryValues = [];
    private int _queryCapacity;
    private int _queryCount;
    private RenderProfileReport? _latest;
    private ulong _frameNumber;
    private ProfileDuration _build;
    private ProfileDuration _acquire;
    private ProfileDuration _record;
    private ProfileDuration _submitAndPresent;
    private ProfileDuration _readback;

    internal VulkanRenderProfiler(Vk api, Device device, PhysicalDevice physicalDevice, uint graphicsFamily)
    {
        _api = api;
        _device = device;
        var limits = api.GetPhysicalDeviceProperties(physicalDevice).Limits;
        _timestampPeriodNanoseconds = limits.TimestampPeriod;
        _timestampValidBits = GetTimestampValidBits(api, physicalDevice, graphicsFamily);
        _gpuTimestampsEnabled = limits.TimestampComputeAndGraphics &&
            _timestampPeriodNanoseconds > 0 &&
            _timestampValidBits > 0;
        _capabilities = new RenderProfilingCapabilities(
            CpuTimings: true,
            GpuTimestamps: _gpuTimestampsEnabled,
            TimestampPeriodNanoseconds: _gpuTimestampsEnabled ? _timestampPeriodNanoseconds : 0,
            TimestampValidBits: _gpuTimestampsEnabled ? _timestampValidBits : 0);
    }

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
        _passCount = 0;
    }

    internal void BeginGpuFrame(CommandBuffer commandBuffer, int passCount)
    {
        _queryCount = 0;
        if (!_gpuTimestampsEnabled || passCount == 0)
        {
            return;
        }

        var required = checked(passCount * 2);
        if (!EnsureQueryPool(required))
        {
            return;
        }

        _queryCount = required;
        _api.CmdResetQueryPool(commandBuffer, _queryPool, 0, (uint)_queryCount);
    }

    internal long StartPhase() => Stopwatch.GetTimestamp();

    internal int BeginPass(string name, RenderProfilePassKind kind, CommandBuffer commandBuffer, out long started)
    {
        started = Stopwatch.GetTimestamp();
        var index = _passCount;
        EnsurePassCapacity(index + 1);
        _passMeasurements[index] = new PassMeasurement(name, kind);
        _passCount++;
        if (_gpuTimestampsEnabled)
        {
            _api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, _queryPool, (uint)(index * 2));
        }

        return index;
    }

    internal void EndPass(int index, long started, CommandBuffer commandBuffer)
    {
        if (_gpuTimestampsEnabled)
        {
            _api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _queryPool, (uint)(index * 2 + 1));
        }

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
        if (status == RenderGraphExecutionStatus.Submitted)
        {
            ReadGpuDurations();
        }

        var passes = new RenderPassProfile[_passCount];
        var rasterCount = 0;
        var computeCount = 0;
        var transferCount = 0;
        for (var index = 0; index < _passCount; index++)
        {
            var measurement = _passMeasurements[index];
            passes[index] = new RenderPassProfile(measurement.Name, measurement.Kind, measurement.CpuRecordDuration, measurement.GpuDuration);
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

    public void Dispose()
    {
        if (_queryPool.Handle != default)
        {
            _api.DestroyQueryPool(_device, _queryPool, null);
            _queryPool = default;
        }

        _queryValues = [];
        _queryCapacity = 0;
        _queryCount = 0;
    }

    private bool EnsureQueryPool(int required)
    {
        if (required <= _queryCapacity)
        {
            return true;
        }

        var capacity = _queryCapacity == 0 ? 64 : checked(_queryCapacity * 2);
        capacity = Math.Max(capacity, required);
        var createInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)capacity,
        };
        if (_api.CreateQueryPool(_device, createInfo, null, out var queryPool) != Result.Success)
        {
            _gpuTimestampsEnabled = false;
            _capabilities = new RenderProfilingCapabilities(true, false, 0, 0);
            return false;
        }

        if (_queryPool.Handle != default)
        {
            _api.DestroyQueryPool(_device, _queryPool, null);
        }

        _queryPool = queryPool;
        _queryCapacity = capacity;
        _queryValues = new ulong[capacity];
        return true;
    }

    private void EnsurePassCapacity(int required)
    {
        if (required <= _passMeasurements.Length)
        {
            return;
        }

        var capacity = _passMeasurements.Length == 0 ? 8 : checked(_passMeasurements.Length * 2);
        Array.Resize(ref _passMeasurements, Math.Max(capacity, required));
    }

    private void ReadGpuDurations()
    {
        if (!_gpuTimestampsEnabled || _queryCount == 0)
        {
            return;
        }

        var flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit;
        var result = _api.GetQueryPoolResults(
            _device,
            _queryPool,
            0,
            (uint)_queryCount,
            _queryValues.AsSpan(0, _queryCount),
            sizeof(ulong),
            flags);
        if (result != Result.Success)
        {
            return;
        }

        for (var index = 0; index < _passCount; index++)
        {
            var measurement = _passMeasurements[index];
            measurement.GpuDuration = ConvertTimestampDelta(_queryValues.RefAt(index * 2), _queryValues.RefAt(index * 2 + 1));
            _passMeasurements[index] = measurement;
        }
    }

    private ProfileDuration ConvertTimestampDelta(ulong start, ulong end)
    {
        var delta = unchecked(end - start);
        if (_timestampValidBits < 64)
        {
            var mask = (1UL << (int)_timestampValidBits) - 1UL;
            delta &= mask;
        }

        var picoseconds = delta * _timestampPeriodNanoseconds * 1_000d;
        if (picoseconds <= 0)
        {
            return ProfileDuration.Zero;
        }

        if (picoseconds >= ulong.MaxValue)
        {
            return new ProfileDuration(ulong.MaxValue);
        }

        return new ProfileDuration((ulong)Math.Round(picoseconds, MidpointRounding.ToEven));
    }

    private static uint GetTimestampValidBits(Vk api, PhysicalDevice physicalDevice, uint graphicsFamily)
    {
        uint familyCount = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &familyCount, null);
        if (familyCount == 0 || graphicsFamily >= familyCount)
        {
            return 0;
        }

        var families = new QueueFamilyProperties[(int)familyCount];
        api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &familyCount, families);
        return families[graphicsFamily].TimestampValidBits;
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
            GpuDuration = null;
        }

        internal string Name;
        internal RenderProfilePassKind Kind;
        internal ProfileDuration CpuRecordDuration;
        internal ProfileDuration? GpuDuration;
    }
}
