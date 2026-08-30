using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal static class VulkanCall
{
    internal static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new VulkanOperationException(result, operation);
        }
    }
}

internal sealed class VulkanOperationException : InvalidOperationException
{
    internal VulkanOperationException(Result result, string operation)
        : base($"{operation} failed: {result}")
    {
        Result = result;
    }

    internal Result Result { get; }
}
