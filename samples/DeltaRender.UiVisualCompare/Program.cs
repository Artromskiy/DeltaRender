using System.Collections.Generic;
using System.Globalization;
using Delta;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Shader.UI;
using Delta.XAML.Contract;

namespace Delta.Render.UiVisualCompare;

internal static class Program
{
    private const int Width = 800;
    private const int Height = 500;
    private const int DefaultFrames = 9;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            string shaderRoot = Path.GetFullPath(GetOption(
                args,
                "--shader-root",
                string.Empty));
            string sliceRoot = Path.GetFullPath(GetOption(
                args,
                "--slice-root",
                string.Empty));
            if (string.IsNullOrWhiteSpace(shaderRoot) || string.IsNullOrWhiteSpace(sliceRoot))
            {
                throw new ArgumentException("--shader-root and --slice-root must point to fresh producer output directories.");
            }
            int frames = ParsePositiveInt(args, "--frames", DefaultFrames);
            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var rendererScope = renderer.ConfigureAwait(false);
            var session = renderer.CreateHeadlessSession(
                Width,
                Height,
                new RenderSessionOptions(EnableProfiling: true, FramesInFlight: 16));
            await using var sessionScope = session.ConfigureAwait(false);
            var graph = session.CreateRenderGraph();

            var rounded = LoadRoundedProgram(shaderRoot);
            var solid = LoadSolidProgram(shaderRoot);
            var slice = LoadSliceProgram(sliceRoot);

            await RunVariantAsync("rounded", graph, session, rounded, null, null, RoundedVisual(), frames).ConfigureAwait(false);
            await RunVariantAsync("solid", graph, session, rounded, solid, null, SolidVisual(), frames).ConfigureAwait(false);
            await RunVariantAsync("slice-9", graph, session, rounded, null, slice, RoundedVisual(), frames).ConfigureAwait(false);
            await RunVariantAsync("slice-7", graph, session, rounded, null, slice, SymmetricRoundedVisual(), frames).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RunVariantAsync(
        string name,
        IRenderGraph graph,
        IRenderFrameSession session,
        IGraphicsShaderProgram defaultProgram,
        IGraphicsShaderProgram? solidProgram,
        IGraphicsShaderProgram? sliceProgram,
        UiVisualDraw visual,
        int frames)
    {
        using var feature = new UiDisplayListGraphFeature(
            session,
            defaultProgram,
            new PixelExtent(Width, Height),
            solidVisualProgram: solidProgram,
            roundedSliceVisualProgram: sliceProgram);
        var displayList = new UiDisplayList(
            [visual],
            [],
            [],
            [new UiDrawRef(UiDrawKind.Visual, 0)],
            [new UiElementIdentity(1, 1, 1)]);
        if (!feature.Consume(displayList))
        {
            throw new InvalidOperationException($"{name} was rejected: {string.Join(" | ", feature.Diagnostics)}");
        }

        IRenderFeature[] features = [feature];
        var buildNanoseconds = new List<double>(frames);
        var recordNanoseconds = new List<double>(frames);
        var gpuPassNanoseconds = new List<double>(frames);
        string countersText = string.Empty;
        for (int frame = 0; frame < frames; frame++)
        {
            graph.Build((ulong)frame, features);
            var result = graph.Execute();
            if (result.Status != RenderGraphExecutionStatus.Submitted)
            {
                throw new InvalidOperationException($"{name} frame {frame} failed: {result.Status}");
            }

            if (session.Profiler is not { } profiler || !profiler.TryGetLatest(out var report))
            {
                continue;
            }

            buildNanoseconds.Add(report.Timing.Build.Nanoseconds);
            recordNanoseconds.Add(report.Timing.Record.Nanoseconds);
            double gpuPassTotal = 0d;
            foreach (var pass in report.Passes)
            {
                if (pass.GpuDuration is { } duration)
                {
                    gpuPassTotal += duration.Nanoseconds;
                }
            }

            gpuPassNanoseconds.Add(gpuPassTotal);
            var counters = report.Counters;
            countersText = $"draws={counters.DrawCallCount} descriptor-binds={counters.DescriptorBindCount} " +
                $"vertex-binds={counters.VertexBufferBindCount} index-binds={counters.IndexBufferBindCount} " +
                $"upload-bytes={counters.UploadBytes}";
        }

        if (buildNanoseconds.Count == 0)
        {
            throw new InvalidOperationException($"{name} did not produce a profile report.");
        }

        await Console.Out.WriteLineAsync(
            $"variant={name} frames={frames} samples={buildNanoseconds.Count} " +
            $"median-build-ns={Median(buildNanoseconds).ToString("F2", CultureInfo.InvariantCulture)} " +
            $"median-record-ns={Median(recordNanoseconds).ToString("F2", CultureInfo.InvariantCulture)} " +
            $"median-gpu-pass-ns={Median(gpuPassNanoseconds).ToString("F2", CultureInfo.InvariantCulture)} " +
            countersText).ConfigureAwait(false);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static UiVisualDraw SolidVisual()
        => UiVisualDraw.WithPaint(
            UiVisualKind.SolidRectangle,
            default,
            new float4(100, 100, 600, 300),
            UiVisualPaint.Solid(new float4(0.2f, 0.5f, 0.9f, 1)),
            UiClipId.None,
            default);

    private static UiVisualDraw RoundedVisual()
        => UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(100, 100, 600, 300),
            new UiVisualPaint(
                new float4(0.2f, 0.5f, 0.9f, 1),
                default,
                0,
                new float4(48, 20, 72, 12)),
            UiClipId.None,
            default);

    private static UiVisualDraw SymmetricRoundedVisual()
        => UiVisualDraw.WithPaint(
            UiVisualKind.RoundedRectangle,
            default,
            new float4(100, 100, 600, 300),
            new UiVisualPaint(
                new float4(0.2f, 0.5f, 0.9f, 1),
                default,
                0,
                new float4(48, 20, 20, 48)),
            UiClipId.None,
            default);

    private static IGraphicsShaderProgram LoadRoundedProgram(string root)
        => RoundedRectangleGraphicsShaderProgram.CreateProgram(
            ReadShader(root, "RoundedRectangleVertex.vert.spv"),
            ReadShader(root, "RoundedRectangleFragment.frag.spv"));

    private static IGraphicsShaderProgram LoadSolidProgram(string root)
        => SolidRectangleGraphicsShaderProgram.CreateProgram(
            ReadShader(root, "SolidRectangleVertex.vert.spv"),
            ReadShader(root, "SolidRectangleFragment.frag.spv"));

    private static IGraphicsShaderProgram LoadSliceProgram(string root)
        => RoundedRectangleGraphicsShaderProgram.CreateProgram(
            ReadShader(root, "RoundedRectangleVertex.vert.spv"),
            ReadShader(root, "RoundedRectangleFragment.frag.spv"));

    private static byte[] ReadShader(string root, string name)
    {
        string path = Path.Combine(root, name);
        return File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new FileNotFoundException($"Missing generated UI shader artifact: {path}");
    }

    private static string GetOption(string[] args, string option, string fallback)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return fallback;
    }

    private static int ParsePositiveInt(string[] args, string option, int fallback)
    {
        string value = GetOption(args, option, string.Empty);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}
