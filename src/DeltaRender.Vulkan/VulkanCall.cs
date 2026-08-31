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
    public VulkanOperationException()
        : this("The Vulkan operation failed.")
    {
    }

    public VulkanOperationException(string message)
        : base(message)
    {
        Result = Result.Success;
    }

    public VulkanOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Result = Result.Success;
    }

    internal VulkanOperationException(Result result, string operation)
        : base($"{operation} failed: {result}")
    {
        Result = result;
    }

    internal Result Result { get; }
}
