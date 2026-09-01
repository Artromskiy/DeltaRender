using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Delta.Maths;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Render.Vulkan;
using Delta.Shader;
using Delta.Shader.ComputerPbr;
using Delta.Shader.Contract;
using static Delta.Maths.maths;

namespace Delta.Render.ComputerPbr;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = SampleOptions.Parse(args);
        try
        {
            var asset = ModelAsset.Load(options.ModelDirectory);
            var shaderDirectory = options.ShaderDirectory;
            var vertexPath = Path.Combine(shaderDirectory, "DeltaShader.ComputerPbrModel.Mesh.vert.spv");
            var fragmentPath = Path.Combine(shaderDirectory, "DeltaShader.ComputerPbrModel.MeshFragment.frag.spv");
            var vertexSpirv = await File.ReadAllBytesAsync(vertexPath).ConfigureAwait(false);
            var fragmentSpirv = await File.ReadAllBytesAsync(fragmentPath).ConfigureAwait(false);
            var program = ComputerPbrTexturedCompositeGraphicsShaderProgram.CreateProgram(vertexSpirv, fragmentSpirv);

            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var rendererScope = renderer.ConfigureAwait(false);
            var session = renderer.CreateHeadlessSession((uint)options.Width, (uint)options.Height);
            await using var sessionScope = session.ConfigureAwait(false);
            var graph = session.CreateRenderGraph();
            var feature = new LaptopPbrFeature(session, program, asset, options);
            IRenderFeature[] features = [feature];
            graph.Build(0, features);
            var result = graph.Execute();
            if (result.Status != RenderGraphExecutionStatus.Submitted)
            {
                await Console.Error.WriteLineAsync($"PBR graph execution failed: {result.Status}").ConfigureAwait(false);
                foreach (var diagnostic in result.Diagnostics.ToArray())
                {
                    await Console.Error.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
                }

                return 1;
            }

            var rgba = new byte[checked(options.Width * options.Height * 4)];
            if (graph.CopyReadback(feature.Readback, rgba) != rgba.Length)
            {
                await Console.Error.WriteLineAsync("PBR graph readback did not complete.").ConfigureAwait(false);
                return 1;
            }

            SavePpm(options.OutputPath, options.Width, options.Height, rgba);
            await Console.Out.WriteLineAsync(
                $"computer-pbr-model target={options.Width}x{options.Height} vertices={asset.Vertices.Length} indices={asset.IndexCount} output={Path.GetFullPath(options.OutputPath)}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync("Computer PBR sample failed:").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static void SavePpm(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory);
        var rgb = new byte[checked(width * height * 3)];
        for (var source = 0; source < rgba.Length; source += 4)
        {
            var destination = source / 4 * 3;
            rgb[destination] = rgba[source];
            rgb[destination + 1] = rgba[source + 1];
            rgb[destination + 2] = rgba[source + 2];
        }

        using var stream = File.Create(fullPath);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        stream.Write(rgb);
    }

    internal static float ReadAngleDegrees()
    {
        var value = Environment.GetEnvironmentVariable("DELTA_PBR_ANGLE_DEGREES");
        return string.IsNullOrWhiteSpace(value)
            ? 0.0f
            : float.Parse(value, CultureInfo.InvariantCulture);
    }
}

internal sealed class LaptopPbrFeature : IRenderFeature
{
    private static readonly uint[] TextureBindings = [4, 5, 6, 7, 8, 9];

    private readonly IRenderFrameSession _session;
    private readonly IGraphicsShaderProgram _program;
    private readonly ModelAsset _asset;
    private readonly SampleOptions _options;
    private readonly RenderTargetHandle _target;
    private readonly RenderBufferHandle _vertexBuffer;
    private readonly RenderBufferHandle _indexBuffer;
    private readonly RenderTextureHandle[] _textures;
    private readonly RenderSamplerHandle[] _samplers;
    private readonly RenderViewport _viewport;
    private readonly PixelRect _scissor;
    private readonly RasterPassDescription _rasterDescription;
    private readonly UploadPass _uploadPass;
    private readonly DrawPass _drawPass;
    private readonly byte[] _vertexData;
    private readonly byte[] _indexData;
    private readonly byte[] _frameData;
    private RenderGraphBufferHandle _vertexGraphBuffer;
    private RenderGraphBufferHandle _indexGraphBuffer;
    private RenderGraphTextureHandle[] _textureGraphHandles = [];
    private RenderGraphReadbackHandle _readback;

    public LaptopPbrFeature(
        IRenderFrameSession session,
        IGraphicsShaderProgram program,
        ModelAsset asset,
        SampleOptions options)
    {
        _session = session;
        _program = program;
        _asset = asset;
        _options = options;
        _target = session.Target;
        _vertexData = ComputerPbrTexturedCompositeGraphicsShaderProgram.PackMeshVertexElements(asset.Vertices);
        _indexData = asset.IndexBytes;
        _frameData = ComputerPbrTexturedCompositeGraphicsShaderProgram.PackMeshFrame(
            ModelFrameBuilder.Create(asset, options.Width, options.Height, Program.ReadAngleDegrees()));
        _vertexBuffer = session.CreateBuffer(new RenderBufferDescription(
            (ulong)_vertexData.Length,
            RenderBufferUsage.Vertex | RenderBufferUsage.TransferDestination));
        _indexBuffer = session.CreateBuffer(new RenderBufferDescription(
            (ulong)_indexData.Length,
            RenderBufferUsage.Index | RenderBufferUsage.TransferDestination));
        _textures = new RenderTextureHandle[asset.Textures.Count];
        _samplers = new RenderSamplerHandle[asset.Textures.Count];
        for (var index = 0; index < asset.Textures.Count; index++)
        {
            var texture = asset.Textures[index];
            _textures[index] = session.CreateTexture(new RenderTextureDescription(
                (uint)texture.Image.Width,
                (uint)texture.Image.Height,
                texture.Format,
                RenderTextureUsage.Sampled | RenderTextureUsage.TransferDestination));
            _samplers[index] = session.CreateSampler(new RenderSamplerDescription());
        }

        _viewport = new RenderViewport(0, 0, options.Width, options.Height);
        _scissor = new PixelRect(0, 0, options.Width, options.Height);
        _rasterDescription = new RasterPassDescription(
            "computer-pbr-model",
            new RasterPipelineDescription(program, PrimitiveTopology.TriangleList, RasterCullMode.None));
        _uploadPass = new UploadPass(this);
        _drawPass = new DrawPass(this);
    }

    public RenderGraphReadbackHandle Readback => _readback;

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        _vertexGraphBuffer = graph.ImportBuffer(_vertexBuffer);
        _indexGraphBuffer = graph.ImportBuffer(_indexBuffer);
        _textureGraphHandles = new RenderGraphTextureHandle[_textures.Length];
        for (var index = 0; index < _textures.Length; index++)
        {
            _textureGraphHandles[index] = graph.ImportTexture(_textures[index]);
        }

        var upload = graph.AddTransferPass("computer-pbr-model-upload", _uploadPass);
        graph.UseBuffer(upload, _vertexGraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        graph.UseBuffer(upload, _indexGraphBuffer, RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        for (var index = 0; index < _textureGraphHandles.Length; index++)
        {
            graph.UseTexture(upload, _textureGraphHandles[index], RenderResourceAccess.Write, RenderPipelineStages.Transfer);
        }

        var raster = graph.AddRasterPass(_rasterDescription, _drawPass);
        graph.UseBuffer(raster, _vertexGraphBuffer, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
        graph.UseBuffer(raster, _indexGraphBuffer, RenderResourceAccess.Read, RenderPipelineStages.Vertex);
        for (var index = 0; index < _textureGraphHandles.Length; index++)
        {
            graph.UseTexture(raster, _textureGraphHandles[index], RenderResourceAccess.Read, RenderPipelineStages.Fragment);
        }

        var target = graph.ImportTarget(_target);
        graph.UseColorAttachment(
            raster,
            0,
            new ColorAttachmentDescription(
                target,
                AttachmentLoadOperation.Clear,
                AttachmentStoreOperation.Store,
                new ClearColor(0.025f, 0.035f, 0.06f, 1f)));
        _readback = graph.ReadbackTexture(target, _scissor);
    }

    private sealed class UploadPass(LaptopPbrFeature feature) : ITransferPass
    {
        public void Record(ITransferCommandContext commands)
        {
            commands.UploadBuffer(feature._vertexGraphBuffer, feature._vertexData);
            commands.UploadBuffer(feature._indexGraphBuffer, feature._indexData);
            for (var index = 0; index < feature._textureGraphHandles.Length; index++)
            {
                var image = feature._asset.Textures[index].Image;
                commands.UploadTexture(
                    feature._textureGraphHandles[index],
                    new PixelRect(0, 0, image.Width, image.Height),
                    image.Pixels,
                    checked((uint)(image.Width * 4)));
            }
        }
    }

    private sealed class DrawPass(LaptopPbrFeature feature) : IRasterPass
    {
        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in feature._viewport);
            commands.SetScissor(in feature._scissor);
            for (var index = 0; index < feature._textureGraphHandles.Length; index++)
            {
                commands.BindTexture(
                    new ShaderBinding(0, TextureBindings[index]),
                    feature._textureGraphHandles[index],
                    feature._samplers[index]);
            }

            commands.BindVertexBuffer(0, feature._vertexGraphBuffer);
            commands.BindIndexBuffer(feature._indexGraphBuffer, IndexElementFormat.UnsignedShort);
            commands.PushConstants(feature._frameData);
            commands.DrawIndexed((uint)feature._asset.IndexCount);
        }
    }
}

internal static class ModelFrameBuilder
{
    public static ComputerMeshFrame Create(ModelAsset asset, int width, int height, float angleDegrees)
    {
        var scale = 1.55f / MathF.Max(asset.Extent.x, asset.Extent.z);
        var model = createScale(scale) * createTranslation(
            new float3(-asset.Center.x, -asset.Center.y, -asset.Center.z));
        var angle = angleDegrees * 0.017453292f;
        var eye = new float3(
            MathF.Sin(angle) * 2.75f,
            0.75f,
            MathF.Cos(angle) * 2.75f);
        var target = new float3(0.0f, 0.15f, 0.0f);
        var view = createLookTo(eye, normalize(target - eye), new float3(0.0f, 1.0f, 0.0f));
        var projection = createPerspectiveFieldOfViewLeftHanded(
            48.0f * 0.017453292f,
            (float)width / height,
            0.01f,
            100.0f);

        return new ComputerMeshFrame(
            projection * view * model,
            normalize(new float3(-0.45f, 0.85f, 0.35f)),
            eye,
            1.25f);
    }
}

internal sealed class ModelAsset
{
    private ModelAsset(
        string name,
        ComputerMeshPayload[] vertices,
        byte[] indexBytes,
        int indexCount,
        IReadOnlyList<ModelTexture> textures,
        float3 center,
        float3 extent)
    {
        Name = name;
        Vertices = vertices;
        IndexBytes = indexBytes;
        IndexCount = indexCount;
        Textures = textures;
        Center = center;
        Extent = extent;
    }

    public string Name { get; }
    public ComputerMeshPayload[] Vertices { get; }
    public byte[] IndexBytes { get; }
    public int IndexCount { get; }
    public IReadOnlyList<ModelTexture> Textures { get; }
    public float3 Center { get; }
    public float3 Extent { get; }

    public static ModelAsset Load(string directory)
    {
        var gltfPath = Directory.GetFiles(directory, "*.gltf").Single();
        using var document = JsonDocument.Parse(File.ReadAllBytes(gltfPath));
        var root = document.RootElement;
        var buffer = File.ReadAllBytes(Path.Combine(directory, root.GetProperty("buffers")[0].GetProperty("uri").GetString()!));
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        var positions = ReadFloatVectors(root, buffer, attributes.GetProperty("POSITION").GetInt32(), 3);
        var normals = ReadFloatVectors(root, buffer, attributes.GetProperty("NORMAL").GetInt32(), 3);
        var tangents = ReadFloatVectors(root, buffer, attributes.GetProperty("TANGENT").GetInt32(), 4);
        var uvs = ReadFloatVectors(root, buffer, attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
        var indices = ReadIndices(root, buffer, primitive.GetProperty("indices").GetInt32());
        var vertices = new ComputerMeshPayload[positions.Length / 3];
        var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new float3(float.MinValue, float.MinValue, float.MinValue);
        for (var index = 0; index < vertices.Length; index++)
        {
            var position = new float3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
            var normal = new float3(normals[index * 3], normals[index * 3 + 1], normals[index * 3 + 2]);
            var uv = new float2(uvs[index * 2], uvs[index * 2 + 1]);
            var tangent = new float4(
                tangents[index * 4],
                tangents[index * 4 + 1],
                tangents[index * 4 + 2],
                tangents[index * 4 + 3]);
            min = new float3(MathF.Min(min.x, position.x), MathF.Min(min.y, position.y), MathF.Min(min.z, position.z));
            max = new float3(MathF.Max(max.x, position.x), MathF.Max(max.y, position.y), MathF.Max(max.z, position.z));
            vertices[index] = new ComputerMeshPayload
            {
                Position = new Position(new float4(position, 1f)),
                WorldNormal = new WorldNormal(normalize(normal)),
                Uv = new Uv0(uv),
                Tangent = new Tangent(tangent),
                WorldPosition = new WorldPosition(position)
            };
        }

        return new ModelAsset(
            Path.GetFileNameWithoutExtension(gltfPath),
            vertices,
            ToIndexBytes(indices),
            indices.Length,
            LoadTextures(root, directory),
            (min + max) * 0.5f,
            max - min);
    }

    public ComputerMeshFrame CreateFrame(int width, int height)
    {
        var scale = 1.55f / MathF.Max(Extent.x, Extent.z);
        var model = float4x4.CreateScale(scale) * float4x4.CreateTranslation(-Center);
        var eye = new float3(0f, 0.75f, 2.75f);
        var view = float4x4.CreateLookTo(eye, new float3(0f, -0.08f, -1f), new float3(0f, 1f, 0f));
        var projection = float4x4.CreatePerspectiveFieldOfViewLeftHanded(
            radians(48f),
            width / (float)height,
            0.01f,
            100f);
        return new ComputerMeshFrame(
            projection * view * model,
            normalize(new float3(-0.45f, -0.8f, -0.35f)),
            eye,
            1.25f);
    }

    private static float[] ReadFloatVectors(JsonElement root, byte[] buffer, int accessorIndex, int components)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var baseOffset = view.GetProperty("byteOffset").GetInt32OrDefault() + accessor.GetProperty("byteOffset").GetInt32OrDefault();
        var stride = view.GetProperty("byteStride").GetInt32OrDefault(components * sizeof(float));
        var count = accessor.GetProperty("count").GetInt32();
        var values = new float[count * components];
        for (var index = 0; index < count; index++)
        {
            var offset = baseOffset + index * stride;
            for (var component = 0; component < components; component++)
            {
                values[index * components + component] = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + component * sizeof(float))));
            }
        }

        return values;
    }

    private static ushort[] ReadIndices(JsonElement root, byte[] buffer, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var offset = view.GetProperty("byteOffset").GetInt32OrDefault() + accessor.GetProperty("byteOffset").GetInt32OrDefault();
        var count = accessor.GetProperty("count").GetInt32();
        var result = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + index * sizeof(ushort)));
        }

        return result;
    }

    private static byte[] ToIndexBytes(ushort[] indices)
    {
        var bytes = new byte[indices.Length * sizeof(ushort)];
        for (var index = 0; index < indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * sizeof(ushort)), indices[index]);
        }

        return bytes;
    }

    private static List<ModelTexture> LoadTextures(JsonElement root, string directory)
    {
        var images = root.GetProperty("images");
        var textures = root.GetProperty("textures");
        var material = root.GetProperty("materials")[0];
        var pbr = material.GetProperty("pbrMetallicRoughness");
        var bindings = new[]
        {
            (4u, pbr, "baseColorTexture", RenderTextureFormat.Rgba8Srgb),
            (5u, pbr, "metallicRoughnessTexture", RenderTextureFormat.Rgba8Unorm),
            (6u, material, "normalTexture", RenderTextureFormat.Rgba8Unorm),
            (7u, pbr, "metallicRoughnessTexture", RenderTextureFormat.Rgba8Unorm),
            (8u, material, "occlusionTexture", RenderTextureFormat.Rgba8Unorm),
            (9u, material, "emissiveTexture", RenderTextureFormat.Rgba8Unorm)
        };
        var result = new List<ModelTexture>(bindings.Length);
        foreach (var (binding, owner, property, format) in bindings)
        {
            var textureIndex = owner.GetProperty(property).GetProperty("index").GetInt32();
            var imageIndex = textures[textureIndex].GetProperty("source").GetInt32();
            var imagePath = Path.Combine(directory, images[imageIndex].GetProperty("uri").GetString()!);
            result.Add(new ModelTexture(binding, format, PngImage.Load(imagePath)));
        }

        return result;
    }
}

internal readonly record struct ModelTexture(uint Binding, RenderTextureFormat Format, PngImage Image);

internal sealed class PngImage
{
    private PngImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public static PngImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (data.Length < signature.Length || !data.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new InvalidDataException("The sample texture is not a PNG.");
        }

        using var idat = new MemoryStream();
        var offset = signature.Length;
        var width = 0;
        var height = 0;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            var payload = data.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(payload);
                height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                if (payload[8] != 8 || payload[9] != 6 || payload[12] != 0)
                {
                    throw new InvalidDataException("Only non-interlaced RGBA8 PNG is supported by this sample.");
                }
            }
            else if (type == "IDAT")
            {
                idat.Write(payload);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset += checked(12 + length);
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = checked(width * 4);
        var scanlines = new byte[checked(height * (stride + 1))];
        zlib.ReadExactly(scanlines);
        var pixels = new byte[checked(width * height * 4)];
        var previous = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var scanline = scanlines.AsSpan(y * (stride + 1), stride + 1);
            var filter = scanline[0];
            var row = scanline[1..];
            for (var x = 0; x < stride; x++)
            {
                var left = x >= 4 ? row[x - 4] : (byte)0;
                var up = previous[x];
                var upLeft = x >= 4 ? previous[x - 4] : (byte)0;
                row[x] = filter switch
                {
                    0 => row[x],
                    1 => unchecked((byte)(row[x] + left)),
                    2 => unchecked((byte)(row[x] + up)),
                    3 => unchecked((byte)(row[x] + ((left + up) / 2))),
                    4 => unchecked((byte)(row[x] + Paeth(left, up, upLeft))),
                    _ => throw new InvalidDataException($"Unsupported PNG filter {filter}.")
                };
            }

            row.CopyTo(pixels.AsSpan(y * stride, stride));
            row.CopyTo(previous);
        }

        return new PngImage(width, height, pixels);
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

internal sealed class SampleOptions
{
    private SampleOptions(string modelDirectory, string shaderDirectory, string outputPath, int width, int height)
    {
        ModelDirectory = modelDirectory;
        ShaderDirectory = shaderDirectory;
        OutputPath = outputPath;
        Width = width;
        Height = height;
    }

    public string ModelDirectory { get; }
    public string ShaderDirectory { get; }
    public string OutputPath { get; }
    public int Width { get; }
    public int Height { get; }

    public static SampleOptions Parse(string[] args)
    {
        var model = "/Users/rum/GitProjects/TheFurnace/DeltaShader/samples/DeltaShader.ComputerPbrModel/assets/Sci-fi_Military_Rugged_Laptop";
        var shaders = "/Users/rum/GitProjects/TheFurnace/DeltaShader/src/DeltaShader/CompiledShaders";
        var output = "/tmp/delta-computer-pbr-model.ppm";
        var width = 1024;
        var height = 768;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--model": model = args[++index]; break;
                case "--shader-dir": shaders = args[++index]; break;
                case "--output": output = args[++index]; break;
                case "--width": width = int.Parse(args[++index], CultureInfo.InvariantCulture); break;
                case "--height": height = int.Parse(args[++index], CultureInfo.InvariantCulture); break;
            }
        }

        return new SampleOptions(model, shaders, output, width, height);
    }
}

internal static class JsonExtensions
{
    public static int GetInt32OrDefault(this JsonElement element, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Undefined ? fallback : element.GetInt32();
}
