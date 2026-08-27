using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Delta.Maths;
using Delta.Render;
using Delta.Render.Platform.SDL3;
using Delta.Render.RenderGraph;
using Delta.Render.UiShaders;
using Delta.Render.XAML;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;
using Delta.Shader.TestShaders;
using Delta.XAML.Contract;

namespace Delta.Render.ShaderSandbox;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The bounded native sandbox converts renderer and loader failures into a process exit diagnostic.")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var panel = args.Any(static argument => string.Equals(argument, "--panel", StringComparison.OrdinalIgnoreCase));
        var shaderDirectory = GetOption(args, "--shader-dir") ?? Path.Combine(AppContext.BaseDirectory, "shaders");
        var vertexPath = Path.Combine(shaderDirectory, panel ? "ui-panel.vert.spv" : "Vertex.vert.spv");
        var fragmentPath = Path.Combine(shaderDirectory, panel ? "ui-panel.frag.spv" : "Fragment.frag.spv");
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            await Console.Error.WriteLineAsync($"Generated shader artifacts were not found in {shaderDirectory}.");
            await Console.Error.WriteLineAsync(panel
                ? "Use samples/DeltaRender.Smoke/shaders or pass --shader-dir with generated UiPanel artifacts."
                : "Run tools/run-delta-shader-sandbox.sh or pass --shader-dir with DeltaShader.Tool output.");
            return 1;
        }

        var frames = GetOption(args, "--frames") is { } frameText && int.TryParse(frameText, out var parsedFrames)
            ? Math.Max(1, parsedFrames)
            : 1;
        var interactive = args.Any(static argument => string.Equals(argument, "--interactive", StringComparison.OrdinalIgnoreCase));

        var windowResult = new Sdl3WindowFactory().CreateWindow(
            new WindowConfiguration("Delta.Render Shader Sandbox", 960, 540, true, true));
        if (!windowResult.Success || windowResult.Window is null)
        {
            await Console.Error.WriteLineAsync("Window creation failed.");
            await Console.Error.WriteLineAsync(windowResult.Diagnostics.ToText());
            return 1;
        }

        await using var window = windowResult.Window;
        try
        {
            await using var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var session = renderer.CreateWindowSession(window);

            var vertexShader = await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false);
            var fragmentShader = await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false);
            IGraphicsShaderProgram program = panel
                ? UiPanelGraphicsShaderProgram.CreateProgram(vertexShader, fragmentShader)
                : FullscreenUiGraphicsShaderProgram.CreateProgram(vertexShader, fragmentShader);

            UiDisplayListBatchAdapter? panelAdapter = null;
            var panelToken = default(UiRenderFrameToken);
            if (panel)
            {
                panelAdapter = CreatePanelAdapter(out panelToken);
            }

            using (panelAdapter)
            {
                var graph = session.CreateRenderGraph();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var renderedFrames = 0;
                var views = new RenderView[1];
                var features = new IRenderFeature[1];
                IRenderFeature feature = panel
                    ? new UiPanelFeature(program, panelAdapter ?? throw new InvalidOperationException("Panel adapter was not created."), panelToken)
                    : new FullscreenFeature(program);
                features[0] = feature;
                while (interactive || renderedFrames < frames)
                {
                    PumpStandaloneEvents();
                    var metrics = window.Metrics;
                    if (metrics.Width == 0 || metrics.Height == 0)
                    {
                        continue;
                    }

                    var parameters = new GraphicsFrameParameters(
                        metrics.Width,
                        metrics.Height,
                        (float)stopwatch.Elapsed.TotalSeconds);
                    views[0] = new RenderView(
                        session.SurfaceHandle,
                        new RenderViewport(0, 0, metrics.Width, metrics.Height),
                        new PixelRect(0, 0, checked((int)metrics.Width), checked((int)metrics.Height)));
                    switch (feature)
                    {
                        case FullscreenFeature fullscreen:
                            fullscreen.Update(parameters);
                            break;
                        case UiPanelFeature uiPanel:
                            uiPanel.Update(parameters);
                            break;
                    }

                    var frame = new RenderGraphFrame(renderedFrames, views);
                    graph.Build(in frame, features);
                    graph.Execute();
                    renderedFrames++;
                }

                await Console.Out.WriteLineAsync($"shader-sandbox mode={(panel ? "ui-panel" : "fullscreen")} frames={renderedFrames} source=DeltaShader artifact=present");
            }
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Shader sandbox failed:");
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
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

    // This executable is a standalone platform sample; the renderer/session
    // itself does not own event polling or input translation.
    private static void PumpStandaloneEvents() => Sdl3WindowFactory.PumpEvents();

    private static UiDisplayListBatchAdapter CreatePanelAdapter(out UiRenderFrameToken token)
    {
        var clips = new[]
        {
            new UiClip(new float4(96, 64, 768, 412), UiClipId.None),
            new UiClip(new float4(160, 110, 640, 300), new UiClipId(0))
        };
        var visuals = new[]
        {
            new UiVisualCommand(
                UiVisualKind.SolidRectangle,
                default,
                new float4(120, 80, 720, 380),
                new float4(0.08f, 0.65f, 0.95f, 0.92f),
                new UiClipId(0),
                UiResourceId.Empty),
            new UiVisualCommand(
                UiVisualKind.RoundedRectangle,
                default,
                new float4(180, 140, 520, 220),
                new float4(0.95f, 0.34f, 0.12f, 0.72f),
                new UiClipId(1),
                UiResourceId.Empty)
        };
        var displayList = new UiDisplayList(visuals, clips, ReadOnlySpan<UiTextDraw>.Empty);
        var adapter = new UiDisplayListBatchAdapter();
        if (!adapter.TryReplace(
            in displayList,
            ReadOnlySpan<TextSubmissionRecord>.Empty,
            ReadOnlySpan<RenderRecordChange>.Empty,
            default,
            out token))
        {
            adapter.Dispose();
            throw new InvalidOperationException("The panel display list could not be adapted to the renderer batch.");
        }

        return adapter;
    }

    private sealed class FullscreenFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly FullscreenPass _pass = new();

        public FullscreenFeature(IGraphicsShaderProgram program)
        {
            _program = program;
        }

        public void Update(GraphicsFrameParameters parameters)
        {
            _pass.Update(parameters);
        }

        public void AddPasses(IRenderGraphBuilder graph, IRenderFeatureContext context)
        {
            var surface = graph.ImportSurface(context.View.Surface);
            var pass = graph.AddRasterPass(
                new RasterPassDescription(
                    "shader-sandbox",
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

    private sealed class UiPanelFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly UiPanelPass _pass;
        private GraphicsFrameParameters _parameters;

        public UiPanelFeature(
            IGraphicsShaderProgram program,
            UiDisplayListBatchAdapter adapter,
            UiRenderFrameToken token)
        {
            _program = program;
            _pass = new UiPanelPass(adapter, token);
        }

        public void Update(GraphicsFrameParameters parameters)
        {
            _parameters = parameters;
        }

        public void AddPasses(IRenderGraphBuilder graph, IRenderFeatureContext context)
        {
            var surface = graph.ImportSurface(context.View.Surface);
            _pass.Update(context.View.Viewport, context.View.Scissor, _parameters);
            var pass = graph.AddRasterPass(
                new RasterPassDescription(
                    "shader-sandbox-ui",
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

    private sealed class UiPanelPass : IRasterPass
    {
        private readonly UiDisplayListBatchAdapter _adapter;
        private readonly UiRenderFrameToken _token;
        private RenderViewport _viewport;
        private PixelRect _scissor;
        private GraphicsFrameParameters _parameters;

        public UiPanelPass(UiDisplayListBatchAdapter adapter, UiRenderFrameToken token)
        {
            _adapter = adapter;
            _token = token;
        }

        public void Update(RenderViewport viewport, PixelRect scissor, GraphicsFrameParameters parameters)
        {
            _viewport = viewport;
            _scissor = scissor;
            _parameters = parameters;
        }

        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in _viewport);
            commands.SetScissor(in _scissor);
            var batch = _adapter.Borrow(in _token);
            var metrics = new WindowMetrics(
                checked((uint)_viewport.Width),
                checked((uint)_viewport.Height),
                1);
            Span<byte> pushConstants = stackalloc byte[40];
            foreach (var quad in batch.Rectangles)
            {
                if (!quad.Clip.TryGetScissor(metrics, out var quadScissor))
                {
                    continue;
                }

                var quadRect = new PixelRect(
                    quadScissor.X,
                    quadScissor.Y,
                    checked((int)quadScissor.Width),
                    checked((int)quadScissor.Height));
                commands.SetScissor(in quadRect);
                WriteFloat(pushConstants, 0, _parameters.ResolutionX);
                WriteFloat(pushConstants, 4, _parameters.ResolutionY);
                WriteFloat(pushConstants, 8, quad.X);
                WriteFloat(pushConstants, 12, quad.Y);
                WriteFloat(pushConstants, 16, quad.Width);
                WriteFloat(pushConstants, 20, quad.Height);
                WriteFloat(pushConstants, 24, quad.Red);
                WriteFloat(pushConstants, 28, quad.Green);
                WriteFloat(pushConstants, 32, quad.Blue);
                WriteFloat(pushConstants, 36, quad.Alpha);
                commands.PushConstants(pushConstants);
                commands.Draw(6);
            }
        }

        private static void WriteFloat(Span<byte> destination, int offset, float value)
            => MemoryMarshal.Write(destination[offset..], in value);
    }

    private sealed class FullscreenPass : IRasterPass
    {
        private GraphicsFrameParameters _parameters;

        public void Update(GraphicsFrameParameters parameters)
        {
            _parameters = parameters;
        }

        public void Record(IRasterCommandContext commands)
        {
            var viewport = new RenderViewport(0, 0, _parameters.ResolutionX, _parameters.ResolutionY);
            var scissor = new PixelRect(0, 0, checked((int)_parameters.ResolutionX), checked((int)_parameters.ResolutionY));
            commands.SetViewport(in viewport);
            commands.SetScissor(in scissor);
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
