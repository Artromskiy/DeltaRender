using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal enum VulkanQueueRole : byte
{
    Graphics,
    Compute,
    Transfer,
}

internal readonly record struct VulkanQueueFamilies(uint Graphics, uint Compute, uint Transfer, uint Present)
{
    internal uint For(VulkanQueueRole role) => role switch
    {
        VulkanQueueRole.Graphics => Graphics,
        VulkanQueueRole.Compute => Compute,
        VulkanQueueRole.Transfer => Transfer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Vulkan queue role."),
    };

    internal uint[] ToUniqueArray()
    {
        var values = new uint[4];
        var count = 0;
        AddUnique(Graphics);
        AddUnique(Compute);
        AddUnique(Transfer);
        AddUnique(Present);
        if (count != values.Length)
        {
            Array.Resize(ref values, count);
        }

        return values;

        void AddUnique(uint value)
        {
            for (var index = 0; index < count; index++)
            {
                if (values[index] == value)
                {
                    return;
                }
            }

            values[count++] = value;
        }
    }

    internal static VulkanQueueFamilies Select(ReadOnlySpan<QueueFamilyProperties> families, uint graphics, uint present)
    {
        var compute = FindDedicated(families, QueueFlags.ComputeBit, QueueFlags.GraphicsBit);
        if (compute == uint.MaxValue)
        {
            compute = FindAny(families, QueueFlags.ComputeBit);
        }

        if (compute == uint.MaxValue)
        {
            compute = graphics;
        }

        var transfer = FindDedicated(families, QueueFlags.TransferBit, QueueFlags.GraphicsBit | QueueFlags.ComputeBit);
        if (transfer == uint.MaxValue)
        {
            transfer = FindAny(families, QueueFlags.TransferBit);
        }

        if (transfer == uint.MaxValue)
        {
            transfer = compute;
        }

        return new VulkanQueueFamilies(graphics, compute, transfer, present);
    }

    private static uint FindDedicated(ReadOnlySpan<QueueFamilyProperties> families, QueueFlags required, QueueFlags excluded)
    {
        for (var index = 0; index < families.Length; index++)
        {
            var flags = families[index].QueueFlags;
            if ((flags & required) == required && (flags & excluded) == 0)
            {
                return (uint)index;
            }
        }

        return uint.MaxValue;
    }

    private static uint FindAny(ReadOnlySpan<QueueFamilyProperties> families, QueueFlags required)
    {
        for (var index = 0; index < families.Length; index++)
        {
            if ((families[index].QueueFlags & required) == required)
            {
                return (uint)index;
            }
        }

        return uint.MaxValue;
    }
}
