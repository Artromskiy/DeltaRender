using System.Buffers.Binary;
using System.Globalization;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;

namespace Delta.Render.HeadlessShaderPlayground;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shaderDirectory = GetOption(args, "--shader-dir") ?? Path.Combine("artifacts", "headless-shader-playground", "shaders");
        var vertexPath = GetOption(args, "--vertex") ?? Path.Combine(shaderDirectory, "SquareVertex.vert.spv");
        var fragmentPath = GetOption(args, "--fragment") ?? Path.Combine(shaderDirectory, "SquareFragment.frag.spv");
        var outputPath = GetOption(args, "--output") ?? Path.Combine("artifacts", "headless-shader-playground", "square.ppm");
        var width = ParseUInt(args, "--width", 960);
        var height = ParseUInt(args, "--height", 540);
        var frames = ParseUInt(args, "--frames", 1);
        var vertexCount = ParseUInt(args, "--vertices", 18);
        var profilingEnabled = HasFlag(args, "--profile");
        if (width == 0 || height == 0 || frames == 0 || vertexCount == 0)
        {
            await Console.Error.WriteLineAsync("--width, --height, --frames and --vertices must be greater than zero.").ConfigureAwait(false);
            return 2;
        }

        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            await Console.Error.WriteLineAsync($"Missing generated shader pair: {vertexPath} and {fragmentPath}").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var program = ShaderManifestFixtureLoader.LoadGraphicsProgram(
                await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false),
                await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false),
                Path.ChangeExtension(vertexPath, ".shader.json"),
                Path.ChangeExtension(fragmentPath, ".shader.json"));

            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var rendererScope = renderer.ConfigureAwait(false);
            var session = renderer.CreateHeadlessSession(width, height, new RenderSessionOptions(profilingEnabled));
            await using var sessionScope = session.ConfigureAwait(false);
            var graph = session.CreateRenderGraph();
            var feature = new RasterFeature(program, session.Target, width, height, vertexCount, ParseFloat(args, "--time", 1.25f));
            IRenderFeature[] features = [feature];
            for (var frameNumber = 0UL; frameNumber < frames; frameNumber++)
            {
                graph.Build(frameNumber, features);
                var result = graph.Execute();
                if (result.Status != RenderGraphExecutionStatus.Submitted)
                {
                    await Console.Error.WriteLineAsync($"Graph execution failed: {result.Status}").ConfigureAwait(false);
                    return 1;
                }
            }

            var rgba = new byte[checked((int)((ulong)width * height * 4))];
            if (graph.CopyReadback(feature.Readback, rgba) != rgba.Length)
            {
                await Console.Error.WriteLineAsync("Headless Vulkan readback did not complete.").ConfigureAwait(false);
                return 1;
            }

            SavePpm(outputPath, width, height, rgba);
            await Console.Out.WriteLineAsync($"headless-shader-playground frames={frames} target={width}x{height} vertices={vertexCount} time={feature.Time.ToString(CultureInfo.InvariantCulture)} vertex={Path.GetFileName(vertexPath)} fragment={Path.GetFileName(fragmentPath)} output={Path.GetFullPath(outputPath)}").ConfigureAwait(false);
            WriteProfile(session.Profiler);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Headless shader playground failed:").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static uint ParseUInt(string[] args, string option, uint fallback)
    {
        var value = GetOption(args, option);
        return value is not null && uint.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static float ParseFloat(string[] args, string option, float fallback)
    {
        var value = GetOption(args, option);
        return value is not null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed) ? parsed : fallback;
    }

    private static bool HasFlag(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteProfile(IRenderProfiler? profiler)
    {
        if (profiler is null || !profiler.TryGetLatest(out var report))
        {
            return;
        }

        var timing = report.Timing;
        var counters = report.Counters;
        Console.WriteLine($"profile frame={report.FrameNumber} status={report.Status} build={timing.Build} acquire={timing.Acquire} record={timing.Record} submit-present={timing.SubmitAndPresent} readback={timing.Readback} passes={counters.PassCount} raster={counters.RasterPassCount} compute={counters.ComputePassCount} transfer={counters.TransferPassCount} resources={counters.ResourceCount}");
        for (var index = 0; index < report.Passes.Count; index++)
        {
            var pass = report.Passes[index];
            Console.WriteLine($"profile-pass index={index} kind={pass.Kind} name={pass.Name} cpu={pass.CpuRecordDuration} gpu={pass.GpuDuration?.ToString() ?? "unavailable"}");
        }
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void SavePpm(string path, uint width, uint height, ReadOnlySpan<byte> rgba)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var rgb = new byte[checked((int)((ulong)width * height * 3))];
        for (var source = 0; source < rgba.Length; source += 4)
        {
            var target = source / 4 * 3;
            rgb[target] = rgba[source];
            rgb[target + 1] = rgba[source + 1];
            rgb[target + 2] = rgba[source + 2];
        }

        using var stream = File.Create(fullPath);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        stream.Write(rgb);
    }

    private sealed class RasterFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly RenderTargetHandle _target;
        private readonly uint _width;
        private readonly uint _height;
        private readonly uint _vertexCount;
        private readonly byte[] _pushConstants = new byte[16];
        private readonly RasterPass _pass;

        internal RasterFeature(IGraphicsShaderProgram program, RenderTargetHandle target, uint width, uint height, uint vertexCount, float time)
        {
            _program = program;
            _target = target;
            _width = width;
            _height = height;
            _vertexCount = vertexCount;
            Time = time;
            BinaryPrimitives.WriteSingleLittleEndian(_pushConstants.AsSpan(0, 4), width);
            BinaryPrimitives.WriteSingleLittleEndian(_pushConstants.AsSpan(4, 4), height);
            BinaryPrimitives.WriteSingleLittleEndian(_pushConstants.AsSpan(8, 4), time);
            _pass = new RasterPass(width, height, _vertexCount, _pushConstants);
        }

        internal float Time { get; }
        internal RenderGraphReadbackHandle Readback { get; private set; }

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            var target = graph.ImportTarget(_target);
            var pass = graph.AddRasterPass(new RasterPassDescription("headless-shader-playground", new RasterPipelineDescription(_program, cullMode: RasterCullMode.None)), _pass);
            graph.UseColorAttachment(pass, 0, new ColorAttachmentDescription(target, AttachmentLoadOperation.Clear, AttachmentStoreOperation.Store, new ClearColor(0.04f, 0.05f, 0.08f, 1f)));
            Readback = graph.ReadbackTexture(target, new PixelRect(0, 0, checked((int)_width), checked((int)_height)));
        }
    }

    private sealed class RasterPass : IRasterPass
    {
        private readonly RenderViewport _viewport;
        private readonly PixelRect _scissor;
        private readonly uint _vertexCount;
        private readonly byte[] _pushConstants;

        internal RasterPass(uint width, uint height, uint vertexCount, byte[] pushConstants)
        {
            _viewport = new RenderViewport(0, 0, width, height);
            _scissor = new PixelRect(0, 0, checked((int)width), checked((int)height));
            _vertexCount = vertexCount;
            _pushConstants = pushConstants;
        }

        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in _viewport);
            commands.SetScissor(in _scissor);
            commands.PushConstants(_pushConstants);
            commands.Draw(_vertexCount);
        }
    }
}
