using System.Buffers.Binary;
using Delta.Shader.Contract;

namespace Delta.Render.MathConformance;

internal static class ShaderAbiValueCodec
{
    public static void Pack(CaseValue value, ShaderAbiLayout layout, Span<byte> destination)
    {
        var cursor = 0;
        WriteLayout(layout, value.Words, destination, ref cursor, GetScalarByteWidth(value.Type));
        if (cursor != value.Words.Length)
        {
            throw new InvalidDataException($"Value '{value.Type}' does not fit its ShaderAbi layout.");
        }
    }

    public static uint[] Read(CaseValue value, ShaderAbiLayout layout, ReadOnlySpan<byte> source)
    {
        var words = new uint[value.Words.Length];
        var cursor = 0;
        ReadLayout(layout, words, source, ref cursor, GetScalarByteWidth(value.Type));
        if (cursor != words.Length)
        {
            throw new InvalidDataException($"Output '{value.Type}' does not fit its ShaderAbi layout.");
        }

        return words;
    }

    private static void WriteLayout(
        ShaderAbiLayout layout,
        ReadOnlySpan<uint> words,
        Span<byte> destination,
        ref int cursor,
        int scalarByteWidth)
    {
        if (layout.Members.Count == 0)
        {
            if (layout.MatrixStride != 0)
            {
                WriteMatrixLayout(layout, words, destination, ref cursor, scalarByteWidth);
                return;
            }

            for (var offset = 0u; cursor < words.Length; offset += checked((uint)scalarByteWidth))
            {
                WriteScalar(words, destination[checked((int)offset)..], ref cursor, scalarByteWidth);
            }

            return;
        }

        foreach (ShaderAbiMember member in layout.Members)
        {
            if (member.NestedLayout is not null)
            {
                WriteLayout(
                    member.NestedLayout,
                    words,
                    destination[(int)member.Offset..],
                    ref cursor,
                    GetMemberScalarByteWidth(member, scalarByteWidth));
                continue;
            }

            var memberByteWidth = GetMemberScalarByteWidth(member, scalarByteWidth);
            for (var offset = 0u; offset < member.Size && cursor < words.Length; offset += checked((uint)memberByteWidth))
            {
                WriteScalar(words, destination[checked((int)(member.Offset + offset))..], ref cursor, memberByteWidth);
            }
        }
    }

    private static void ReadLayout(
        ShaderAbiLayout layout,
        Span<uint> words,
        ReadOnlySpan<byte> source,
        ref int cursor,
        int scalarByteWidth)
    {
        if (layout.Members.Count == 0)
        {
            if (layout.MatrixStride != 0)
            {
                ReadMatrixLayout(layout, words, source, ref cursor, scalarByteWidth);
                return;
            }

            for (var offset = 0u; cursor < words.Length; offset += checked((uint)scalarByteWidth))
            {
                ReadScalar(source[checked((int)offset)..], scalarByteWidth, words, ref cursor);
            }

            return;
        }

        foreach (ShaderAbiMember member in layout.Members)
        {
            if (member.NestedLayout is not null)
            {
                ReadLayout(
                    member.NestedLayout,
                    words,
                    source[(int)member.Offset..],
                    ref cursor,
                    GetMemberScalarByteWidth(member, scalarByteWidth));
                continue;
            }

            var memberByteWidth = GetMemberScalarByteWidth(member, scalarByteWidth);
            for (var offset = 0u; offset < member.Size && cursor < words.Length; offset += checked((uint)memberByteWidth))
            {
                ReadScalar(source[checked((int)(member.Offset + offset))..], memberByteWidth, words, ref cursor);
            }
        }
    }

    private static void WriteMatrixLayout(
        ShaderAbiLayout layout,
        ReadOnlySpan<uint> words,
        Span<byte> destination,
        ref int cursor,
        int scalarByteWidth)
    {
        if (scalarByteWidth is not (2 or 4 or 8) || layout.MatrixStride % (uint)scalarByteWidth != 0 || layout.Size % layout.MatrixStride != 0)
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
        if ((ulong)componentCount * (uint)scalarByteWidth > layout.MatrixStride)
        {
            throw new InvalidDataException("Matrix components exceed the ShaderAbi column stride.");
        }

        for (var column = 0; column < columnCount; column++)
        {
            for (var component = 0; component < componentCount; component++)
            {
                var offset = checked((int)(column * layout.MatrixStride + (uint)component * (uint)scalarByteWidth));
                WriteScalar(words, destination[offset..], ref cursor, scalarByteWidth);
            }
        }
    }

    private static void ReadMatrixLayout(
        ShaderAbiLayout layout,
        Span<uint> words,
        ReadOnlySpan<byte> source,
        ref int cursor,
        int scalarByteWidth)
    {
        if (scalarByteWidth is not (2 or 4 or 8) || layout.MatrixStride % (uint)scalarByteWidth != 0 || layout.Size % layout.MatrixStride != 0)
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
        if ((ulong)componentCount * (uint)scalarByteWidth > layout.MatrixStride)
        {
            throw new InvalidDataException("Matrix components exceed the ShaderAbi column stride.");
        }

        for (var column = 0; column < columnCount; column++)
        {
            for (var component = 0; component < componentCount; component++)
            {
                var offset = checked((int)(column * layout.MatrixStride + (uint)component * (uint)scalarByteWidth));
                ReadScalar(source[offset..], scalarByteWidth, words, ref cursor);
            }
        }
    }

    private static int GetMemberScalarByteWidth(ShaderAbiMember member, int fallback)
        => member.Type.BitWidth is 16 or 32 or 64
            ? checked((int)(member.Type.BitWidth / 8))
            : fallback;

    private static int GetScalarByteWidth(string type)
        => type.StartsWith("half", StringComparison.Ordinal) ? 2
            : type.StartsWith("double", StringComparison.Ordinal) || type.StartsWith("dvec", StringComparison.Ordinal) ? 8
            : 4;

    private static void WriteScalar(ReadOnlySpan<uint> words, Span<byte> destination, ref int cursor, int byteWidth)
    {
        switch (byteWidth)
        {
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)words[cursor++]));
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, words[cursor++]);
                break;
            case 8:
                var low = words[cursor++];
                var high = words[cursor++];
                BinaryPrimitives.WriteUInt64LittleEndian(destination, low | ((ulong)high << 32));
                break;
            default:
                throw new InvalidDataException($"Unsupported ShaderAbi scalar width: {byteWidth} bytes.");
        }
    }

    private static void ReadScalar(ReadOnlySpan<byte> source, int byteWidth, Span<uint> words, ref int cursor)
    {
        switch (byteWidth)
        {
            case 2:
                words[cursor++] = BinaryPrimitives.ReadUInt16LittleEndian(source);
                break;
            case 4:
                words[cursor++] = BinaryPrimitives.ReadUInt32LittleEndian(source);
                break;
            case 8:
                var value = BinaryPrimitives.ReadUInt64LittleEndian(source);
                words[cursor++] = (uint)value;
                words[cursor++] = (uint)(value >> 32);
                break;
            default:
                throw new InvalidDataException($"Unsupported ShaderAbi scalar width: {byteWidth} bytes.");
        }
    }
}
