using System.Runtime.InteropServices;
using Delta.Render.Core;

namespace Delta.Render.Vulkan;

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct TextGlyphGpu
{
    [FieldOffset(0)]
    internal float PixelMinX;
    [FieldOffset(4)]
    internal float PixelMinY;
    [FieldOffset(8)]
    internal float PixelMaxX;
    [FieldOffset(12)]
    internal float PixelMaxY;
    [FieldOffset(16)]
    internal float UvMinX;
    [FieldOffset(20)]
    internal float UvMinY;
    [FieldOffset(24)]
    internal float UvMaxX;
    [FieldOffset(28)]
    internal float UvMaxY;
    [FieldOffset(32)]
    internal float ColorR;
    [FieldOffset(36)]
    internal float ColorG;
    [FieldOffset(40)]
    internal float ColorB;
    [FieldOffset(44)]
    internal float ColorA;

    internal static TextGlyphGpu Pack(in TextGlyphInstance glyph)
    {
        if (!glyph.IsValid)
        {
            throw new ArgumentException("Text glyph domain validation failed.", nameof(glyph));
        }

        var pixelMaxX = checked((long)glyph.PixelBounds.X + glyph.PixelBounds.Width);
        var pixelMaxY = checked((long)glyph.PixelBounds.Y + glyph.PixelBounds.Height);
        var uvMaxX = glyph.Uv.U + glyph.Uv.Width;
        var uvMaxY = glyph.Uv.V + glyph.Uv.Height;
        if (!float.IsFinite(uvMaxX) || !float.IsFinite(uvMaxY))
        {
            throw new ArgumentException("Text glyph UV bounds are not finite.", nameof(glyph));
        }

        return new TextGlyphGpu
        {
            PixelMinX = glyph.PixelBounds.X,
            PixelMinY = glyph.PixelBounds.Y,
            PixelMaxX = pixelMaxX,
            PixelMaxY = pixelMaxY,
            UvMinX = glyph.Uv.U,
            UvMinY = glyph.Uv.V,
            UvMaxX = uvMaxX,
            UvMaxY = uvMaxY,
            ColorR = glyph.Color.Red,
            ColorG = glyph.Color.Green,
            ColorB = glyph.Color.Blue,
            ColorA = glyph.Color.Alpha
        };
    }

    internal static void Pack(ReadOnlySpan<TextGlyphInstance> source, Span<TextGlyphGpu> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("GPU destination is smaller than the glyph source.", nameof(destination));
        }

        for (var index = 0; index < source.Length; index++)
        {
            destination[index] = Pack(in source[index]);
        }
    }
}
