using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Delta.Render.Core;
using Delta.Render.Platform.SDL3;
using Delta.Render.Vulkan;
using DeltaShaderArtifact = Delta.Shader.Abstractions.ShaderArtifact;
using DeltaShaderManifest = Delta.Shader.Abstractions.ShaderAbiManifest;

namespace Delta.Render.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--compute", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunComputeSmokeAsync(GetOption(args, "--compute-shader"), GetOption(args, "--compute-manifest"));
        }

        var clearOnly = args.Any(a => string.Equals(a, "--clear", StringComparison.OrdinalIgnoreCase));
        var panel = args.Any(a => string.Equals(a, "--panel", StringComparison.OrdinalIgnoreCase));
        var interactive = args.Any(a => string.Equals(a, "--interactive", StringComparison.OrdinalIgnoreCase));

        var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        if (headless)
        {
            var probe = VulkanEnvironmentProbe.CheckHeadless();
            Console.WriteLine(probe.Diagnostics.ToText());
            return probe.Usable ? 0 : 1;
        }

        var factory = new Sdl3WindowFactory();
        var result = factory.CreateWindow(new WindowConfiguration("Delta.Render Smoke", 960, 540, true, true));

        if (!result.Success || result.Window is null)
        {
            Console.Error.WriteLine("Window creation failed");
            Console.Error.WriteLine(result.Diagnostics.ToText());
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
                var frameState = session.BeginFrame();
                if (!frameState.IsValid || !session.EndFrame(in frameState, ReadOnlySpan<RenderRecordChange>.Empty))
                {
                    Console.Error.WriteLine("Failed to render clear frame.");
                    return 1;
                }
            }
            else
            {
                var vertexPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.vert.spv");
                var fragmentPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.frag.spv");
                var vertexManifestPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.vert.shader.json");
                var fragmentManifestPath = Path.Combine(AppContext.BaseDirectory, "shaders", "fullscreen-rounded-rectangle.frag.shader.json");
                if (panel)
                {
                    vertexPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.vert.spv");
                    fragmentPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.frag.spv");
                    vertexManifestPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.vert.shader.json");
                    fragmentManifestPath = Path.Combine(AppContext.BaseDirectory, "shaders", "ui-panel.frag.shader.json");
                }
                if (!File.Exists(vertexPath) || !File.Exists(fragmentPath) ||
                    !File.Exists(vertexManifestPath) || !File.Exists(fragmentManifestPath))
                {
                    Console.Error.WriteLine("Graphics fixtures were not found; use --clear for the swapchain-only path.");
                    return 1;
                }

                var program = new GraphicsShaderProgram(
                    LoadShaderArtifact(vertexPath, vertexManifestPath),
                    LoadShaderArtifact(fragmentPath, fragmentManifestPath));
                await using var pipeline = session.CreateGraphicsPipeline(in program);
                var stopwatch = Stopwatch.StartNew();
                var frames = GetOption(args, "--frames") is { } frameText && int.TryParse(frameText, out var parsedFrames)
                    ? Math.Max(1, parsedFrames)
                    : 1;
                var renderedFrames = 0;
                IUiDrawListProvider panelAdapter = new PanelUiAdapter();
                while (interactive || renderedFrames < frames)
                {
                    Sdl3WindowFactory.PumpEvents();
                    var parameters = new GraphicsFrameParameters(window.Metrics.Width, window.Metrics.Height, (float)stopwatch.Elapsed.TotalSeconds);
                    var drawList = new UiDrawList(panelAdapter.CurrentDrawList);
                    var rendered = panel
                        ? session.SubmitFrame(pipeline, in parameters, in drawList, ReadOnlySpan<RenderRecordChange>.Empty)
                        : SubmitFullscreenFrame(session, pipeline, in parameters);
                    if (!rendered)
                    {
                        Console.Error.WriteLine("Failed to render fullscreen graphics frame.");
                        return 1;
                    }

                    renderedFrames++;
                }
                Console.WriteLine($"graphics={(panel ? "ui-panel" : "fullscreen-rounded-rectangle")} frames={renderedFrames} pass=present");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Renderer initialization failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }

        Sdl3WindowFactory.PumpEvents();
        return 0;
    }

    private static DeltaShaderArtifact LoadShaderArtifact(string spirvPath, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<DeltaShaderManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException($"Shader manifest was empty: {manifestPath}");
        if (manifest.Version != DeltaShaderManifest.CurrentVersion)
        {
            manifest = new DeltaShaderManifest
            {
                Version = DeltaShaderManifest.CurrentVersion,
                Stage = manifest.Stage,
                SourceEntryPointName = manifest.SourceEntryPointName,
                EntryPointName = manifest.EntryPointName,
                TargetProfile = manifest.TargetProfile,
                GlslVersion = manifest.GlslVersion,
                SpirvVersion = manifest.SpirvVersion,
                StorageLayout = manifest.StorageLayout,
                LocalSizeX = manifest.LocalSizeX,
                LocalSizeY = manifest.LocalSizeY,
                LocalSizeZ = manifest.LocalSizeZ,
                Resources = manifest.Resources,
                Inputs = manifest.Inputs,
                VertexInputs = manifest.VertexInputs,
                VertexBufferBindings = manifest.VertexBufferBindings,
                Outputs = manifest.Outputs,
                PushConstants = manifest.PushConstants
            };
        }
        return new DeltaShaderArtifact(File.ReadAllBytes(spirvPath), manifest);
    }

    private static bool SubmitFullscreenFrame(
        IRenderWindowFrameSession session,
        IGraphicsPipeline pipeline,
        in GraphicsFrameParameters parameters)
    {
        var frameState = session.BeginFrame();
        return frameState.IsValid && session.DrawFullscreenTriangle(pipeline, in parameters) &&
               session.EndFrame(in frameState, ReadOnlySpan<RenderRecordChange>.Empty);
    }

    private sealed class PanelUiAdapter : IUiDrawListProvider
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

    private static async Task<int> RunComputeSmokeAsync(string? externalShaderPath, string? externalManifestPath)
    {
        var shaderPath = externalShaderPath ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "compute_double.spv");
        if (!File.Exists(shaderPath))
        {
            Console.Error.WriteLine($"Compute shader was not found: {shaderPath}");
            return 1;
        }

        Console.WriteLine($"compute-shader={Path.GetFullPath(shaderPath)}");
        var shader = File.ReadAllBytes(shaderPath);
        var metadata = new ComputeShaderMetadata(
            ComputeAbiLayout.Std430,
            64,
            1,
            1,
            new[] { new ComputeDescriptorBinding(0, 0, ComputeDescriptorKind.StorageBuffer, ComputeBufferAccess.ReadWrite) });

        await using var device = new VulkanComputeDevice(new VulkanRendererOptions());
        IComputePipeline pipeline;
        if (externalShaderPath is not null)
        {
            if (string.IsNullOrWhiteSpace(externalManifestPath) || !File.Exists(externalManifestPath))
            {
                Console.Error.WriteLine("Generated compute smoke requires --compute-manifest alongside --compute-shader.");
                return 1;
            }

            var manifest = JsonSerializer.Deserialize<DeltaShaderManifest>(await File.ReadAllTextAsync(externalManifestPath));
            if (manifest is null)
            {
                Console.Error.WriteLine($"Delta.Shader manifest was empty: {externalManifestPath}");
                return 1;
            }

            pipeline = device.CreateComputePipeline(new DeltaShaderArtifact(shader, manifest));
        }
        else
        {
            pipeline = device.CreateComputePipeline(shader, in metadata);
        }

        await using (pipeline)
        {
            return await RunComputeSizesAsync(device, pipeline);
        }
    }

    private static async Task<int> RunComputeSizesAsync(VulkanComputeDevice device, IComputePipeline pipeline)
    {
        if (pipeline.Metadata.Bindings.Length == 2)
        {
            return await RunMultiBufferComputeSizesAsync(device, pipeline);
        }

        var localSizeX = pipeline.Metadata.LocalSizeX;

        foreach (var size in new[] { 0, 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var buffer = device.CreateStorageBuffer((ulong)size * sizeof(uint));
            if (size == 0)
            {
                var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
                if (noOp.Status != ComputeDispatchStatus.NoOp)
                {
                    Console.Error.WriteLine("zero-sized dispatch was not a no-op");
                    return 1;
                }
                Console.WriteLine("compute-size=0 pass=no-op");
                continue;
            }

            var values = new uint[size];
            for (var i = 0; i < values.Length; i++) values[i] = (uint)i;
            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            if (!device.Upload(buffer, bytes)) return FailCompute(size, "upload");

            var groups = (uint)((size + (int)localSizeX - 1) / (int)localSizeX);
            var dispatch = device.Dispatch(pipeline, new[] { new ComputeBufferBinding(0, 0, buffer) }, groups);
            if (!dispatch.Succeeded || dispatch.Status != ComputeDispatchStatus.Executed) return FailCompute(size, dispatch.Error ?? "dispatch");

            var output = new byte[bytes.Length];
            if (!device.Readback(buffer, output)) return FailCompute(size, "readback");
            var actual = MemoryMarshal.Cast<byte, uint>(output);
            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != (uint)(i * 2 + 1)) return FailCompute(size, $"oracle mismatch at {i}: {actual[i]}");
            }
            Console.WriteLine($"compute-size={size} groups={groups} pass=oracle");
        }

        var recordStride = 16u;
        await using var recordBuffer = device.CreateStorageBuffer(64);
        var payload = Marshal.AllocHGlobal((int)recordStride);
        try
        {
            var payloadBytes = Enumerable.Range(0, (int)recordStride).Select(static value => (byte)value).ToArray();
            Marshal.Copy(payloadBytes, 0, payload, payloadBytes.Length);
            var changes = new[]
            {
                RenderRecordChange.Upsert(1, 7, (ulong)payload, recordStride),
                RenderRecordChange.Upsert(2, 7, (ulong)payload, recordStride),
                RenderRecordChange.Remove(3, 7)
            };
            var update = device.ApplyDirtyRecords(recordBuffer, changes, recordStride, 4);
            if (!update.Succeeded || update.UploadRuns != 1) return FailCompute(-1, update.Error ?? "dirty-record update");
            var records = new byte[64];
            if (!device.Readback(recordBuffer, records)) return FailCompute(-1, "dirty-record readback");
            if (!records.AsSpan(16, 16).SequenceEqual(payloadBytes) || !records.AsSpan(32, 16).SequenceEqual(payloadBytes) || !records.AsSpan(48, 16).SequenceEqual(new byte[16]))
                return FailCompute(-1, "dirty-record oracle mismatch");
            Console.WriteLine("dirty-records pass=coalesced");
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }

        return 0;
    }

    private static async Task<int> RunMultiBufferComputeSizesAsync(VulkanComputeDevice device, IComputePipeline pipeline)
    {
        var localSizeX = pipeline.Metadata.LocalSizeX;
        foreach (var size in new[] { 0, 1, 63, 64, 65, 128, 129, 256 })
        {
            await using var input = device.CreateStorageBuffer((ulong)size * sizeof(uint), ComputeBufferAccess.ReadOnly);
            await using var output = device.CreateStorageBuffer((ulong)size * sizeof(uint), ComputeBufferAccess.ReadWrite);
            if (size == 0)
            {
                var noOp = device.Dispatch(pipeline, ReadOnlySpan<ComputeBufferBinding>.Empty, 0);
                if (noOp.Status != ComputeDispatchStatus.NoOp)
                {
                    Console.Error.WriteLine("zero-sized multi-buffer dispatch was not a no-op");
                    return 1;
                }
                Console.WriteLine("multi-compute-size=0 pass=no-op");
                continue;
            }

            var values = new uint[size];
            for (var i = 0; i < values.Length; i++) values[i] = (uint)i;
            var inputBytes = MemoryMarshal.AsBytes(values.AsSpan());
            if (!device.Upload(input, inputBytes)) return FailCompute(size, "multi-buffer input upload");

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
                return FailCompute(size, dispatch.Error ?? "multi-buffer dispatch");

            var outputBytes = new byte[inputBytes.Length];
            if (!device.Readback(output, outputBytes)) return FailCompute(size, "multi-buffer readback");
            var actual = MemoryMarshal.Cast<byte, uint>(outputBytes);
            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != (uint)(i * 2 + 1)) return FailCompute(size, $"multi-buffer oracle mismatch at {i}: {actual[i]}");
            }
            Console.WriteLine($"multi-compute-size={size} groups={groups} pass=oracle");
        }

        return 0;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private static int FailCompute(int size, string reason)
    {
        Console.Error.WriteLine($"compute-size={size} failed: {reason}");
        return 1;
    }
}
