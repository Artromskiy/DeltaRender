using System.Buffers.Binary;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class ShaderAbiValueCodec
{
    public static void Pack(CaseValue value, ShaderAbiLayout layout, Span<byte> destination)
    {
        var cursor = 0;
        WriteLayout(layout, value.Words, destination, ref cursor);
        if (cursor != value.Words.Length)
        {
            throw new InvalidDataException($"Value '{value.Type}' does not fit its ShaderAbi layout.");
        }
    }

    public static uint[] Read(CaseValue value, ShaderAbiLayout layout, ReadOnlySpan<byte> source)
    {
        var words = new uint[value.Words.Length];
        var cursor = 0;
        ReadLayout(layout, words, source, ref cursor);
        if (cursor != words.Length)
        {
            throw new InvalidDataException($"Output '{value.Type}' does not fit its ShaderAbi layout.");
        }

        return words;
    }

    private static void WriteLayout(ShaderAbiLayout layout, ReadOnlySpan<uint> words, Span<byte> destination, ref int cursor)
    {
        if (layout.Members.Count == 0)
        {
            for (var index = 0; index < (layout.Size + 3) / 4 && cursor < words.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(index * 4)..], words[cursor++]);
            }

            return;
        }

        foreach (ShaderAbiMember member in layout.Members)
        {
            if (member.NestedLayout is not null)
            {
                WriteLayout(member.NestedLayout, words, destination[(int)member.Offset..], ref cursor);
                continue;
            }

            for (var offset = 0u; offset < member.Size && cursor < words.Length; offset += 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[checked((int)(member.Offset + offset))..], words[cursor++]);
            }
        }
    }

    private static void ReadLayout(ShaderAbiLayout layout, Span<uint> words, ReadOnlySpan<byte> source, ref int cursor)
    {
        if (layout.Members.Count == 0)
        {
            for (var index = 0; index < (layout.Size + 3) / 4 && cursor < words.Length; index++)
            {
                words[cursor++] = BinaryPrimitives.ReadUInt32LittleEndian(source[(index * 4)..]);
            }

            return;
        }

        foreach (ShaderAbiMember member in layout.Members)
        {
            if (member.NestedLayout is not null)
            {
                ReadLayout(member.NestedLayout, words, source[(int)member.Offset..], ref cursor);
                continue;
            }

            for (var offset = 0u; offset < member.Size && cursor < words.Length; offset += 4)
            {
                words[cursor++] = BinaryPrimitives.ReadUInt32LittleEndian(source[checked((int)(member.Offset + offset))..]);
            }
        }
    }
}
