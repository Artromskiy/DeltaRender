using System.Runtime.InteropServices;
using System.Globalization;
using Delta.Render;
using Delta.Shader.Contract;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;

namespace Delta.Render.HeadlessShaderPlayground;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shaderDirectory = GetOption(args, "--shader-dir") ?? Path.Combine("samples", "DeltaRender.Smoke", "shaders");
        var vertexPath = GetOption(args, "--vertex") ?? Path.Combine(shaderDirectory, "fullscreen-rounded-rectangle.vert.spv");
        var fragmentPath = GetOption(args, "--fragment") ?? Path.Combine(shaderDirectory, "fullscreen-rounded-rectangle.frag.spv");
        var outputPath = GetOption(args, "--output") ?? Path.Combine("artifacts", "headless-shader-playground", "output.ppm");
        var width = ParseUInt(args, "--width", 960);
        var height = ParseUInt(args, "--height", 540);
        var frames = ParseUInt(args, "--frames", 1);
        if (width == 0 || height == 0 || frames == 0)
        {
            await Console.Error.WriteLineAsync("--width, --height and --frames must be greater than zero.").ConfigureAwait(false);
            return 2;
        }

        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            await Console.Error.WriteLineAsync($"Missing generated shader pair: {vertexPath} and {fragmentPath}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Pass --shader-dir or explicit --vertex/--fragment paths.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var vertexSpirv = await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false);
            var fragmentSpirv = await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false);
            var program = ShaderManifestFixtureLoader.LoadGraphicsProgram(
                vertexSpirv,
                fragmentSpirv,
                Path.ChangeExtension(vertexPath, ".shader.json"),
                Path.ChangeExtension(fragmentPath, ".shader.json"));

            await using var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var session = renderer.CreateHeadlessSession(width, height);
            var graph = session.CreateRenderGraph();
            var view = new RenderView(
                session.SurfaceHandle,
                new RenderViewport(0, 0, width, height),
                new PixelRect(0, 0, checked((int)width), checked((int)height)));
            var views = new[] { view };
            var time = ParseFloat(args, "--time", 1.25f);
            var features = new IRenderFeature[] { new FullscreenFeature(program, width, height, time) };

            for (var frameNumber = 0u; frameNumber < frames; frameNumber++)
            {
                var frame = new RenderGraphFrame(frameNumber, views);
                graph.Build(in frame, features);
                graph.Execute();
            }

            var bgra = new byte[checked((int)((ulong)width * height * 4))];
            if (!session.ReadbackRgba8(bgra))
            {
                await Console.Error.WriteLineAsync("Headless Vulkan readback did not complete.").ConfigureAwait(false);
                return 1;
            }

            SavePpm(outputPath, width, height, bgra);

            await Console.Out.WriteLineAsync($"headless-shader-playground frames={frames} target={width}x{height} time={time.ToString(CultureInfo.InvariantCulture)} vertex={Path.GetFileName(vertexPath)} fragment={Path.GetFileName(fragmentPath)} output={Path.GetFullPath(outputPath)}").ConfigureAwait(false);
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
        return value is not null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed)
            ? parsed
            : fallback;
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

    private static void SavePpm(string path, uint width, uint height, ReadOnlySpan<byte> bgra)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var rgb = new byte[checked((int)((ulong)width * height * 3))];
        var source = 0;
        var target = 0;
        while (source < bgra.Length)
        {
            rgb[target++] = bgra[source];
            rgb[target++] = bgra[source + 1];
            rgb[target++] = bgra[source + 2];
            source += 4;
        }

        using var stream = File.Create(fullPath);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        stream.Write(rgb);
    }

    private sealed class FullscreenFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly FullscreenPass _pass;

        public FullscreenFeature(IGraphicsShaderProgram program, uint width, uint height, float time)
        {
            _program = program;
            _pass = new FullscreenPass(width, height, time);
        }

        public void AddPasses(IRenderGraphBuilder graph, IRenderFeatureContext context)
        {
            var surface = graph.ImportSurface(context.View.Surface);
            var pass = graph.AddRasterPass(
                new RasterPassDescription(
                    "headless-shader-playground",
                    new RasterPipelineDescription(_program, cullMode: RasterCullMode.None)),
                _pass);
            graph.UseColorAttachment(
                pass,
                0,
                new ColorAttachmentDescription(
                    surface,
                    AttachmentLoadOperation.Clear,
                    AttachmentStoreOperation.Store,
                    new ClearColor(0.04f, 0.05f, 0.08f, 1f)));
        }
    }

    private sealed class FullscreenPass : IRasterPass
    {
        private readonly GraphicsFrameParameters _parameters;
        private readonly RenderViewport _viewport;
        private readonly PixelRect _scissor;

        public FullscreenPass(uint width, uint height, float time)
        {
            _parameters = new GraphicsFrameParameters(width, height, time);
            _viewport = new RenderViewport(0, 0, width, height);
            _scissor = new PixelRect(0, 0, checked((int)width), checked((int)height));
        }

        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in _viewport);
            commands.SetScissor(in _scissor);
            Span<byte> pushConstants = stackalloc byte[16];
            WriteFloat(pushConstants, 0, _parameters.ResolutionX);
            WriteFloat(pushConstants, 4, _parameters.ResolutionY);
            WriteFloat(pushConstants, 8, _parameters.TimeSeconds);
            commands.PushConstants(pushConstants);
            commands.Draw(3);
        }

        private static void WriteFloat(Span<byte> destination, int offset, float value)
            => MemoryMarshal.Write(destination[offset..], in value);
    }
}
