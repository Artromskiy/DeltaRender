using Delta.Render.Core;
using Delta.Shader.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextContractTests
{
    [Fact]
    public void TextShaderArtifactContractAcceptsCanonicalProgram()
    {
        var program = new GraphicsShaderProgram(
            Artifact(ShaderStage.Vertex, VertexAbi()),
            Artifact(ShaderStage.Fragment, FragmentAbi(3)));

        var valid = TextShaderArtifactContract.TryDescribe(program, out var layout, out var diagnostic);

        Assert.True(valid);
        Assert.Equal(TextShaderArtifactStatus.Ready, diagnostic.Status);
        Assert.Equal(0u, layout.VertexStorageSet);
        Assert.Equal(0u, layout.VertexStorageBinding);
        Assert.Equal(0u, layout.TextureSet);
        Assert.Equal(3u, layout.TextureBinding);
        Assert.Equal(64u, layout.PushConstantSize);
    }

    [Fact]
    public void TextShaderArtifactContractRejectsMalformedStride()
    {
        var malformed = new ShaderAbiLayout(
            48,
            16,
            arrayStride: 32,
            members: GlyphMembers());
        var program = new GraphicsShaderProgram(
            Artifact(ShaderStage.Vertex, VertexAbi(malformed)),
            Artifact(ShaderStage.Fragment, FragmentAbi(3)));

        Assert.Equal(TextShaderArtifactStatus.Invalid, TextShaderArtifactContract.Validate(program).Status);
    }

    [Fact]
    public void TextShaderArtifactContractRejectsMalformedMemberOffset()
    {
        var members = GlyphMembers();
        members[1] = new ShaderAbiMember(
            new ShaderValueType(ShaderValueKind.FloatingPoint, 32, 2),
            12,
            8,
            8,
            arrayStride: 8);
        var program = new GraphicsShaderProgram(
            Artifact(ShaderStage.Vertex, VertexAbi(new ShaderAbiLayout(48, 16, arrayStride: 48, members: members))),
            Artifact(ShaderStage.Fragment, FragmentAbi(3)));

        Assert.Equal(TextShaderArtifactStatus.Invalid, TextShaderArtifactContract.Validate(program).Status);
    }

    [Fact]
    public void TextShaderArtifactContractRejectsMalformedPushMember()
    {
        var members = PushMembers();
        members[3] = new ShaderAbiMember(
            new ShaderValueType(ShaderValueKind.FloatingPoint, 32),
            48,
            8,
            4);
        var push = new ShaderAbiLayout(64, 16, members: members);
        var program = new GraphicsShaderProgram(
            Artifact(ShaderStage.Vertex, VertexAbi(pushConstants: push)),
            Artifact(ShaderStage.Fragment, FragmentAbi(3, push)));

        Assert.Equal(TextShaderArtifactStatus.Invalid, TextShaderArtifactContract.Validate(program).Status);
    }

    [Theory]
    [InlineData("sdf", 3u)]
    [InlineData("msdf", 4u)]
    public void GeneratedDeltaShaderTextArtifactsMeetRenderContract(string mode, uint expectedBinding)
    {
        var program = mode == "sdf"
            ? Delta.Shader.Text.SdfTextGraphicsShaderProgram.CreateProgram(MagicSpirv(), MagicSpirv())
            : Delta.Shader.Text.MsdfTextGraphicsShaderProgram.CreateProgram(MagicSpirv(), MagicSpirv());

        Assert.True(TextShaderArtifactContract.TryDescribe(program, out var layout, out var diagnostic));
        Assert.Equal(TextShaderArtifactStatus.Ready, diagnostic.Status);
        Assert.Equal(0u, layout.VertexStorageBinding);
        Assert.Equal(expectedBinding, layout.TextureBinding);
        Assert.Equal(64u, layout.PushConstantSize);
    }

    private static ShaderArtifact Artifact(ShaderStage stage, ShaderAbi abi)
        => new(MagicSpirv(), "main", abi);

    private static ShaderAbi VertexAbi(
        ShaderAbiLayout? storage = null,
        ShaderAbiLayout? pushConstants = null)
        => new(
            ShaderStage.Vertex,
            resources:
            [
                new ShaderResourceBinding(
                    new ShaderBinding(0, 0),
                    ShaderResourceKind.StorageBuffer,
                    ShaderResourceAccess.Read,
                    ShaderStageMask.Vertex,
                    storage ?? GlyphLayout())
            ],
            pushConstants: [Push(ShaderStageMask.Vertex, pushConstants ?? PushLayout())]);

    private static ShaderAbi FragmentAbi(uint binding, ShaderAbiLayout? pushConstants = null)
        => new(
            ShaderStage.Fragment,
            resources:
            [
                new ShaderResourceBinding(
                    new ShaderBinding(0, binding),
                    ShaderResourceKind.SampledTexture,
                    ShaderResourceAccess.Read,
                    ShaderStageMask.Fragment)
            ],
            pushConstants: [Push(ShaderStageMask.Fragment, pushConstants ?? PushLayout())]);

    private static ShaderPushConstantRange Push(ShaderStageMask stage, ShaderAbiLayout layout)
        => new(0, 64, stage, layout);

    private static ShaderAbiLayout GlyphLayout()
        => new(48, 16, arrayStride: 48, members: GlyphMembers());

    private static ShaderAbiMember[] GlyphMembers()
        =>
        [
            Member(0, 8, 8, 2),
            Member(8, 8, 8, 2),
            Member(16, 16, 16, 4),
            Member(32, 16, 16, 4)
        ];

    private static ShaderAbiLayout PushLayout() => new(64, 16, members: PushMembers());

    private static ShaderAbiMember[] PushMembers()
        =>
        [
            Member(0, 8, 8, 2),
            Member(16, 16, 16, 4),
            Member(32, 16, 16, 4),
            Member(48, 4, 4, 1)
        ];

    private static ShaderAbiMember Member(uint offset, uint size, uint alignment, uint vectorSize)
        => new(
            new ShaderValueType(ShaderValueKind.FloatingPoint, 32, vectorSize),
            offset,
            size,
            alignment,
            arrayStride: size);

    private static byte[] MagicSpirv() => [0x03, 0x02, 0x23, 0x07];
}
