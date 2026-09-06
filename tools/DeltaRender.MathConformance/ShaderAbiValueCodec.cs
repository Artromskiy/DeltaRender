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
            if (layout.MatrixStride != 0)
            {
                WriteMatrixLayout(layout, words, destination, ref cursor);
                return;
            }

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
            if (layout.MatrixStride != 0)
            {
                ReadMatrixLayout(layout, words, source, ref cursor);
                return;
            }

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

    private static void WriteMatrixLayout(ShaderAbiLayout layout, ReadOnlySpan<uint> words, Span<byte> destination, ref int cursor)
    {
        if (layout.MatrixStride % 4 != 0 || layout.Size % layout.MatrixStride != 0)
        {
            throw new InvalidDataException("Matrix ShaderAbi layout must use a four-byte column stride.");
        }

        var columnCount = checked((int)(layout.Size / layout.MatrixStride));
        var remaining = words.Length - cursor;
        if (columnCount == 0 || remaining % columnCount != 0)
        {
            throw new InvalidDataException("Matrix value does not fit its ShaderAbi layout.");
        }

        var componentCount = remaining / columnCount;
        if ((ulong)componentCount * sizeof(uint) > layout.MatrixStride)
        {
            throw new InvalidDataException("Matrix components exceed the ShaderAbi column stride.");
        }

        for (var column = 0; column < columnCount; column++)
        {
            for (var component = 0; component < componentCount; component++)
            {
                var offset = checked((int)(column * layout.MatrixStride + (uint)component * sizeof(uint)));
                BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], words[cursor++]);
            }
        }
    }

    private static void ReadMatrixLayout(ShaderAbiLayout layout, Span<uint> words, ReadOnlySpan<byte> source, ref int cursor)
    {
        if (layout.MatrixStride % 4 != 0 || layout.Size % layout.MatrixStride != 0)
        {
            throw new InvalidDataException("Matrix ShaderAbi layout must use a four-byte column stride.");
        }

        var columnCount = checked((int)(layout.Size / layout.MatrixStride));
        var remaining = words.Length - cursor;
        if (columnCount == 0 || remaining % columnCount != 0)
        {
            throw new InvalidDataException("Matrix value does not fit its ShaderAbi layout.");
        }

        var componentCount = remaining / columnCount;
        if ((ulong)componentCount * sizeof(uint) > layout.MatrixStride)
        {
            throw new InvalidDataException("Matrix components exceed the ShaderAbi column stride.");
        }

        for (var column = 0; column < columnCount; column++)
        {
            for (var component = 0; component < componentCount; component++)
            {
                var offset = checked((int)(column * layout.MatrixStride + (uint)component * sizeof(uint)));
                words[cursor++] = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            }
        }
    }
}
