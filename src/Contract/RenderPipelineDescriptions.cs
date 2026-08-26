using Delta.Shader.Contract;

namespace Delta.Render.Core.RenderGraph;

public enum PrimitiveTopology
{
    TriangleList,
    TriangleStrip,
    LineList,
    PointList,
}

public enum RasterCullMode
{
    None,
    Front,
    Back,
}

public enum RasterFrontFace
{
    CounterClockwise,
    Clockwise,
}

public enum RenderBlendMode
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

public sealed record ComputePipelineDescription
{
    public ComputePipelineDescription(IShaderArtifact shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (shader.Stage != ShaderStage.Compute)
        {
            throw new ArgumentException("A compute pipeline requires a compute shader artifact.", nameof(shader));
        }

        Shader = shader;
    }

    public IShaderArtifact Shader { get; }
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
    public ComputePassDescription(string name, ComputePipelineDescription pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pipeline);
        Name = name;
        Pipeline = pipeline;
    }

    public string Name { get; }

    public ComputePipelineDescription Pipeline { get; }
}

public sealed record TransferPassDescription
{
    public TransferPassDescription(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
