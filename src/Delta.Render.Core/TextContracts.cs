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
        if (!IsValid || !Clip.TryGetScissor(metrics, out var scissor)) return false;
        var right = (long)PixelBounds.X + PixelBounds.Width;
        var bottom = (long)PixelBounds.Y + PixelBounds.Height;
        return right > scissor.X && bottom > scissor.Y && (long)PixelBounds.X < scissor.X + (long)scissor.Width &&
               (long)PixelBounds.Y < scissor.Y + (long)scissor.Height;
    }
}

public readonly struct TextRun
{
    public TextRun(ReadOnlyMemory<TextGlyphInstance> glyphs) => Glyphs = glyphs;
    public ReadOnlyMemory<TextGlyphInstance> Glyphs { get; }
    public bool IsEmpty => Glyphs.IsEmpty;
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
        if (ordered.Length < source.Length || batches.Length < source.Length) return false;
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            var key = source[sourceIndex].BatchKey;
            var seen = false;
            for (var prior = 0; prior < sourceIndex; prior++)
            {
                if (source[prior].BatchKey == key) { seen = true; break; }
            }
            if (seen) continue;
            var start = orderedCount;
            for (var glyphIndex = sourceIndex; glyphIndex < source.Length; glyphIndex++)
            {
                if (source[glyphIndex].BatchKey == key) ordered[orderedCount++] = source[glyphIndex];
            }
            batches[batchCount++] = new TextBatchRange(key, start, orderedCount - start);
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

public static class TextShaderArtifactContract
{
    // Resource semantics remain owned by Delta.Shader; Render introduces no second manifest.
    public static TextShaderArtifactDiagnostic Validate(GraphicsShaderProgram program)
    {
        if (program.Vertex.FormatVersion != ShaderArtifact.CurrentFormatVersion ||
            program.Fragment.FormatVersion != ShaderArtifact.CurrentFormatVersion)
            return new(TextShaderArtifactStatus.Invalid, "Unsupported ShaderArtifact format version.");
        return new(TextShaderArtifactStatus.SampledImageAbiUnavailable,
            "Delta.Shader artifact metadata does not yet expose sampled image and sampler resources.");
    }
}
