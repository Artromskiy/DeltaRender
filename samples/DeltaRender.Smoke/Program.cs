using System.Buffers.Binary;
using System.Diagnostics;
using Delta.Render;
using Delta.Render.FullscreenShaders;
using Delta.Render.Platform.SDL3;
using Delta.Render.RenderGraph;
using Delta.Render.UIShaders;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;

namespace Delta.Render.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var headless = args.Any(argument => string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase));
        if (headless)
        {
            var probe = VulkanEnvironmentProbe.CheckHeadless();
            await Console.Out.WriteLineAsync(probe.Diagnostics.ToText()).ConfigureAwait(false);
            return probe.Usable ? 0 : 1;
        }

        var panel = args.Any(argument => string.Equals(argument, "--panel", StringComparison.OrdinalIgnoreCase));
        var clearOnly = args.Any(argument => string.Equals(argument, "--clear", StringComparison.OrdinalIgnoreCase));
        var interactive = args.Any(argument => string.Equals(argument, "--interactive", StringComparison.OrdinalIgnoreCase));
        var frameCount = ParsePositiveInt(args, "--frames", interactive ? int.MaxValue : 1);
        var factory = new Sdl3WindowFactory();
        var createResult = factory.CreateWindow(new WindowConfiguration("DeltaRender Smoke", 960, 540, true, true));
        if (!createResult.Succeeded || createResult.Window is not { } window)
        {
            await Console.Error.WriteLineAsync("Window creation failed.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(createResult.Diagnostics.ToText()).ConfigureAwait(false);
            return 1;
        }

        await using var windowLease = window.ConfigureAwait(false);
        var renderer = new VulkanRenderer(new VulkanRendererOptions());
        await using var rendererScope = renderer.ConfigureAwait(false);
        try
        {
            var session = renderer.CreateWindowSession(window);
            await using var sessionScope = session.ConfigureAwait(false);
            var program = LoadProgram(panel);
            var graph = session.CreateRenderGraph();
            var metrics = window.Metrics;
            var feature = new SmokeFeature(program, session.Target, panel, clearOnly, metrics.Width, metrics.Height);
            IRenderFeature[] features = [feature];
            var extent = default(PixelExtent);
            var stopwatch = Stopwatch.StartNew();
            var renderedFrames = 0;
            while (!window.IsClosed && renderedFrames < frameCount)
            {
                Sdl3WindowFactory.PumpEvents();
                metrics = window.Metrics;
                var nextExtent = new PixelExtent(metrics.Width, metrics.Height);
                if (nextExtent != extent)
                {
                    session.ResizeTarget(in nextExtent);
                    extent = nextExtent;
                }

                feature.Update(metrics.Width, metrics.Height, (float)stopwatch.Elapsed.TotalSeconds);
                graph.Build((ulong)renderedFrames, features);
                var result = graph.Execute();
                if (result.Status != RenderGraphExecutionStatus.Submitted)
                {
                    await Console.Error.WriteLineAsync($"Graph execution failed: {result.Status}").ConfigureAwait(false);
                    return 1;
                }

                renderedFrames++;
            }

            await Console.Out.WriteLineAsync($"graphics={(clearOnly ? "clear" : panel ? "ui-panel" : "fullscreen-rounded-rectangle")} frames={renderedFrames} pass=present").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Renderer initialization or execution failed:").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static IGraphicsShaderProgram LoadProgram(bool panel)
    {
        var prefix = panel ? "ui-panel" : "fullscreen-rounded-rectangle";
        var vertexPath = Path.Combine(AppContext.BaseDirectory, "shaders", prefix + ".vert.spv");
        var fragmentPath = Path.Combine(AppContext.BaseDirectory, "shaders", prefix + ".frag.spv");
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            throw new FileNotFoundException($"Graphics fixtures were not found: {vertexPath}, {fragmentPath}");
        }

        var vertex = File.ReadAllBytes(vertexPath);
        var fragment = File.ReadAllBytes(fragmentPath);
        return panel
            ? UiPanelGraphicsShaderProgram.CreateProgram(vertex, fragment)
            : FullscreenUiGraphicsShaderProgram.CreateProgram(vertex, fragment);
    }

    private static int ParsePositiveInt(string[] args, string option, int fallback)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[index + 1], out var value))
            {
                return Math.Max(1, value);
            }
        }

        return fallback;
    }

    private sealed class SmokeFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly RenderTargetHandle _target;
        private readonly bool _panel;
        private readonly bool _clearOnly;
        private readonly PanelQuad[] _quads =
        [
            new PanelQuad(180, 120, 600, 300, 0.08f, 0.65f, 0.95f, 0.92f),
            new PanelQuad(240, 180, 480, 180, 0.95f, 0.34f, 0.12f, 0.72f, new PixelRect(260, 200, 420, 120))
        ];
        private readonly SmokePass _pass;

        internal SmokeFeature(IGraphicsShaderProgram program, RenderTargetHandle target, bool panel, bool clearOnly, uint width, uint height)
        {
            _program = program;
            _target = target;
            _panel = panel;
            _clearOnly = clearOnly;
            _pass = new SmokePass(panel, clearOnly, width, height, _quads);
        }

        internal void Update(uint width, uint height, float time) => _pass.Update(width, height, time);

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            var target = graph.ImportTarget(_target);
            var pass = graph.AddRasterPass(new RasterPassDescription("smoke", new RasterPipelineDescription(_program, cullMode: RasterCullMode.None)), _pass);
            graph.UseColorAttachment(pass, 0, new ColorAttachmentDescription(target, AttachmentLoadOperation.Clear, AttachmentStoreOperation.Store, new ClearColor(0.1f, 0.12f, 0.2f, 1f)));
        }
    }

    private sealed class SmokePass : IRasterPass
    {
        private readonly bool _panel;
        private readonly bool _clearOnly;
        private readonly PanelQuad[] _quads;
        private RenderViewport _viewport;
        private PixelRect _scissor;
        private float _width;
        private float _height;
        private float _time;

        internal SmokePass(bool panel, bool clearOnly, uint width, uint height, PanelQuad[] quads)
        {
            _panel = panel;
            _clearOnly = clearOnly;
            _quads = quads;
            Update(width, height, 0);
        }

        internal void Update(uint width, uint height, float time)
        {
            _width = width;
            _height = height;
            _time = time;
            _viewport = new RenderViewport(0, 0, width, height);
            _scissor = new PixelRect(0, 0, checked((int)width), checked((int)height));
        }

        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in _viewport);
            commands.SetScissor(in _scissor);
            if (_clearOnly)
            {
                return;
            }

            if (!_panel)
            {
                Span<byte> constants = stackalloc byte[16];
                WriteFloat(constants, 0, _width);
                WriteFloat(constants, 4, _height);
                WriteFloat(constants, 8, _time);
                commands.PushConstants(constants);
                commands.Draw(3);
                return;
            }

            Span<byte> panelConstants = stackalloc byte[40];
            foreach (var quad in _quads)
            {
                var scissor = quad.Clip.IsEmpty ? _scissor : quad.Clip;
                commands.SetScissor(in scissor);
                WriteFloat(panelConstants, 0, _width);
                WriteFloat(panelConstants, 4, _height);
                WriteFloat(panelConstants, 8, quad.X);
                WriteFloat(panelConstants, 12, quad.Y);
                WriteFloat(panelConstants, 16, quad.Width);
                WriteFloat(panelConstants, 20, quad.Height);
                WriteFloat(panelConstants, 24, quad.Red);
                WriteFloat(panelConstants, 28, quad.Green);
                WriteFloat(panelConstants, 32, quad.Blue);
                WriteFloat(panelConstants, 36, quad.Alpha);
                commands.PushConstants(panelConstants);
                commands.Draw(6);
            }
        }

        private static void WriteFloat(Span<byte> destination, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(destination[offset..], value);
    }

    private readonly record struct PanelQuad(float X, float Y, float Width, float Height, float Red, float Green, float Blue, float Alpha, PixelRect Clip = default);
}
