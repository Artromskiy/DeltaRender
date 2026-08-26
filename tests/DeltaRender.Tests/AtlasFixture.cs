using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Delta.Render;

namespace Delta.Render.Tests;

internal static partial class AtlasFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"(?<=\d),(?=\d)")]
    private static partial Regex DecimalCommaRegex();

    public static AtlasFixtureData Load()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "delta-text-atlas");
        var atlasJson = File.ReadAllText(Path.Combine(root, "atlas.json"));
        atlasJson = DecimalCommaRegex().Replace(atlasJson, ".");
        var summary = JsonSerializer.Deserialize<AtlasFixtureSummary>(
            atlasJson,
            JsonOptions);
        if (summary.Pages == 0 || summary.Glyphs.Length == 0)
        {
            throw new InvalidDataException("Atlas fixture metadata was empty.");
        }

        var pngPath = Path.Combine(root, "page-000.png");
        var pixels = DecodeGray8Png(File.ReadAllBytes(pngPath), out var width, out var height);
        if (summary.Pages != 1 || width != 256 || height != 256)
        {
            throw new InvalidDataException($"Atlas fixture dimensions were unexpected: {width}x{height}, pages={summary.Pages}.");
        }

        return new AtlasFixtureData(summary, new TextAtlasPageDescription(new TextAtlasPageId(1), (uint)width, (uint)height, TextAtlasFormat.R8Unorm), pixels);
    }

    private static byte[] DecodeGray8Png(byte[] png, out int width, out int height)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
        {
            throw new InvalidDataException("Atlas fixture is not a PNG.");
        }

        var offset = 8;
        var ihdrLength = ReadBigEndianInt32(png.AsSpan(offset, 4));
        offset += 4;
        if (png[offset] != (byte)'I' || png[offset + 1] != (byte)'H' || png[offset + 2] != (byte)'D' || png[offset + 3] != (byte)'R')
        {
            throw new InvalidDataException("Atlas fixture is missing IHDR.");
        }

        offset += 4;
        if (ihdrLength < 13)
        {
            throw new InvalidDataException("Atlas fixture IHDR chunk is truncated.");
        }

        width = ReadBigEndianInt32(png.AsSpan(offset, 4));
        height = ReadBigEndianInt32(png.AsSpan(offset + 4, 4));
        var bitDepth = png[offset + 8];
        var colorType = png[offset + 9];
        if (bitDepth != 8 || colorType != 0)
        {
            throw new InvalidDataException($"Atlas fixture is not grayscale 8-bit: depth={bitDepth}, color={colorType}.");
        }

        var idat = new MemoryStream();
        offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = ReadBigEndianInt32(png.AsSpan(offset, 4));
            offset += 4;
            var type = System.Text.Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            if (offset + length > png.Length)
            {
                throw new InvalidDataException("Atlas fixture PNG chunk overflow.");
            }

            if (type == "IDAT")
            {
                idat.Write(png, offset, length);
            }
            offset += length + 4;
            if (type == "IEND")
            {
                break;
            }
        }

        using var input = new MemoryStream(idat.ToArray());
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var outputStream = new MemoryStream();
        inflater.CopyTo(outputStream);
        var decompressed = outputStream.ToArray();
        var expected = checked(width * height);
        var output = new byte[expected];
        var scanline = width;
        var src = 0;
        var dst = 0;
        var prior = new byte[scanline];
        var current = new byte[scanline];

        for (var y = 0; y < height; y++)
        {
            var filter = decompressed[src++];
            Array.Clear(current);
            if (filter == 0)
            {
                Buffer.BlockCopy(decompressed, src, current, 0, scanline);
            }
            else if (filter == 1)
            {
                for (var x = 0; x < scanline; x++)
                {
                    var left = x == 0 ? 0 : current[x - 1];
                    current[x] = (byte)((decompressed[src + x] + left) & 0xff);
                }
            }
            else if (filter == 2)
            {
                for (var x = 0; x < scanline; x++)
                {
                    current[x] = (byte)((decompressed[src + x] + prior[x]) & 0xff);
                }
            }
            else if (filter == 3)
            {
                for (var x = 0; x < scanline; x++)
                {
                    var left = x == 0 ? 0 : current[x - 1];
                    var up = prior[x];
                    current[x] = (byte)((decompressed[src + x] + ((left + up) >> 1)) & 0xff);
                }
            }
            else if (filter == 4)
            {
                for (var x = 0; x < scanline; x++)
                {
                    var a = x == 0 ? 0 : current[x - 1];
                    var b = prior[x];
                    var c = x == 0 ? 0 : prior[x - 1];
                    current[x] = (byte)((decompressed[src + x] + Paeth(a, b, c)) & 0xff);
                }
            }
            else
            {
                throw new InvalidDataException($"Unsupported PNG filter {filter}.");
            }

            Buffer.BlockCopy(current, 0, output, dst, scanline);
            (prior, current) = (current, prior);
            src += scanline;
            dst += scanline;
        }

        return output;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

internal readonly record struct AtlasFixtureSummary(string Font, string Mode, int PixelSize, int Pages, AtlasFixtureGlyph[] Glyphs);

internal readonly record struct AtlasFixtureGlyph(uint GlyphId, int PageIndex, float U0, float V0, float U1, float V1, int Width, int Height, int Stride);

internal readonly record struct AtlasFixtureData(AtlasFixtureSummary Summary, TextAtlasPageDescription Description, byte[] Pixels);
