using System.Globalization;
using Delta;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader.Contract;
using Delta.Render.Mesh;

namespace Delta.Render.MeshSample;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shaderDirectory = GetOption(args, "--shader-dir") ?? ResolveProducerShaderDirectory();
        var outputPath = GetOption(args, "--output") ?? Path.Combine("artifacts", "mesh-sample", "mesh.ppm");
        var width = ParseUInt(args, "--width", 640);
        var height = ParseUInt(args, "--height", 480);
        if (width == 0 || height == 0)
        {
            await Console.Error.WriteLineAsync("--width and --height must be greater than zero.").ConfigureAwait(false);
            return 2;
        }

        var vertexPath = Path.Combine(shaderDirectory, "Mesh.vert.spv");
        var fragmentPath = Path.Combine(shaderDirectory, "Fragment.frag.spv");
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath))
        {
            await Console.Error.WriteLineAsync($"Missing mesh shader pair: {vertexPath} and {fragmentPath}").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var vertexSpirv = await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false);
            var fragmentSpirv = await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false);
            var program = MeshGraphicsShaderProgram.CreateProgram(vertexSpirv, fragmentSpirv);

            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var rendererScope = renderer.ConfigureAwait(false);
            var session = renderer.CreateHeadlessSession(width, height);
            await using var sessionScope = session.ConfigureAwait(false);
            var graph = session.CreateRenderGraph();
            var feature = new MeshFeature(session, program, width, height);
            IRenderFeature[] features = [feature];
            graph.Build(0, features);
            var result = graph.Execute();
            if (result.Status != RenderGraphExecutionStatus.Submitted)
            {
                await Console.Error.WriteLineAsync($"Mesh graph execution failed: {result.Status}").ConfigureAwait(false);
                return 1;
            }

            var rgba = new byte[checked((int)((ulong)width * height * 4))];
            if (graph.CopyReadback(feature.Readback, rgba) != rgba.Length)
            {
                await Console.Error.WriteLineAsync("Mesh graph readback did not complete.").ConfigureAwait(false);
                return 1;
            }

            SavePpm(outputPath, width, height, rgba);
            await Console.Out.WriteLineAsync($"mesh-sample target={width}x{height} indexedVertices=6 output={Path.GetFullPath(outputPath)}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Mesh sample failed:").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static uint ParseUInt(string[] args, string option, uint fallback)
    {
        var value = GetOption(args, option);
        return value is not null && uint.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string ResolveProducerShaderDirectory()
    {
        var producerRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/DeltaRender.Mesh/bin"));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var directory = Path.Combine(producerRoot, configuration, "net10.0", "DeltaShader", "DeltaRender.Mesh");
            if (File.Exists(Path.Combine(directory, "Mesh.vert.spv")) && File.Exists(Path.Combine(directory, "Fragment.frag.spv")))
            {
                return directory;
            }
        }

        return Path.Combine(producerRoot, "Release", "net10.0", "DeltaShader", "DeltaRender.Mesh");
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

    private sealed class MeshFeature : IRenderFeature
    {
        private static readonly MeshPayload[] Vertices =
        [
            new() { Position = new float4(-0.65f, -0.55f, 0f, 1f), Normal = new float3(1f, 0f, 0f), Uv = new float2(0f, 0f) },
            new() { Position = new float4(0.65f, -0.55f, 0f, 1f), Normal = new float3(0f, 1f, 0f), Uv = new float2(1f, 0f) },
            new() { Position = new float4(0.65f, 0.55f, 0f, 1f), Normal = new float3(0f, 0f, 1f), Uv = new float2(1f, 1f) },
            new() { Position = new float4(-0.65f, 0.55f, 0f, 1f), Normal = new float3(1f, 1f, 0f), Uv = new float2(0f, 1f) },
            new() { Position = new float4(-0.65f, -0.55f, 0f, 1f), Normal = new float3(1f, 0f, 0f), Uv = new float2(0f, 0f) },
            new() { Position = new float4(0.65f, 0.55f, 0f, 1f), Normal = new float3(0f, 0f, 1f), Uv = new float2(1f, 1f) },
        ];

        private static readonly byte[] VertexData = MeshGraphicsShaderProgram.PackMeshVertexElements(Vertices);

        private static readonly byte[] IndexData = [0, 0, 1, 0, 2, 0, 2, 0, 1, 0, 3, 0];

        private readonly IRenderFrameSession _session;
        private readonly IGraphicsShaderProgram _program;
        private readonly RenderTargetHandle _target;
        private readonly RenderBufferHandle _vertexBuffer;
        private readonly RenderBufferHandle _indexBuffer;
        private readonly RenderViewport _viewport;
        private readonly PixelRect _scissor;
        private readonly RasterPassDescription _rasterDescription;
        private readonly UploadPass _uploadPass;
        private readonly DrawPass _drawPass;
        private RenderGraphBufferHandle _vertexGraphBuffer;
        private RenderGraphBufferHandle _indexGraphBuffer;
        private RenderGraphReadbackHandle _readback;

        internal MeshFeature(IRenderFrameSession session, IGraphicsShaderProgram program, uint width, uint height)
        {
            _session = session;
            _program = program;
            _target = session.Target;
            _vertexBuffer = session.CreateBuffer(new RenderBufferDescription((ulong)VertexData.Length, RenderBufferUsage.Vertex | RenderBufferUsage.TransferDestination));
            _indexBuffer = session.CreateBuffer(new RenderBufferDescription((ulong)IndexData.Length, RenderBufferUsage.Index | RenderBufferUsage.TransferDestination));
            _viewport = new RenderViewport(0, 0, width, height);
            _scissor = new PixelRect(0, 0, checked((int)width), checked((int)height));
            _rasterDescription = new RasterPassDescription(
                "mesh-sample",
                new RasterPipelineDescription(program, PrimitiveTopology.TriangleList, RasterCullMode.None));
            _uploadPass = new UploadPass(this);
            _drawPass = new DrawPass(this);
        }

        internal RenderGraphReadbackHandle Readback => _readback;

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            _vertexGraphBuffer = graph.ImportBuffer(_vertexBuffer);
            _indexGraphBuffer = graph.ImportBuffer(_indexBuffer);
            var upload = graph.AddTransferPass("mesh-sample-upload", _uploadPass);
            graph.UseBuffer(upload, _vertexGraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
            graph.UseBuffer(upload, _indexGraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);

            var target = graph.ImportTarget(_target);
            var raster = graph.AddRasterPass(_rasterDescription, _drawPass);
            graph.UseBuffer(raster, _vertexGraphBuffer, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
            graph.UseBuffer(raster, _indexGraphBuffer, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
            graph.UseColorAttachment(
                raster,
                0,
                new ColorAttachmentDescription(
                    target,
                    AttachmentLoadOperation.Clear,
                    AttachmentStoreOperation.Store,
                    new ClearColor(0.05f, 0.05f, 0.05f, 1f)));
            _readback = graph.ReadbackTexture(target, new PixelRect(0, 0, _scissor.Width, _scissor.Height));
        }

        private sealed class UploadPass(MeshFeature feature) : ITransferPass
        {
            public void Record(ITransferCommandContext commands)
            {
                commands.UploadBuffer(feature._vertexGraphBuffer, VertexData);
                commands.UploadBuffer(feature._indexGraphBuffer, IndexData);
            }
        }

        private sealed class DrawPass(MeshFeature feature) : IRasterPass
        {
            public void Record(IRasterCommandContext commands)
            {
                commands.SetViewport(in feature._viewport);
                commands.SetScissor(in feature._scissor);
                commands.BindVertexBuffer(0, feature._vertexGraphBuffer);
                commands.BindIndexBuffer(feature._indexGraphBuffer, IndexElementFormat.UnsignedShort);
                commands.DrawIndexed(6);
            }
        }
    }
}
