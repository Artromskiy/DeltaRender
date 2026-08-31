using Delta.Render;
using Delta.Render.RenderGraph;

namespace Delta.Render.HeadlessShaderPlayground;

internal sealed class HeadlessRasterPass : IRasterPass
{
    private readonly RenderViewport _viewport;
    private readonly PixelRect _scissor;
    private readonly uint _vertexCount;
    private readonly byte[] _pushConstants;

    internal HeadlessRasterPass(uint width, uint height, uint vertexCount, byte[] pushConstants)
    {
        _viewport = new RenderViewport(0, 0, width, height);
        _scissor = new PixelRect(0, 0, checked((int)width), checked((int)height));
        _vertexCount = vertexCount;
        _pushConstants = pushConstants;
    }

    public void Record(IRasterCommandContext commands)
    {
        commands.SetViewport(in _viewport);
        commands.SetScissor(in _scissor);
        commands.PushConstants(_pushConstants);
        commands.Draw(_vertexCount);
    }
}
