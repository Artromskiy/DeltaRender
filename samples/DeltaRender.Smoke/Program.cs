using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DeltaRender;
using DeltaRender.Platform.SDL3;
using DeltaRender.Vulkan;
using DeltaRender.FullscreenShaders;
using DeltaRender.UiShaders;
using DeltaShader.Contract;

namespace DeltaRender.Smoke;

[SuppressMessage("Performance", "CA2007:Do not directly await a Task", Justification = "The bounded native smoke has no synchronization context; await-using declarations intentionally keep Vulkan resources scoped to each smoke operation.")]
internal static class Program
{
    private const string ComputeZeroMessage = "compute-size=0 pass=no-op";
    private const string DirtyRecordsMessage = "dirty-records pass=coalesced";
    private const string MultiComputeZeroMessage = "multi-compute-size=0 pass=no-op";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The executable smoke boundary converts renderer/native failures into a process exit diagnostic.")]
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--compute", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunComputeSmokeAsync(GetOption(args, "--compute-shader")).ConfigureAwait(false);
        }

        var clearOnly = args.Any(a => string.Equals(a, "--clear", StringComparison.OrdinalIgnoreCase));
        var panel = args.Any(a => string.Equals(a, "--panel", StringComparison.OrdinalIgnoreCase));
        var interactive = args.Any(a => string.Equals(a, "--interactive", StringComparison.OrdinalIgnoreCase));

        var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        if (headless)
        {
            var probe = VulkanEnvironmentProbe.CheckHeadless();
            await Console.Out.WriteLineAsync(probe.Diagnostics.ToText());
            return probe.Usable ? 0 : 1;
        }

        var factory = new Sdl3WindowFactory();
        var result = factory.CreateWindow(new WindowConfiguration("DeltaRender Smoke", 960, 540, true, true));

        if (!result.Success || result.Window is null)
        {
            await Console.Error.WriteLineAsync("Window creation failed");
            await Console.Error.WriteLineAsync(result.Diagnostics.ToText());
            return 1;
        }

        await using var window = result.Window;

        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var _ = renderer;

            await using IRenderWindowFrameSession session = renderer.CreateWindowSession(window);
            if (clearOnly)
            {
                if (!session.SubmitFrame(default(RenderFramePacket)))
                {
                    await Console.Error.WriteLineAsync("Failed to render clear frame.");
                    return 1;
                }
            }
            else
            {
                var vertexPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.vert.spv");
                var fragmentPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.frag.spv");
                if (panel)
                {
                    vertexPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.vert.spv");
                    fragmentPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.frag.spv");
                }
                if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
                {
                    await Console.Error.WriteLineAsync("Graphics fixtures were not found; use --clear for the swapchain-only path.");
                    return 1;
                }

                var program = panel
                    ? UiPanelGraphicsShaderProgram.CreateProgram(File.ReadAllBytes(vertexPath), File.ReadAllBytes(fragmentPath))
                    : FullscreenUiGraphicsShaderProgram.CreateProgram(File.ReadAllBytes(vertexPath), File.ReadAllBytes(fragmentPath));
                await using var pipeline = session.CreateGraphicsPipeline(in program);
                var stopwatch = Stopwatch.StartNew();
                var frames = GetOption(args, "--frames") is { } frameText && int.TryParse(frameText, out var parsedFrames)
                    ? Math.Max(1, parsedFrames)
                    : 1;
                var renderedFrames = 0;
                var panelAdapter = new PanelUiAdapter();
                while (interactive || renderedFrames < frames)
                {
                    Sdl3WindowFactory.PumpEvents();
                    var parameters = new GraphicsFrameParameters(window.Metrics.Width, window.Metrics.Height, (float)stopwatch.Elapsed.TotalSeconds);
                    var drawList = new UiDrawList(panelAdapter.CurrentDrawList.Span);
                    var rendered = panel
                        ? SubmitPanelFrame(session, pipeline, in parameters, in drawList)
                        : SubmitFullscreenFrame(session, pipeline, in parameters);
                    if (!rendered)
                    {
                        await Console.Error.WriteLineAsync("Failed to render fullscreen graphics frame.");
                        return 1;
                    }

                    renderedFrames++;
                }
                await Console.Out.WriteLineAsync($"graphics={(panel ? "ui-panel" : "fullscreen-rounded-rectangle")} frames={renderedFrames} pass=present");
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Renderer initialization failed:");
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
            return 1;
        }

        Sdl3WindowFactory.PumpEvents();
        return 0;
    }

    private static bool SubmitFullscreenFrame(
        IRenderWindowFrameSession session,
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters)
    {
        var frameState = session.BeginFrame();
        return frameState.IsValid && session.DrawFullscreenTriangle(pipeline, in parameters) &&
               session.EndFrame(in frameState, default(RenderFramePacket));
    }

    private static bool SubmitPanelFrame(
        IRenderWindowFrameSession session,
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters,
        in UiDrawList drawList)
    {
        var packet = new RenderFramePacket(
            pipeline,
            parameters,
            drawList,
            null,
            default,
            ReadOnlySpan<ITextAtlasPage>.Empty,
            default,
            ReadOnlySpan<RenderRecordChange>.Empty);
        return session.SubmitFrame(in packet);
    }

    private sealed class PanelUiAdapter
    {
        private readonly UiQuad[] _drawList =
        [
            new UiQuad(180, 120, 600, 300, 0.08f, 0.65f, 0.95f, 0.92f),
            new UiQuad(240, 180, 480, 180, 0.95f, 0.34f, 0.12f, 0.72f)
            {
                Clip = new UiClipRect(260, 200, 420, 120)
            }
        ];

        public ReadOnlyMemory<UiQuad> CurrentDrawList => _drawList;
    }

    private static async Task<int> RunComputeSmokeAsync(string? externalShaderPath)
    {
        var shaderPath = externalShaderPath ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_double.spv");
        if (!File.Exists(shaderPath))
        {
            await Console.Error.WriteLineAsync($"Compute shader was not found: {shaderPath}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"compute-shader={Path.GetFullPath(shaderPath)}");
        var shader = await File.ReadAllBytesAsync(shaderPath).ConfigureAwait(false);
        var artifact = new ShaderArtifact(shader, "main", new ShaderAbi(
            ShaderStage.Compute,
            resources: [new ShaderResourceBinding(
                new ShaderBinding(0, 0),
                ShaderResourceKind.StorageBuffer,
                ShaderResourceAccess.ReadWrite,
                ShaderStageMask.Compute,
                new ShaderAbiLayout(4, 4, arrayStride: 4))],
            workgroupSize: new ShaderWorkgroupSize(64, 1, 1)));

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        IComputePipeline pipeline = device.CreateComputePipeline(artifact);

        await using (pipeline)
        {
            return await RunComputeSizesAsync(device, pipeline).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunComputeSizesAsync(VulkanComputeDevice device, IComputePipeline pipeline)
    {
        if (pipeline.Abi.Resources.Count == 2)
        {
            return await RunMultiBufferComputeSizesAsync(device, pipeline).ConfigureAwait(false);
        }

        var localSizeX = pipeline.Abi.WorkgroupSize.X;

        foreach (var size in new[] { 0, 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var buffer = device.CreateStorageBuffer((ulong)size * sizeof(uint));
            if (size == 0)
            {
                var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
                if (noOp.Status != ComputeDispatchStatus.NoOp)
                {
                    await Console.Error.WriteLineAsync("zero-sized dispatch was not a no-op");
                    return 1;
                }
                await Console.Out.WriteLineAsync(ComputeZeroMessage);
                continue;
            }

            var values = new uint[size];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (uint)i;
            }

            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            if (!device.Upload(buffer, bytes))
            {
                return await FailComputeAsync(size, "upload");
            }

            var groups = (uint)((size + (int)localSizeX - 1) / (int)localSizeX);
            var dispatch = device.Dispatch(pipeline, new[] { new ComputeBufferBinding(0, 0, buffer) }, groups);
            if (!dispatch.Succeeded || dispatch.Status != ComputeDispatchStatus.Executed)
            {
                return await FailComputeAsync(size, dispatch.Error ?? "dispatch");
            }

            var output = new byte[bytes.Length];
            if (!device.Readback(buffer, output))
            {
                return await FailComputeAsync(size, "readback");
            }

            var actual = MemoryMarshal.Cast<byte, uint>(output);
            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != (uint)(i * 2 + 1))
                {
                    return await FailComputeAsync(size, $"oracle mismatch at {i}: {actual[i]}");
                }
            }
            await Console.Out.WriteLineAsync($"compute-size={size} groups={groups} pass=oracle");
        }

        var recordStride = 16u;
        await using var recordBuffer = device.CreateStorageBuffer(64);
        var payloadBytes = Enumerable.Range(0, (int)recordStride).Select(static value => (byte)value).ToArray();
        var changes = new[]
        {
            RenderRecordChange.Upsert(1, 7, payloadBytes),
            RenderRecordChange.Upsert(2, 7, payloadBytes),
            RenderRecordChange.Remove(3, 7)
        };
        var update = device.ApplyDirtyRecords(recordBuffer, changes, recordStride, 4);
        if (!update.Succeeded || update.UploadRuns != 1)
        {
            return await FailComputeAsync(-1, update.Error ?? "dirty-record update");
        }

        var records = new byte[64];
        if (!device.Readback(recordBuffer, records))
        {
            return await FailComputeAsync(-1, "dirty-record readback");
        }

        if (!records.AsSpan(16, 16).SequenceEqual(payloadBytes) || !records.AsSpan(32, 16).SequenceEqual(payloadBytes) || !records.AsSpan(48, 16).SequenceEqual(new byte[16]))
        {
            return await FailComputeAsync(-1, "dirty-record oracle mismatch");
        }

        await Console.Out.WriteLineAsync(DirtyRecordsMessage);

        return 0;
    }

    private static async Task<int> RunMultiBufferComputeSizesAsync(VulkanComputeDevice device, IComputePipeline pipeline)
    {
        var localSizeX = pipeline.Abi.WorkgroupSize.X;
        foreach (var size in new[] { 0, 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var input = device.CreateStorageBuffer((ulong)size * sizeof(uint), ComputeBufferAccess.ReadOnly);
            await using var output = device.CreateStorageBuffer((ulong)size * sizeof(uint), ComputeBufferAccess.ReadWrite);
            if (size == 0)
            {
                var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
                if (noOp.Status != ComputeDispatchStatus.NoOp)
                {
                    await Console.Error.WriteLineAsync("zero-sized multi-buffer dispatch was not a no-op");
                    return 1;
                }
                await Console.Out.WriteLineAsync(MultiComputeZeroMessage);
                continue;
            }

            var values = new uint[size];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (uint)i;
            }

            var inputBytes = MemoryMarshal.AsBytes(values.AsSpan());
            if (!device.Upload(input, inputBytes))
            {
                return await FailComputeAsync(size, "multi-buffer input upload");
            }

            var groups = (uint)((size + (int)localSizeX - 1) / (int)localSizeX);
            var dispatch = device.Dispatch(
                pipeline,
                new[]
                {
                    new ComputeBufferBinding(0, 0, input),
                    new ComputeBufferBinding(0, 1, output)
                },
                groups);
            if (!dispatch.Succeeded || dispatch.Status != ComputeDispatchStatus.Executed)
            {
                return await FailComputeAsync(size, dispatch.Error ?? "multi-buffer dispatch");
            }

            var outputBytes = new byte[inputBytes.Length];
            if (!device.Readback(output, outputBytes))
            {
                return await FailComputeAsync(size, "multi-buffer readback");
            }

            var actual = MemoryMarshal.Cast<byte, uint>(outputBytes);
            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != (uint)(i * 2 + 1))
                {
                    return await FailComputeAsync(size, $"multi-buffer oracle mismatch at {i}: {actual[i]}");
                }
            }
            await Console.Out.WriteLineAsync($"multi-compute-size={size} groups={groups} pass=oracle");
        }

        return 0;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task<int> FailComputeAsync(int size, string reason)
    {
        await Console.Error.WriteLineAsync($"compute-size={size} failed: {reason}");
        return 1;
    }
}
