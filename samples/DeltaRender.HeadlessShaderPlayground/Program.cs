using System.Buffers.Binary;
using System.Globalization;
using Delta.Diagnostics;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;

namespace Delta.Render.HeadlessShaderPlayground;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string shaderDirectory = GetOption(args, "--shader-dir") ?? ResolveDefaultShaderDirectory();
        string vertexPath = GetOption(args, "--vertex") ?? Path.Combine(shaderDirectory, "SquareVertex.vert.spv");
        string fragmentPath = GetOption(args, "--fragment") ?? Path.Combine(shaderDirectory, "SquareFragment.frag.spv");
        string outputPath = GetOption(args, "--output") ?? Path.Combine("artifacts", "headless-shader-playground", "square.ppm");
        uint width = ParseUInt(args, "--width", 960);
        uint height = ParseUInt(args, "--height", 540);
        uint frames = ParseUInt(args, "--frames", 1);
        uint vertexCount = ParseUInt(args, "--vertices", 18);
        bool profilingEnabled = HasFlag(args, "--profile");
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
            var feature = new HeadlessRasterFeature(program, session.Target, width, height, vertexCount, ParseFloat(args, "--time", 1.25f));
            IRenderFeature[] features = [feature];
            for (ulong frameNumber = 0UL; frameNumber < frames; frameNumber++)
            {
                graph.Build(frameNumber, features);
                var result = graph.Execute();
                if (result.Status != RenderGraphExecutionStatus.Submitted)
                {
                    await Console.Error.WriteLineAsync($"Graph execution failed: {result.Status}").ConfigureAwait(false);
                    return 1;
                }
            }

            byte[] rgba = new byte[checked((int)((ulong)width * height * 4))];
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
        string? value = GetOption(args, option);
        return value is not null && uint.TryParse(value, out uint parsed) ? parsed : fallback;
    }

    private static string ResolveDefaultShaderDirectory()
    {
        string[] producerDirectories =
        [
            Path.Combine("tools", "DeltaRender.SquareShaders", "bin", "Release", "net10.0", "DeltaShader", "DeltaRender.SquareShaders"),
            Path.Combine("tools", "DeltaRender.SquareShaders", "bin", "Release", "net10.0", "DeltaShader"),
            Path.Combine("tools", "DeltaRender.SquareShaders", "bin", "Debug", "net10.0", "DeltaShader", "DeltaRender.SquareShaders"),
            Path.Combine("tools", "DeltaRender.SquareShaders", "bin", "Debug", "net10.0", "DeltaShader")
        ];
        for (int index = 0; index < producerDirectories.Length; index++)
        {
            string producerDirectory = producerDirectories[index];
            if (File.Exists(Path.Combine(producerDirectory, "SquareVertex.vert.spv")))
            {
                return producerDirectory;
            }
        }

        return producerDirectories[0];
    }

    private static float ParseFloat(string[] args, string option, float fallback)
    {
        string? value = GetOption(args, option);
        return value is not null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed) ? parsed : fallback;
    }

    private static bool HasFlag(string[] args, string option)
    {
        for (int index = 0; index < args.Length; index++)
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
        Console.WriteLine($"profile frame={report.FrameNumber} status={report.Status} build-ns={FormatNanoseconds(timing.Build)} acquire-ns={FormatNanoseconds(timing.Acquire)} record-ns={FormatNanoseconds(timing.Record)} submit-present-ns={FormatNanoseconds(timing.SubmitAndPresent)} readback-ns={FormatNanoseconds(timing.Readback)} fence-wait-ns={FormatNanoseconds(timing.FenceWait)} layout-shaping-ns={FormatNanoseconds(timing.LayoutAndShapingCpu)} passes={counters.PassCount} raster={counters.RasterPassCount} compute={counters.ComputePassCount} transfer={counters.TransferPassCount} resources={counters.ResourceCount} draws={counters.DrawCallCount} descriptor-binds={counters.DescriptorBindCount} vertex-binds={counters.VertexBufferBindCount} index-binds={counters.IndexBufferBindCount} upload-bytes={counters.UploadBytes}");
        for (int index = 0; index < report.Passes.Count; index++)
        {
            var pass = report.Passes[index];
            string gpu = pass.GpuDuration is ProfileDuration gpuDuration ? FormatNanoseconds(gpuDuration) : "unavailable";
            Console.WriteLine($"profile-pass index={index} kind={pass.Kind} name={pass.Name} cpu-ns={FormatNanoseconds(pass.CpuRecordDuration)} gpu-ns={gpu}");
        }
    }

    private static string FormatNanoseconds(ProfileDuration duration)
        => $"{duration.Nanoseconds.ToString("F2", CultureInfo.InvariantCulture)}ns";

    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index + 1 < args.Length; index++)
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
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        byte[] rgb = new byte[checked((int)((ulong)width * height * 3))];
        for (int source = 0; source < rgba.Length; source += 4)
        {
            int target = source / 4 * 3;
            rgb[target] = rgba[source];
            rgb[target + 1] = rgba[source + 1];
            rgb[target + 2] = rgba[source + 2];
        }

        using var stream = File.Create(fullPath);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        stream.Write(rgb);
    }

}
