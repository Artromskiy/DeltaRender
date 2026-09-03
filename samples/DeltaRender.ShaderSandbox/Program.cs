using System.Runtime.InteropServices;
using Delta;
using Delta.Render;
using Delta.Render.Platform.SDL3;
using Delta.Render.RenderGraph;
using Delta.Render.FullscreenShaders;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;

namespace Delta.Render.ShaderSandbox;
using Maths = global::Delta.Maths;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shaderDirectory = GetOption(args, "--shader-dir") ?? Path.Combine(AppContext.BaseDirectory, "shaders");
        var vertexPath = Path.Combine(shaderDirectory, "Vertex.vert.spv");
        var fragmentPath = Path.Combine(shaderDirectory, "Fragment.frag.spv");
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            await Console.Error.WriteLineAsync($"Generated shader artifacts were not found in {shaderDirectory}.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Pass --shader-dir with a DeltaShader-generated fullscreen vertex/fragment pair.").ConfigureAwait(false);
            return 1;
        }

        var frames = GetOption(args, "--frames") is { } frameText && int.TryParse(frameText, out var parsedFrames)
            ? Maths.Max(1, parsedFrames)
            : 1;
        var interactive = args.Any(static argument => string.Equals(argument, "--interactive", StringComparison.OrdinalIgnoreCase));

        var windowResult = new Sdl3WindowFactory().CreateWindow(
            new WindowConfiguration("Delta.Render Shader Sandbox", 960, 540, true, true));
        if (!windowResult.Success || windowResult.Window is not { } window)
        {
            await Console.Error.WriteLineAsync("Window creation failed.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(windowResult.Diagnostics.ToText()).ConfigureAwait(false);
            return 1;
        }

        await using var windowLease = window.ConfigureAwait(false);
        var renderer = new VulkanRenderer(new VulkanRendererOptions());
        await using var rendererLease = renderer.ConfigureAwait(false);
        var session = renderer.CreateWindowSession(window);
        await using var sessionLease = session.ConfigureAwait(false);

        var vertexShader = await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false);
        var fragmentShader = await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false);
        IGraphicsShaderProgram program = FullscreenUiGraphicsShaderProgram.CreateProgram(vertexShader, fragmentShader);
        var graph = session.CreateRenderGraph();
        try
        {
            var feature = new FullscreenFeature(program, session.Target);
            var features = new IRenderFeature[] { feature };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var renderedFrames = 0;
            var extent = default(PixelExtent);
            while (interactive ? !window.IsClosed : renderedFrames < frames)
            {
                PumpStandaloneEvents();
                var metrics = window.Metrics;
                if (metrics.Width == 0 || metrics.Height == 0)
                {
                    continue;
                }

                var nextExtent = new PixelExtent(metrics.Width, metrics.Height);
                if (nextExtent != extent)
                {
                    session.ResizeTarget(in nextExtent);
                    extent = nextExtent;
                }

                feature.Update(metrics.Width, metrics.Height, (float)stopwatch.Elapsed.TotalSeconds);
                graph.Build((ulong)renderedFrames, features);
                graph.Execute();

                renderedFrames++;
            }

            await Console.Out.WriteLineAsync($"shader-sandbox frames={renderedFrames} source=DeltaShader artifact=present").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Shader sandbox failed:").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (graph is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
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

    private sealed class FullscreenFeature : IRenderFeature
    {
        private readonly IGraphicsShaderProgram _program;
        private readonly RenderTargetHandle _target;
        private readonly FullscreenPass _pass = new();

        public FullscreenFeature(IGraphicsShaderProgram program, RenderTargetHandle target)
        {
            _program = program;
            _target = target;
        }

        public void Update(uint width, uint height, float time)
        {
            _pass.Update(width, height, time);
        }

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            var surface = graph.ImportTarget(_target);
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

    private sealed class FullscreenPass : IRasterPass
    {
        private uint _width;
        private uint _height;
        private float _time;

        public void Update(uint width, uint height, float time)
        {
            _width = width;
            _height = height;
            _time = time;
        }

        public void Record(IRasterCommandContext commands)
        {
            var viewport = new RenderViewport(0, 0, _width, _height);
            var scissor = new PixelRect(0, 0, checked((int)_width), checked((int)_height));
            commands.SetViewport(in viewport);
            commands.SetScissor(in scissor);
            Span<byte> pushConstants = stackalloc byte[16];
            WriteFloat(pushConstants, 0, _width);
            WriteFloat(pushConstants, 4, _height);
            WriteFloat(pushConstants, 8, _time);
            commands.PushConstants(pushConstants);
            commands.Draw(3);
        }

        private static void WriteFloat(Span<byte> destination, int offset, float value)
            => MemoryMarshal.Write(destination[offset..], in value);
    }
}
