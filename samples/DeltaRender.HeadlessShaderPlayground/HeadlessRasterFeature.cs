using System.Buffers.Binary;
using Delta.Render;
using Delta.Render.RenderGraph;
using Delta.Shader.Contract;

namespace Delta.Render.HeadlessShaderPlayground;

internal sealed class HeadlessRasterFeature : IRenderFeature
{
    private readonly IGraphicsShaderProgram _program;
    private readonly RenderTargetHandle _target;
    private readonly uint _width;
    private readonly uint _height;
    private readonly HeadlessRasterPass _pass;

    internal HeadlessRasterFeature(IGraphicsShaderProgram program, RenderTargetHandle target, uint width, uint height, uint vertexCount, float time)
    {
        _program = program;
        _target = target;
        _width = width;
        _height = height;
        Time = time;
        var pushConstants = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(pushConstants.AsSpan(0, 4), width);
        BinaryPrimitives.WriteSingleLittleEndian(pushConstants.AsSpan(4, 4), height);
        BinaryPrimitives.WriteSingleLittleEndian(pushConstants.AsSpan(8, 4), time);
        _pass = new HeadlessRasterPass(width, height, vertexCount, pushConstants);
    }

    internal float Time { get; }
    internal RenderGraphReadbackHandle Readback { get; private set; }

    public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
    {
        var target = graph.ImportTarget(_target);
        var pass = graph.AddRasterPass(new RasterPassDescription("headless-shader-playground", new RasterPipelineDescription(_program, cullMode: RasterCullMode.None)), _pass);
        graph.UseColorAttachment(pass, 0, new ColorAttachmentDescription(target, AttachmentLoadOperation.Clear, AttachmentStoreOperation.Store, new ClearColor(0.04f, 0.05f, 0.08f, 1f)));
        Readback = graph.ReadbackTexture(target, new PixelRect(0, 0, checked((int)_width), checked((int)_height)));
    }
}
