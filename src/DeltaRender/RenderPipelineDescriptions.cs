using Delta.Shader.Contract;

namespace Delta.Render.RenderGraph;

public enum PrimitiveTopology : byte
{
    TriangleList,
    TriangleStrip,
    LineList,
    PointList,
}

public enum RasterCullMode : byte
{
    None,
    Front,
    Back,
}

public enum RasterFrontFace : byte
{
    CounterClockwise,
    Clockwise,
}

public enum RenderBlendMode : byte
{
    Opaque,
    Alpha,
    PremultipliedAlpha,
    Additive,
}

public sealed record RasterPipelineDescription
{
    public RasterPipelineDescription(
        IGraphicsShaderProgram shaderProgram,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        RasterCullMode cullMode = RasterCullMode.Back,
        RasterFrontFace frontFace = RasterFrontFace.CounterClockwise,
        RenderBlendMode blendMode = RenderBlendMode.Opaque,
        bool depthTest = false,
        bool depthWrite = false)
    {
        ArgumentNullException.ThrowIfNull(shaderProgram);
        ShaderProgram = shaderProgram;
        Topology = topology;
        CullMode = cullMode;
        FrontFace = frontFace;
        BlendMode = blendMode;
        DepthTest = depthTest;
        DepthWrite = depthWrite;
    }

    public IGraphicsShaderProgram ShaderProgram { get; }

    public PrimitiveTopology Topology { get; }

    public RasterCullMode CullMode { get; }

    public RasterFrontFace FrontFace { get; }

    public RenderBlendMode BlendMode { get; }

    public bool DepthTest { get; }

    public bool DepthWrite { get; }
}

public sealed record RasterPassDescription
{
    public RasterPassDescription(string name, RasterPipelineDescription pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pipeline);
        Name = name;
        Pipeline = pipeline;
    }

    public string Name { get; }

    public RasterPipelineDescription Pipeline { get; }
}

public sealed record ComputePassDescription
{
    public ComputePassDescription(string name, IShaderArtifact shader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shader);
        if (shader.Stage != ShaderStage.Compute)
        {
            throw new ArgumentException("A compute pass requires a compute shader artifact.", nameof(shader));
        }

        Name = name;
        Shader = shader;
    }

    public string Name { get; }

    public IShaderArtifact Shader { get; }
}
