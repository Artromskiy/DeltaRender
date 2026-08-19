using System.Buffers.Binary;
using System.Text;

namespace Delta.Render.Vulkan;

internal static class SpirvEntryPointReader
{
    private const uint SpirvMagic = 0x07230203;
    private const ushort OpEntryPoint = 15;
    private const uint VertexExecutionModel = 0;
    private const uint FragmentExecutionModel = 4;
    private const uint ComputeExecutionModel = 5;

    public static string ReadComputeEntryPoint(ReadOnlySpan<byte> spirv)
        => ReadEntryPoint(spirv, ComputeExecutionModel, "compute");

    public static string ReadGraphicsEntryPoint(ReadOnlySpan<byte> spirv, bool vertex)
        => ReadEntryPoint(spirv, vertex ? VertexExecutionModel : FragmentExecutionModel, vertex ? "vertex" : "fragment");

    private static string ReadEntryPoint(ReadOnlySpan<byte> spirv, uint requestedExecutionModel, string stageName)
    {
        if (spirv.Length < 20 || (spirv.Length & 3) != 0)
        {
            throw new ArgumentException("SPIR-V must contain a five-word header and be word aligned.", nameof(spirv));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(spirv) != SpirvMagic)
        {
            throw new ArgumentException("SPIR-V magic is invalid.", nameof(spirv));
        }

        string? entryPoint = null;
        for (var offset = 20; offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(spirv[offset..]);
            var wordCount = (int)(instruction >> 16);
            var opcode = (ushort)(instruction & 0xffff);
            if (wordCount < 1 || offset > spirv.Length - (wordCount * 4))
            {
                throw new ArgumentException("SPIR-V contains a truncated instruction.", nameof(spirv));
            }

            if (opcode == OpEntryPoint)
            {
                if (wordCount < 4)
                {
                    throw new ArgumentException("SPIR-V OpEntryPoint instruction is truncated.", nameof(spirv));
                }

                var instructionBytes = spirv.Slice(offset + 4, (wordCount - 1) * 4);
                var executionModel = BinaryPrimitives.ReadUInt32LittleEndian(instructionBytes);
                if (executionModel == requestedExecutionModel)
                {
                    var nameBytes = instructionBytes[8..];
                    var terminator = nameBytes.IndexOf((byte)0);
                    if (terminator < 0)
                    {
                        throw new ArgumentException("SPIR-V OpEntryPoint name is not null terminated.", nameof(spirv));
                    }

                    var name = Encoding.UTF8.GetString(nameBytes[..terminator]);
                    if (name.Length == 0)
                    {
                        throw new ArgumentException($"SPIR-V {stageName} entry point name is empty.", nameof(spirv));
                    }

                    if (entryPoint is not null)
                    {
                        throw new ArgumentException($"SPIR-V contains multiple {stageName} entry points; the artifact must select one.", nameof(spirv));
                    }

                    entryPoint = name;
                }
            }

            offset += wordCount * 4;
        }

        return entryPoint ?? throw new ArgumentException($"SPIR-V does not contain a {stageName} entry point.", nameof(spirv));
    }
}
