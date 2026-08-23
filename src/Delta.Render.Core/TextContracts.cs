using Delta.Shader.Abstractions;

namespace Delta.Render.Core;

public readonly record struct TextAtlasPageId(uint Value)
{
    public bool IsValid => Value != 0;
}

public enum TextAtlasFormat : byte
{
    R8Unorm = 0,
    Rgba8Unorm = 1
}

public enum TextRenderMode : byte
{
    Sdf = 0,
    Msdf = 1
}

public readonly record struct TextAtlasPageDescription(TextAtlasPageId Id, uint Width, uint Height, TextAtlasFormat Format)
{
    public uint BytesPerPixel => Format == TextAtlasFormat.Rgba8Unorm ? 4u : 1u;
    public bool IsValid => Id.IsValid && Width > 0 && Height > 0 && Width <= 16384 && Height <= 16384 &&
                           Format is TextAtlasFormat.R8Unorm or TextAtlasFormat.Rgba8Unorm;
    public ulong RequiredBytes => checked((ulong)Width * Height * BytesPerPixel);
}

public readonly record struct TextAtlasDirtyRange(uint X, uint Y, uint Width, uint Height, uint SourceRowPitch, ReadOnlyMemory<byte> Source)
{
    public bool IsValid => Width > 0 && Height > 0 && SourceRowPitch > 0 &&
                           Source.Length >= checked((int)((ulong)SourceRowPitch * Height));
}

public readonly record struct TextUvRect(float U, float V, float Width, float Height)
{
    public bool IsValid => Width > 0 && Height > 0 && float.IsFinite(U) && float.IsFinite(V) &&
                           float.IsFinite(Width) && float.IsFinite(Height);
}

public readonly record struct TextPixelBounds(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct TextColor(float Red, float Green, float Blue, float Alpha)
{
    public bool IsValid => float.IsFinite(Red) && float.IsFinite(Green) && float.IsFinite(Blue) && float.IsFinite(Alpha);
}

public readonly record struct TextGlyphInstance(
    TextAtlasPageId AtlasPage,
    TextUvRect Uv,
    TextPixelBounds PixelBounds,
    TextColor Color,
    UiClipRect Clip,
    TextRenderMode Mode,
    float PxRange,
    float Smoothing,
    uint PipelineId = 0)
{
    public bool IsValid => AtlasPage.IsValid && Uv.IsValid && PixelBounds.IsValid && Color.IsValid && Clip.IsValid &&
                           Mode is TextRenderMode.Sdf or TextRenderMode.Msdf && float.IsFinite(PxRange) && PxRange > 0 &&
                           float.IsFinite(Smoothing) && Smoothing >= 0;

    public TextBatchKey BatchKey => new(PipelineId, AtlasPage, Clip, Mode);

    public bool IsVisible(WindowMetrics metrics)
    {
        if (!IsValid || !Clip.TryGetScissor(metrics, out var scissor))
        {
            return false;
        }

        var right = (long)PixelBounds.X + PixelBounds.Width;
        var bottom = (long)PixelBounds.Y + PixelBounds.Height;
        return right > scissor.X && bottom > scissor.Y && (long)PixelBounds.X < scissor.X + (long)scissor.Width &&
               (long)PixelBounds.Y < scissor.Y + (long)scissor.Height;
    }
}

public readonly record struct TextFrameParameters(
    float ResolutionX,
    float ResolutionY,
    float TimeSeconds,
    TextColor TextColor,
    TextColor OutlineColor,
    float OutlineWidth)
{
    public bool IsValid => ResolutionX > 0 && ResolutionY > 0 &&
                           float.IsFinite(ResolutionX) && float.IsFinite(ResolutionY) &&
                           float.IsFinite(TimeSeconds) &&
                           TextColor.IsValid &&
                           OutlineColor.IsValid &&
                           float.IsFinite(OutlineWidth) && OutlineWidth >= 0;
}

public readonly struct TextRun
{
    public TextRun(ReadOnlyMemory<TextGlyphInstance> glyphs) => Glyphs = glyphs;
    public ReadOnlyMemory<TextGlyphInstance> Glyphs { get; }
    public bool IsEmpty => Glyphs.IsEmpty;
}

public enum TextSubmissionOwnerKind : byte
{
    Entity = 0,
    XamlElement = 1
}

public readonly record struct TextSubmissionHandle(TextSubmissionOwnerKind Kind, ulong Value, uint Generation)
{
    public bool IsValid => Value != 0 && Generation != 0 && Kind is TextSubmissionOwnerKind.Entity or TextSubmissionOwnerKind.XamlElement;
}

public readonly record struct TextScreenAnchor(float X, float Y)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y);
}

public readonly record struct TextWorldAnchor(float X, float Y, float Z)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

public readonly record struct TextProjectionContext(uint Width, uint Height, float DpiScale)
{
    public bool IsValid => Width > 0 && Height > 0 && float.IsFinite(DpiScale) && DpiScale > 0;
}

public interface ITextWorldProjection
{
    bool TryProject(in TextWorldAnchor anchor, in TextProjectionContext context, out TextScreenAnchor screen);
}

public readonly record struct TextAnchor
{
    private TextAnchor(TextScreenAnchor screen, TextWorldAnchor world, bool isWorld)
    {
        Screen = screen;
        World = world;
        IsWorld = isWorld;
    }

    public TextScreenAnchor Screen { get; }
    public TextWorldAnchor World { get; }
    public bool IsWorld { get; }
    public bool IsValid => IsWorld ? World.IsValid : Screen.IsValid;
    public static TextAnchor ScreenPixels(TextScreenAnchor value) => new(value, default, false);
    public static TextAnchor WorldSpace(TextWorldAnchor value) => new(default, value, true);
}

public readonly record struct TextSubmissionRecord(
    TextSubmissionHandle Owner,
    TextAnchor Anchor,
    TextRun Glyphs,
    UiClipRect Clip,
    uint DirtyGeneration,
    int Order)
{
    public uint Version => DirtyGeneration;

    public bool IsValid => Owner.IsValid && Anchor.IsValid && !Glyphs.IsEmpty && Clip.IsValid && DirtyGeneration != 0;

    public bool Matches(in TextSubmissionHandle currentOwner, uint currentVersion)
        => Owner == currentOwner && DirtyGeneration == currentVersion;
}

public enum TextSubmissionChangeKind : byte
{
    Upserted = 0,
    Removed = 1
}

public readonly record struct TextSubmissionChange(
    TextSubmissionChangeKind Kind,
    TextSubmissionHandle Owner,
    uint Version,
    TextSubmissionRecord Record)
{
    public bool IsValid => Owner.IsValid && Version != 0 &&
                           (Kind == TextSubmissionChangeKind.Removed ||
                            Kind == TextSubmissionChangeKind.Upserted && Record.Matches(Owner, Version));
}

public readonly ref struct TextSubmissionFrame
{
    public TextSubmissionFrame(ReadOnlySpan<TextSubmissionRecord> records) => Records = records;
    public ReadOnlySpan<TextSubmissionRecord> Records { get; }
    public int Count => Records.Length;
    public bool IsEmpty => Records.IsEmpty;
}

public static class TextSubmissionBatching
{
    // This is the neutral glue boundary for Entity and XAML producers. The
    // producer owns runs and generation tokens; Render only resolves anchors,
    // intersects clips, and feeds its existing allocation-free TextBatching path.
    public static bool TryBuild(
        in TextSubmissionFrame frame,
        in TextProjectionContext projectionContext,
        ITextWorldProjection? worldProjection,
        Span<TextGlyphInstance> ordered,
        Span<TextBatchRange> batches,
        out int orderedCount,
        out int batchCount,
        out int rejectedCount)
    {
        orderedCount = 0;
        batchCount = 0;
        rejectedCount = 0;
        if (!projectionContext.IsValid || ordered.Length < CountGlyphs(frame.Records) || batches.Length < ordered.Length)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < frame.Records.Length; recordIndex++)
        {
            var record = frame.Records[recordIndex];
            if (!record.IsValid || !TryResolveAnchor(record.Anchor, projectionContext, worldProjection, out var anchor))
            {
                rejectedCount++;
                continue;
            }

            var glyphs = record.Glyphs.Glyphs.Span;
            for (var glyphIndex = 0; glyphIndex < glyphs.Length; glyphIndex++)
            {
                var glyph = glyphs[glyphIndex];
                if (!glyph.IsValid)
                {
                    rejectedCount++;
                    continue;
                }

                var bounds = glyph.PixelBounds with
                {
                    X = checked(glyph.PixelBounds.X + (int)MathF.Round(anchor.X)),
                    Y = checked(glyph.PixelBounds.Y + (int)MathF.Round(anchor.Y))
                };
                var clip = IntersectClip(glyph.Clip, record.Clip);
                var adjusted = glyph with { PixelBounds = bounds, Clip = clip };
                if (!adjusted.IsValid)
                {
                    rejectedCount++;
                    continue;
                }

                if (batchCount > 0 && batches[batchCount - 1].Key == adjusted.BatchKey &&
                    batches[batchCount - 1].End == orderedCount)
                {
                    ordered[orderedCount++] = adjusted;
                    var previous = batches[batchCount - 1];
                    batches[batchCount - 1] = previous with { Count = previous.Count + 1 };
                }
                else
                {
                    ordered[orderedCount] = adjusted;
                    batches[batchCount++] = new TextBatchRange(adjusted.BatchKey, orderedCount, 1);
                    orderedCount++;
                }
            }
        }

        return true;
    }

    private static bool TryResolveAnchor(in TextAnchor anchor, in TextProjectionContext context,
        ITextWorldProjection? worldProjection, out TextScreenAnchor screen)
    {
        if (!anchor.IsValid)
        {
            screen = default;
            return false;
        }

        if (!anchor.IsWorld)
        {
            screen = anchor.Screen;
            return true;
        }

        screen = default;
        return worldProjection is not null && worldProjection.TryProject(anchor.World, context, out screen) && screen.IsValid;
    }

    private static int CountGlyphs(ReadOnlySpan<TextSubmissionRecord> records)
    {
        var count = 0;
        for (var i = 0; i < records.Length; i++)
        {
            count = checked(count + records[i].Glyphs.Glyphs.Length);
        }

        return count;
    }

    private static UiClipRect IntersectClip(UiClipRect left, UiClipRect right)
    {
        if (left.IsUnbounded)
        {
            return right;
        }

        if (right.IsUnbounded)
        {
            return left;
        }

        var x = MathF.Max(left.X, right.X);
        var y = MathF.Max(left.Y, right.Y);
        var rightEdge = MathF.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = MathF.Min(left.Y + left.Height, right.Y + right.Height);
        return new UiClipRect(x, y, rightEdge - x, bottomEdge - y);
    }
}

public readonly ref struct TextDrawList
{
    public TextDrawList(ReadOnlySpan<TextGlyphInstance> glyphs) => Glyphs = glyphs;
    public TextDrawList(ReadOnlyMemory<TextGlyphInstance> glyphs) => Glyphs = glyphs.Span;
    public ReadOnlySpan<TextGlyphInstance> Glyphs { get; }
    public int Count => Glyphs.Length;
    public bool IsEmpty => Glyphs.IsEmpty;
}

public readonly record struct TextBatchKey(uint PipelineId, TextAtlasPageId AtlasPage, UiClipRect Clip, TextRenderMode Mode);

public readonly record struct TextBatchRange(TextBatchKey Key, int Start, int Count)
{
    public int End => checked(Start + Count);
}

public static class TextBatching
{
    // The caller owns reusable ordered/batch storage. No sort or allocation is
    // performed per glyph in the frame hot path.
    public static bool TryBuild(ReadOnlySpan<TextGlyphInstance> source, Span<TextGlyphInstance> ordered,
        Span<TextBatchRange> batches, out int orderedCount, out int batchCount)
    {
        orderedCount = 0;
        batchCount = 0;
        if (ordered.Length < source.Length || batches.Length < source.Length)
        {
            return false;
        }

        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            var glyph = source[sourceIndex];
            var key = glyph.BatchKey;
            if (batchCount > 0 && batches[batchCount - 1].Key == key && batches[batchCount - 1].End == orderedCount)
            {
                ordered[orderedCount++] = glyph;
                var previous = batches[batchCount - 1];
                batches[batchCount - 1] = previous with { Count = previous.Count + 1 };
            }
            else
            {
                ordered[orderedCount] = glyph;
                batches[batchCount++] = new TextBatchRange(key, orderedCount, 1);
                orderedCount++;
            }
        }
        return true;
    }
}

public interface ITextAtlasPage : IAsyncDisposable
{
    TextAtlasPageDescription Description { get; }
    bool IsDisposed { get; }
}

public readonly record struct TextAtlasUploadStatistics(int StagingAllocationCount, int SubmissionCount, ulong StagingCapacity);

public interface ITextAtlasDevice
{
    TextAtlasUploadStatistics AtlasUploadStatistics { get; }
    ITextAtlasPage CreateAtlasPage(in TextAtlasPageDescription description);
    bool UploadAtlasPage(ITextAtlasPage page, ReadOnlySpan<byte> pixels, uint sourceRowPitch);
    bool UploadAtlasDirtyRanges(ITextAtlasPage page, ReadOnlySpan<TextAtlasDirtyRange> ranges);
}

public enum TextShaderArtifactStatus : byte
{
    Ready = 0,
    Invalid = 1,
    SampledImageAbiUnavailable = 2
}

public readonly record struct TextShaderArtifactDiagnostic(TextShaderArtifactStatus Status, string Message);

public readonly record struct TextGraphicsShaderLayout(
    uint VertexStorageSet,
    uint VertexStorageBinding,
    ShaderResourceAccess VertexStorageAccess,
    uint TextureSet,
    uint TextureBinding,
    ShaderResourceAccess TextureAccess,
    uint PushConstantSize);

public static class TextShaderArtifactContract
{
    // Resource semantics remain owned by Delta.Shader; Render introduces no second manifest.
    public static TextShaderArtifactDiagnostic Validate(GraphicsShaderProgram program)
    {
        if (program.Vertex.FormatVersion != ShaderArtifact.CurrentFormatVersion ||
            program.Fragment.FormatVersion != ShaderArtifact.CurrentFormatVersion)
        {
            return new(TextShaderArtifactStatus.Invalid, "Unsupported ShaderArtifact format version.");
        }

        if (!TryDescribe(program, out _, out var diagnostic))
        {
            return diagnostic;
        }

        return new(TextShaderArtifactStatus.Ready, "Text shader artifact manifest is ready.");
    }

    public static bool TryDescribe(GraphicsShaderProgram program, out TextGraphicsShaderLayout layout, out TextShaderArtifactDiagnostic diagnostic)
    {
        layout = default;
        if (program.Vertex.FormatVersion != ShaderArtifact.CurrentFormatVersion ||
            program.Fragment.FormatVersion != ShaderArtifact.CurrentFormatVersion)
        {
            diagnostic = new(TextShaderArtifactStatus.Invalid, "Unsupported ShaderArtifact format version.");
            return false;
        }

        if (!ValidateVertex(program.Vertex.Manifest, out var vertexMessage, out var vertexLayout))
        {
            diagnostic = new(TextShaderArtifactStatus.Invalid, vertexMessage);
            return false;
        }

        if (!ValidateFragment(program.Fragment.Manifest, out var fragmentMessage, out var fragmentLayout))
        {
            diagnostic = new(TextShaderArtifactStatus.Invalid, fragmentMessage);
            return false;
        }

        if (vertexLayout.PushConstantSize == 0 || fragmentLayout.PushConstantSize == 0 ||
            vertexLayout.PushConstantSize != fragmentLayout.PushConstantSize)
        {
            diagnostic = new(TextShaderArtifactStatus.Invalid, "Text shader stages must declare the same non-zero push-constant size.");
            return false;
        }

        layout = new TextGraphicsShaderLayout(
            vertexLayout.VertexStorageSet,
            vertexLayout.VertexStorageBinding,
            vertexLayout.VertexStorageAccess,
            fragmentLayout.TextureSet,
            fragmentLayout.TextureBinding,
            fragmentLayout.TextureAccess,
            vertexLayout.PushConstantSize);
        diagnostic = new(TextShaderArtifactStatus.Ready, "Text shader artifact manifest is ready.");
        return true;
    }

    private static bool ValidateVertex(ShaderAbiManifest manifest, out string message, out TextGraphicsShaderLayout layout)
    {
        layout = default;
        if (manifest.Version != ShaderAbiManifest.CurrentVersion || string.IsNullOrWhiteSpace(manifest.EntryPointName))
        {
            message = "Vertex text artifact manifest is incomplete.";
            return false;
        }

        if (!TryGetStorageBuffer(manifest.Resources, out var storage))
        {
            message = "Missing vertex text storage buffer resource at set/binding declared by the shader manifest.";
            return false;
        }

        layout = new TextGraphicsShaderLayout(
            storage.Set,
            storage.Binding,
            storage.Access,
            0,
            0,
            ShaderResourceAccess.ReadOnly,
            manifest.PushConstants.FirstOrDefault()?.Size ?? 0);
        message = string.Empty;
        return true;
    }

    private static bool ValidateFragment(ShaderAbiManifest manifest, out string message, out TextGraphicsShaderLayout layout)
    {
        layout = default;
        if (manifest.Version != ShaderAbiManifest.CurrentVersion || string.IsNullOrWhiteSpace(manifest.EntryPointName))
        {
            message = "Fragment text artifact manifest is incomplete.";
            return false;
        }

        if (!TryGetSampledTexture(manifest.Resources, out var texture))
        {
            message = "Missing sampled texture resource in fragment text manifest.";
            return false;
        }

        layout = new TextGraphicsShaderLayout(
            0,
            0,
            ShaderResourceAccess.ReadOnly,
            texture.Set,
            texture.Binding,
            texture.Access,
            manifest.PushConstants.FirstOrDefault()?.Size ?? 0);
        message = string.Empty;
        return true;
    }

    private static bool TryGetStorageBuffer(IReadOnlyList<ShaderAbiResource> resources, out ShaderAbiResource resource)
    {
        resource = default!;
        var seen = new HashSet<(uint Set, uint Binding)>();
        foreach (var candidate in resources)
        {
            if (!seen.Add((candidate.Set, candidate.Binding)))
            {
                return false;
            }

            if (candidate.Category == "storage-buffer")
            {
                if (candidate.Access == ShaderResourceAccess.ReadOnly && candidate.Layout == "std430")
                {
                    resource = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetSampledTexture(IReadOnlyList<ShaderAbiResource> resources, out ShaderAbiResource resource)
    {
        resource = default!;
        foreach (var candidate in resources)
        {
            if (candidate.Category == "sampled-texture" && candidate.Access == ShaderResourceAccess.ReadOnly)
            {
                resource = candidate;
                return true;
            }
        }

        return false;
    }
}
