using System.Text.Json;
using Delta.Render.Core;
using Delta.Shader.Abstractions;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextContractTests
{
    private static TextGlyphInstance Glyph(uint page, int x, int y, UiClipRect? clip = null, uint pipeline = 1) =>
        new(new TextAtlasPageId(page), new TextUvRect(0, 0, 0.1f, 0.1f), new TextPixelBounds(x, y, 10, 10),
            new TextColor(1, 1, 1, 1), clip ?? UiClipRect.Unbounded, TextRenderMode.Sdf, 4, 0.01f, pipeline);

    [Fact]
    public void EmptyDrawListHasNoBatches()
    {
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[1];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[1];
        Assert.True(TextBatching.TryBuild(ReadOnlySpan<TextGlyphInstance>.Empty, ordered, batches, out var count, out var batchCount));
        Assert.Equal(0, count);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public void BatchingPreservesMultiplePagesPipelineAndClipsWithoutPerGlyphDraws()
    {
        var clip = new UiClipRect(0, 0, 50, 50);
        var source = new[] { Glyph(1, 0, 0, clip), Glyph(2, 10, 0, clip), Glyph(1, 20, 0, clip), Glyph(1, 30, 0, clip, 2) };
        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[4];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[4];
        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var count, out var batchCount));
        Assert.Equal(4, count);
        Assert.Equal(4, batchCount);
        Assert.Equal(1, batches[0].Count);
        Assert.Equal(new TextAtlasPageId(1), batches[0].Key.AtlasPage);
        Assert.Equal(new TextAtlasPageId(2), batches[1].Key.AtlasPage);
    }

    [Fact]
    public void ClipVisibilityCoversEmptyPartialAndFullyClippedGlyphsAndResize()
    {
        var metrics = new WindowMetrics(100, 80, 1);
        Assert.False(Glyph(1, 0, 0, new UiClipRect(200, 200, 10, 10)).IsVisible(metrics));
        Assert.True(Glyph(1, 0, 0, new UiClipRect(5, 5, 3, 3)).IsVisible(metrics));
        Assert.False(Glyph(1, 90, 70).IsVisible(new WindowMetrics(50, 40, 2)));
        Assert.False(Glyph(1, 0, 0).IsVisible(new WindowMetrics(0, 0, 1)));
    }

    [Fact]
    public void TextRunAndInvalidGlyphsAreExplicit()
    {
        var run = new TextRun(new[] { Glyph(1, 0, 0) });
        Assert.False(run.IsEmpty);
        Assert.True(run.Glyphs.Span[0].IsValid);
        Assert.False(Glyph(0, 0, 0).IsValid);
        Assert.False((Glyph(1, 0, 0) with { Mode = (TextRenderMode)99 }).IsValid);
    }

    [Fact]
    public void AtlasDirtyRangeValidatesPitchAndSourceSize()
    {
        var valid = new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[4]);
        Assert.True(valid.IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 0, new byte[4]).IsValid);
        Assert.False(new TextAtlasDirtyRange(0, 0, 2, 2, 2, new byte[3]).IsValid);
    }

    [Fact]
    public void ShaderArtifactContractReportsInvalidWhenManifestIsIncomplete()
    {
        var bytes = new byte[20];
        var vertex = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = Delta.Shader.Abstractions.ShaderStage.Vertex
        });
        var fragment = new Delta.Shader.Abstractions.ShaderArtifact(bytes, new Delta.Shader.Abstractions.ShaderAbiManifest
        {
            Stage = Delta.Shader.Abstractions.ShaderStage.Fragment
        });
        var diagnostic = TextShaderArtifactContract.Validate(new GraphicsShaderProgram(vertex, fragment));
        Assert.Equal(TextShaderArtifactStatus.Invalid, diagnostic.Status);
    }

    [Fact]
    public void ShaderArtifactContractAcceptsCanonicalTextManifest()
    {
        var bytes = new byte[20];
        var vertex = new ShaderArtifact(bytes, CanonicalVertexManifest());
        var fragment = new ShaderArtifact(bytes, CanonicalFragmentManifest(3));

        var valid = TextShaderArtifactContract.TryDescribe(new GraphicsShaderProgram(vertex, fragment), out var layout, out var diagnostic);
        Assert.True(valid);
        Assert.Equal(TextShaderArtifactStatus.Ready, diagnostic.Status);
        Assert.Equal(0u, layout.VertexStorageSet);
        Assert.Equal(0u, layout.VertexStorageBinding);
        Assert.Equal(0u, layout.TextureSet);
        Assert.Equal(3u, layout.TextureBinding);
        Assert.Equal(64u, layout.PushConstantSize);

        var malformedStride = TextShaderArtifactContract.Validate(new GraphicsShaderProgram(
            new ShaderArtifact(bytes, CanonicalVertexManifest(CanonicalGlyphStorage(arrayStride: 32, packingStride: 32))), fragment));
        Assert.Equal(TextShaderArtifactStatus.Invalid, malformedStride.Status);

        var storage = CanonicalGlyphStorage();
        var malformedMembers = ReplaceMember(storage.Members, 1,
            new ShaderAbiMember { Name = "PixelMax", GlslType = "vec2", Offset = 12, Size = 8, ArrayStride = 8 });
        var malformedMember = TextShaderArtifactContract.Validate(new GraphicsShaderProgram(
            new ShaderArtifact(bytes, CanonicalVertexManifest(CopyStorage(storage, malformedMembers))), fragment));
        Assert.Equal(TextShaderArtifactStatus.Invalid, malformedMember.Status);

        var push = CanonicalPushConstants();
        var malformedPushMembers = ReplaceMember(push.Members, 3,
            new ShaderAbiMember { Name = "OutlineWidth", GlslType = "float", Offset = 48, Size = 8, ArrayStride = 8 });
        var malformedPush = CopyPush(push, malformedPushMembers);
        var malformedPushDiagnostic = TextShaderArtifactContract.Validate(new GraphicsShaderProgram(
            new ShaderArtifact(bytes, CanonicalVertexManifest(push: malformedPush)),
            new ShaderArtifact(bytes, CanonicalFragmentManifest(3, malformedPush))));
        Assert.Equal(TextShaderArtifactStatus.Invalid, malformedPushDiagnostic.Status);
    }

    [Theory]
    [InlineData("sdf", 3u)]
    [InlineData("msdf", 4u)]
    public void GeneratedDeltaShaderTextArtifactsMeetRenderContract(string mode, uint expectedBinding)
    {
        var vertexJson = mode == "sdf"
            ? Delta.Shader.Text.SdfTextGraphicsShaderProgram.VertexManifestJson
            : Delta.Shader.Text.MsdfTextGraphicsShaderProgram.VertexManifestJson;
        var fragmentJson = mode == "sdf"
            ? Delta.Shader.Text.SdfTextGraphicsShaderProgram.FragmentManifestJson
            : Delta.Shader.Text.MsdfTextGraphicsShaderProgram.FragmentManifestJson;
        var vertexManifest = JsonSerializer.Deserialize<ShaderAbiManifest>(vertexJson);
        var fragmentManifest = JsonSerializer.Deserialize<ShaderAbiManifest>(fragmentJson);
        if (vertexManifest is null || fragmentManifest is null)
        {
            throw new InvalidOperationException("Delta.Shader.Text generated manifest could not be deserialized.");
        }

        var program = new GraphicsShaderProgram(
            new ShaderArtifact(new byte[20], vertexManifest),
            new ShaderArtifact(new byte[20], fragmentManifest));
        Assert.True(TextShaderArtifactContract.TryDescribe(program, out var layout, out var diagnostic));
        Assert.Equal(TextShaderArtifactStatus.Ready, diagnostic.Status);
        Assert.Equal(0u, layout.VertexStorageBinding);
        Assert.Equal(expectedBinding, layout.TextureBinding);
        Assert.Equal(64u, layout.PushConstantSize);
    }

    private static ShaderAbiManifest CanonicalVertexManifest(ShaderAbiResource? storage = null, ShaderAbiPushConstant? push = null) => new()
    {
        Version = ShaderAbiManifest.CurrentVersion,
        Stage = ShaderStage.Vertex,
        EntryPointName = "main",
        Resources = new[] { storage ?? CanonicalGlyphStorage() },
        PushConstants = new[] { push ?? CanonicalPushConstants() }
    };

    private static ShaderAbiManifest CanonicalFragmentManifest(uint binding, ShaderAbiPushConstant? push = null) => new()
    {
        Version = ShaderAbiManifest.CurrentVersion,
        Stage = ShaderStage.Fragment,
        EntryPointName = "main",
        Resources = new[]
        {
            new ShaderAbiResource
            {
                Name = "atlas",
                Category = "sampled-texture",
                Stage = ShaderStage.Fragment,
                Set = 0,
                Binding = binding,
                Access = ShaderResourceAccess.ReadOnly,
                ReadOnly = true
            }
        },
        PushConstants = new[] { push ?? CanonicalPushConstants() }
    };

    private static ShaderAbiResource CanonicalGlyphStorage(uint arrayStride = 48, uint packingStride = 48) => new()
    {
        Name = "glyphs",
        Category = "storage-buffer",
        Stage = ShaderStage.Vertex,
        Set = 0,
        Binding = 0,
        Access = ShaderResourceAccess.ReadOnly,
        Layout = "std430",
        ReadOnly = true,
        Size = 48,
        ArrayStride = arrayStride,
        Packing = new ShaderAbiPackingPlan { Scheme = "std430", Stride = packingStride },
        Members =
        [
            new ShaderAbiMember { Name = "PixelMin", GlslType = "vec2", Offset = 0, Size = 8, ArrayStride = 8 },
            new ShaderAbiMember { Name = "PixelMax", GlslType = "vec2", Offset = 8, Size = 8, ArrayStride = 8 },
            new ShaderAbiMember { Name = "UvRect", GlslType = "vec4", Offset = 16, Size = 16, ArrayStride = 16 },
            new ShaderAbiMember { Name = "Color", GlslType = "vec4", Offset = 32, Size = 16, ArrayStride = 16 }
        ]
    };

    private static ShaderAbiPushConstant CanonicalPushConstants() => new()
    {
        Name = "TextParameters",
        GlslType = "DeltaPushConstants",
        Alignment = 16,
        Size = 64,
        ArrayStride = 64,
        Members =
        [
            new ShaderAbiMember { Name = "Resolution", GlslType = "vec2", Offset = 0, Size = 8, ArrayStride = 8 },
            new ShaderAbiMember { Name = "TextColor", GlslType = "vec4", Offset = 16, Size = 16, ArrayStride = 16 },
            new ShaderAbiMember { Name = "OutlineColor", GlslType = "vec4", Offset = 32, Size = 16, ArrayStride = 16 },
            new ShaderAbiMember { Name = "OutlineWidth", GlslType = "float", Offset = 48, Size = 4, ArrayStride = 4 }
        ]
    };

    private static ShaderAbiResource CopyStorage(ShaderAbiResource source, IReadOnlyList<ShaderAbiMember> members) => new()
    {
        Name = source.Name,
        Category = source.Category,
        Stage = source.Stage,
        Set = source.Set,
        Binding = source.Binding,
        Access = source.Access,
        Layout = source.Layout,
        ReadOnly = source.ReadOnly,
        Size = source.Size,
        ArrayStride = source.ArrayStride,
        Packing = source.Packing,
        Members = members
    };

    private static ShaderAbiPushConstant CopyPush(ShaderAbiPushConstant source, IReadOnlyList<ShaderAbiMember> members) => new()
    {
        Name = source.Name,
        ParameterName = source.ParameterName,
        GlslType = source.GlslType,
        Alignment = source.Alignment,
        Size = source.Size,
        ArrayStride = source.ArrayStride,
        Members = members
    };

    private static ShaderAbiMember[] ReplaceMember(IReadOnlyList<ShaderAbiMember> members, int index, ShaderAbiMember replacement)
    {
        var result = members.ToArray();
        result[index] = replacement;
        return result;
    }

    [Fact]
    public void Gray8AtlasFixtureExposesPageAndGlyphContracts()
    {
        var fixture = AtlasFixture.Load();

        Assert.Equal(new TextAtlasPageDescription(new TextAtlasPageId(1), 256, 256, TextAtlasFormat.R8Unorm), fixture.Description);
        Assert.Equal(3, fixture.Summary.Glyphs.Length);
        Assert.All(fixture.Summary.Glyphs, glyph =>
        {
            Assert.Equal(0, glyph.PageIndex);
            Assert.True(glyph.U1 > glyph.U0);
            Assert.True(glyph.V1 > glyph.V0);
            Assert.True(glyph.Width > 0);
            Assert.True(glyph.Height > 0);
        });
        Assert.Contains(fixture.Pixels, pixel => pixel != 0);
    }

    [Fact]
    public void TextBatchingPreservesGroupedInstanceOrderAndBatchCounts()
    {
        var clipA = new UiClipRect(0, 0, 32, 32);
        var clipB = new UiClipRect(8, 8, 16, 16);
        var source = new[]
        {
            new TextGlyphInstance(new TextAtlasPageId(1), new TextUvRect(0, 0, 0.2f, 0.2f), new TextPixelBounds(0, 0, 10, 10), new TextColor(1, 1, 1, 1), clipA, TextRenderMode.Sdf, 4, 0.01f, 7),
            new TextGlyphInstance(new TextAtlasPageId(1), new TextUvRect(0.2f, 0, 0.2f, 0.2f), new TextPixelBounds(10, 0, 10, 10), new TextColor(1, 1, 1, 1), clipA, TextRenderMode.Sdf, 4, 0.01f, 7),
            new TextGlyphInstance(new TextAtlasPageId(2), new TextUvRect(0, 0, 0.2f, 0.2f), new TextPixelBounds(0, 12, 10, 10), new TextColor(1, 1, 1, 1), clipB, TextRenderMode.Sdf, 4, 0.01f, 7)
        };

        Span<TextGlyphInstance> ordered = stackalloc TextGlyphInstance[source.Length];
        Span<TextBatchRange> batches = stackalloc TextBatchRange[source.Length];

        Assert.True(TextBatching.TryBuild(source, ordered, batches, out var orderedCount, out var batchCount));
        Assert.Equal(3, orderedCount);
        Assert.Equal(2, batchCount);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(1, batches[1].Count);
        Assert.Equal(source[0].BatchKey, batches[0].Key);
        Assert.Equal(source[2].BatchKey, batches[1].Key);
    }
}
