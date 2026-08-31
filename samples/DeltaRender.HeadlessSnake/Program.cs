using System.Globalization;
using Delta.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;

var frames = ParsePositiveInt(args, "--frames", 600);
var slots = ParsePositiveInt(args, "--slots", 16);
var width = ParsePositiveInt(args, "--width", 960);
var height = ParsePositiveInt(args, "--height", 720);
var options = new RenderSessionOptions(EnableProfiling: true, FramesInFlight: slots);

var renderer = new VulkanRenderer(new VulkanRendererOptions());
await using var rendererScope = renderer.ConfigureAwait(false);
var session = renderer.CreateHeadlessSession((uint)width, (uint)height, options);
await using var sessionScope = session.ConfigureAwait(false);
var graph = session.CreateRenderGraph();
var feature = new SnakeFrameFeature();
IRenderFeature[] features = [feature];
var snake = new SnakeSimulation(width, height);
var completedProfiles = 0;

for (var frame = 0UL; frame < (ulong)frames; frame++)
{
    snake.Update();
    graph.Build(frame, features);
    var result = graph.Execute();
    if (result.Status != RenderGraphExecutionStatus.Submitted)
    {
        await Console.Error.WriteLineAsync($"Headless Snake failed at frame {frame}: {result.Status}").ConfigureAwait(false);
        return 1;
    }

    if (session.Profiler is { } profiler && profiler.TryGetCompleted(frame, out var report))
    {
        WriteProfile(report);
        completedProfiles++;
    }
}

await Console.Out.WriteLineAsync($"headless-snake frames={frames} slots={options.FramesInFlight} profiles={completedProfiles} score={snake.Score} length={snake.Length}").ConfigureAwait(false);
return 0;

static void WriteProfile(RenderProfileReport report)
{
    var timing = report.Timing;
    var counters = report.Counters;
    Console.WriteLine(
        $"Render profile: frame={report.FrameNumber}, status={report.Status}, " +
        $"build={FormatNanoseconds(timing.Build)}, acquire={FormatNanoseconds(timing.Acquire)}, " +
        $"record={FormatNanoseconds(timing.Record)}, submit-present={FormatNanoseconds(timing.SubmitAndPresent)}, " +
        $"fence-wait={FormatNanoseconds(timing.FenceWait)}, layout-shaping={FormatNanoseconds(timing.LayoutAndShapingCpu)}, " +
        $"passes={counters.PassCount}, resources={counters.ResourceCount}, draws={counters.DrawCallCount}, " +
        $"descriptor-binds={counters.DescriptorBindCount}, upload-bytes={counters.UploadBytes}, gpu-timestamps={report.Capabilities.GpuTimestamps}");

    for (var index = 0; index < report.Passes.Count; index++)
    {
        var pass = report.Passes[index];
        var gpu = pass.GpuDuration is { } gpuDuration ? FormatNanoseconds(gpuDuration) : "unavailable";
        Console.WriteLine(
            $"  pass={pass.Name}, kind={pass.Kind}, cpu-record={FormatNanoseconds(pass.CpuRecordDuration)}, gpu={gpu}");
    }
}

static string FormatNanoseconds(ProfileDuration duration)
    => $"{duration.Nanoseconds.ToString("F2", CultureInfo.InvariantCulture)}ns";

static int ParsePositiveInt(string[] args, string name, int fallback)
{
    for (var index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.Ordinal) &&
            int.TryParse(args[index + 1], out var value) && value > 0)
        {
            return value;
        }
    }

    return fallback;
}

internal sealed class SnakeFrameFeature : IRenderFeature
{
    private readonly SnakeTransferPass _pass = new();

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        graph.AddTransferPass("headless-snake-update", _pass);
    }
}

internal sealed class SnakeTransferPass : ITransferPass
{
    public void Record(ITransferCommandContext commands)
    {
    }
}

internal sealed class SnakeSimulation
{
    private readonly int _width;
    private int _head;

    public SnakeSimulation(int width, int height)
    {
        _width = width;
        _ = height;
    }

    public int Score { get; private set; }

    public int Length { get; private set; } = 4;

    public void Update()
    {
        _head++;
        if (_head >= _width)
        {
            _head = 0;
            Score++;
        }

        if (Score > Length)
        {
            Length = Score;
        }
    }
}
