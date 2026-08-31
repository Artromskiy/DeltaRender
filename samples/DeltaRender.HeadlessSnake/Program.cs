using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;

var frames = ParsePositiveInt(args, "--frames", 600);
var slots = ParsePositiveInt(args, "--slots", 16);
var width = ParsePositiveInt(args, "--width", 960);
var height = ParsePositiveInt(args, "--height", 720);
var options = new RenderSessionOptions(FramesInFlight: slots);

var renderer = new VulkanRenderer(new VulkanRendererOptions());
await using var rendererScope = renderer.ConfigureAwait(false);
var session = renderer.CreateHeadlessSession((uint)width, (uint)height, options);
await using var sessionScope = session.ConfigureAwait(false);
var graph = session.CreateRenderGraph();
var feature = new SnakeFrameFeature();
IRenderFeature[] features = [feature];
var snake = new SnakeSimulation(width, height);

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
}

await Console.Out.WriteLineAsync($"headless-snake frames={frames} slots={options.FramesInFlight} score={snake.Score} length={snake.Length}").ConfigureAwait(false);
return 0;

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
