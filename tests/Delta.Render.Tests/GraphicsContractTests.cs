using Delta.Render.Core;
using Delta.Shader.Abstractions;
using Xunit;
using CoreGraphicsShaderProgram = Delta.Render.Core.GraphicsShaderProgram;

namespace Delta.Render.Tests;

public sealed class GraphicsContractTests
{
    [Fact]
    public void GraphicsProgramRequiresPairedVertexAndFragmentStages()
    {
        var bytes = new byte[4];
        var program = new CoreGraphicsShaderProgram(
            Artifact(bytes, ShaderStage.Vertex),
            Artifact(bytes, ShaderStage.Fragment));

        Assert.Equal(ShaderStage.Vertex, program.Vertex.Stage);
        Assert.Equal(ShaderStage.Fragment, program.Fragment.Stage);
        Assert.Equal("main", program.Vertex.EntryPoint);

        Assert.Throws<ArgumentException>(() => new CoreGraphicsShaderProgram(
            Artifact(bytes, ShaderStage.Fragment),
            Artifact(bytes, ShaderStage.Vertex)));
    }

    [Fact]
    public void GraphicsFrameParametersValidateResolutionAndTime()
    {
        Assert.True(new GraphicsFrameParameters(960, 540, 1.25f).IsValid);
        Assert.False(new GraphicsFrameParameters(0, 540, 1.25f).IsValid);
        Assert.False(new GraphicsFrameParameters(960, float.NaN, 1.25f).IsValid);
    }

    [Fact]
    public void UiQuadIsAValueOnlyConsumerOwnedDrawRecord()
    {
        Assert.True(new UiQuad(1, 2, 3, 4, 1, 0.5f, 0.25f, 1).IsValid);
        Assert.False(new UiQuad(1, 2, 0, 4, 1, 0.5f, 0.25f, 1).IsValid);
        Assert.True(new UiQuad(1, 2, 3, 4, 1, 0.5f, 0.25f, 1) { Clip = new UiClipRect(0, 0, 2, 2) }.Clip.IsValid);
        Assert.False(new UiClipRect(0, 0, 0, 2).IsValid);
    }

    [Fact]
    public void UiDrawListPreservesConsumerSpanWithoutCopying()
    {
        Span<UiQuad> quads = stackalloc UiQuad[2];
        quads[0] = new UiQuad(0, 0, 10, 10, 1, 0, 0, 1);
        quads[1] = new UiQuad(10, 10, 20, 20, 0, 1, 0, 1);
        var drawList = new UiDrawList(quads);

        Assert.Equal(2, drawList.Count);
        Assert.False(drawList.IsEmpty);
        Assert.Equal(quads[1], drawList.Quads[1]);
    }

    [Fact]
    public void UiScissorContractCoversEmptyOneMultiAndClippedBatches()
    {
        var metrics = new WindowMetrics(100, 80, 1);
        var empty = new UiDrawList(ReadOnlySpan<UiQuad>.Empty);
        var one = new UiDrawList(stackalloc[] { new UiQuad(1, 2, 3, 4, 1, 1, 1, 1) });
        var multi = new UiDrawList(stackalloc[]
        {
            new UiQuad(0, 0, 10, 10, 1, 0, 0, 1),
            new UiQuad(10, 10, 20, 20, 0, 1, 0, 1)
        });
        var fullyClipped = new UiQuad(0, 0, 10, 10, 1, 1, 1, 1) { Clip = new UiClipRect(120, 0, 10, 10) };
        var partiallyClipped = new UiQuad(0, 0, 10, 10, 1, 1, 1, 1) { Clip = new UiClipRect(90, 70, 20, 20) };

        Assert.True(empty.IsEmpty);
        Assert.Equal(1, one.Count);
        Assert.Equal(2, multi.Count);
        Assert.False(fullyClipped.Clip.TryGetScissor(metrics, out _));
        Assert.True(partiallyClipped.Clip.TryGetScissor(metrics, out var scissor));
        Assert.Equal(new UiScissorRect(90, 70, 10, 10), scissor);
    }

    [Fact]
    public void UiScissorContractIsResizeSafeAndClampsToExtent()
    {
        var clip = new UiClipRect(40, 20, 40, 40);

        Assert.Equal(new UiScissorRect(40, 20, 40, 40), GetScissor(clip, new WindowMetrics(100, 80, 1)));
        Assert.Equal(new UiScissorRect(40, 20, 24, 28), GetScissor(clip, new WindowMetrics(64, 48, 1)));
    }

    private static UiScissorRect GetScissor(UiClipRect clip, WindowMetrics metrics)
    {
        Assert.True(clip.TryGetScissor(metrics, out var scissor));
        return scissor;
    }

    [Fact]
    public void FrameSessionExposesGraphicsWithoutEventPumpOwnership()
    {
        var members = typeof(IRenderWindowFrameSession).GetMethods()
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IRenderWindowFrameSession.CreateGraphicsPipeline), members);
        Assert.Contains(nameof(IRenderWindowFrameSession.DrawFullscreenTriangle), members);
        Assert.Contains(nameof(IRenderWindowFrameSession.EndFrame), members);
        Assert.Contains(nameof(IRenderWindowFrameSession.SubmitFrame), members);
        Assert.DoesNotContain("PollEvents", members);
        Assert.NotNull(typeof(IUiDrawListProvider).GetProperty(nameof(IUiDrawListProvider.CurrentDrawList)));
    }

    private static ShaderArtifact Artifact(byte[] spirv, ShaderStage stage)
        => new(spirv, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = stage,
            EntryPointName = "main"
        });
}
