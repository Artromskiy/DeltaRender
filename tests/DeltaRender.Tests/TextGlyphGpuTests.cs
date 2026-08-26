using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Delta.Render;
using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class TextGlyphGpuTests
{
    [Fact]
    public void TextGlyphGpuHasCanonicalStd430SizeAndOffsets()
    {
        Assert.Equal(48, Unsafe.SizeOf<TextGlyphGpu>());
        Assert.Equal((IntPtr)0, Marshal.OffsetOf<TextGlyphGpu>(nameof(TextGlyphGpu.PixelMinX)));
        Assert.Equal((IntPtr)8, Marshal.OffsetOf<TextGlyphGpu>(nameof(TextGlyphGpu.PixelMaxX)));
        Assert.Equal((IntPtr)16, Marshal.OffsetOf<TextGlyphGpu>(nameof(TextGlyphGpu.UvMinX)));
        Assert.Equal((IntPtr)32, Marshal.OffsetOf<TextGlyphGpu>(nameof(TextGlyphGpu.ColorR)));
    }

    [Fact]
    public void PackWritesManifestMemberValuesWithoutRawDomainFields()
    {
        var source = new TextGlyphInstance(
            new TextAtlasPageId(1),
            new TextUvRect(0.1f, 0.2f, 0.3f, 0.4f),
            new TextPixelBounds(2, 3, 5, 7),
            new TextColor(0.2f, 0.4f, 0.6f, 0.8f),
            UiClipRect.Unbounded,
            TextRenderMode.Sdf,
            4,
            0.01f);

        var packed = TextGlyphGpu.Pack(in source);

        Assert.Equal(2, packed.PixelMinX);
        Assert.Equal(3, packed.PixelMinY);
        Assert.Equal(7, packed.PixelMaxX);
        Assert.Equal(10, packed.PixelMaxY);
        Assert.Equal(0.1f, packed.UvMinX);
        Assert.Equal(0.2f, packed.UvMinY);
        Assert.Equal(0.4f, packed.UvMaxX);
        Assert.Equal(0.6f, packed.UvMaxY);
        Assert.Equal(0.2f, packed.ColorR);
        Assert.Equal(0.4f, packed.ColorG);
        Assert.Equal(0.6f, packed.ColorB);
        Assert.Equal(0.8f, packed.ColorA);

        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packed, 1));
        Assert.Equal(48, bytes.Length);
    }
}
