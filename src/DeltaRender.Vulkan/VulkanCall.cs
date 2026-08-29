using Silk.NET.Vulkan;

namespace Delta.Render.Vulkan;

internal static class VulkanCall
{
    internal static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }
}
