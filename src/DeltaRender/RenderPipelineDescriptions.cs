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
    Multiply,
}

public enum RenderBlendFactor : byte
{
    Zero,
    One,
    SrcColor,
    OneMinusSrcColor,
    DstColor,
    OneMinusDstColor,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstAlpha,
    OneMinusDstAlpha,
}

public enum RenderBlendOperation : byte
{
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max,
}

public readonly record struct RenderBlendState(
    bool Enabled,
    RenderBlendFactor SourceColorFactor,
    RenderBlendFactor DestinationColorFactor,
    RenderBlendOperation ColorOperation,
    RenderBlendFactor SourceAlphaFactor,
    RenderBlendFactor DestinationAlphaFactor,
    RenderBlendOperation AlphaOperation)
{
    public static RenderBlendState PremultipliedAdditive => new(
        true,
        RenderBlendFactor.One,
        RenderBlendFactor.One,
        RenderBlendOperation.Add,
        RenderBlendFactor.One,
        RenderBlendFactor.OneMinusSrcAlpha,
        RenderBlendOperation.Add);

    public static RenderBlendState FromMode(RenderBlendMode blendMode) => blendMode switch
    {
        RenderBlendMode.Opaque => new(
            false,
            RenderBlendFactor.One,
            RenderBlendFactor.Zero,
            RenderBlendOperation.Add,
            RenderBlendFactor.One,
            RenderBlendFactor.Zero,
            RenderBlendOperation.Add),
        RenderBlendMode.Alpha => new(
            true,
            RenderBlendFactor.SrcAlpha,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add,
            RenderBlendFactor.One,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add),
        RenderBlendMode.PremultipliedAlpha => new(
            true,
            RenderBlendFactor.One,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add,
            RenderBlendFactor.One,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add),
        RenderBlendMode.Additive => new(
            true,
            RenderBlendFactor.SrcAlpha,
            RenderBlendFactor.One,
            RenderBlendOperation.Add,
            RenderBlendFactor.One,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add),
        RenderBlendMode.Multiply => new(
            true,
            RenderBlendFactor.DstColor,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add,
            RenderBlendFactor.One,
            RenderBlendFactor.OneMinusSrcAlpha,
            RenderBlendOperation.Add),
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode), blendMode, "Unsupported render blend mode."),
    };
}

public enum RenderCompareOperation : byte
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

public enum RenderStencilOperation : byte
{
    Keep,
    Zero,
    Replace,
    IncrementClamp,
    DecrementClamp,
    Invert,
    IncrementWrap,
    DecrementWrap,
}

public readonly record struct RenderStencilFaceState(
    RenderCompareOperation CompareOperation = RenderCompareOperation.Always,
    RenderStencilOperation FailOperation = RenderStencilOperation.Keep,
    RenderStencilOperation DepthFailOperation = RenderStencilOperation.Keep,
    RenderStencilOperation PassOperation = RenderStencilOperation.Keep,
    uint CompareMask = uint.MaxValue,
    uint WriteMask = uint.MaxValue,
    uint Reference = 0);

public readonly record struct RenderStencilState(
    bool Enabled = false,
    RenderStencilFaceState Front = default,
    RenderStencilFaceState Back = default);

public sealed record RasterPipelineDescription
{
    public RasterPipelineDescription(
        IGraphicsShaderProgram shaderProgram,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        RasterCullMode cullMode = RasterCullMode.Back,
        RasterFrontFace frontFace = RasterFrontFace.CounterClockwise,
        RenderBlendMode blendMode = RenderBlendMode.Opaque,
        bool depthTest = false,
        bool depthWrite = false,
        RenderCompareOperation depthCompareOperation = RenderCompareOperation.LessOrEqual,
        RenderStencilState stencilState = default,
        RenderBlendState? blendState = null)
    {
        ArgumentNullException.ThrowIfNull(shaderProgram);
        ShaderProgram = shaderProgram;
        Topology = topology;
        CullMode = cullMode;
        FrontFace = frontFace;
        BlendMode = blendMode;
        DepthTest = depthTest;
        DepthWrite = depthWrite;
        DepthCompareOperation = depthCompareOperation;
        StencilState = stencilState;
        BlendState = blendState ?? RenderBlendState.FromMode(blendMode);
    }

    public IGraphicsShaderProgram ShaderProgram { get; }

    public PrimitiveTopology Topology { get; }

    public RasterCullMode CullMode { get; }

    public RasterFrontFace FrontFace { get; }

    public RenderBlendMode BlendMode { get; }

    public RenderBlendState BlendState { get; }

    public bool DepthTest { get; }

    public bool DepthWrite { get; }

    public RenderCompareOperation DepthCompareOperation { get; }

    public RenderStencilState StencilState { get; }
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
