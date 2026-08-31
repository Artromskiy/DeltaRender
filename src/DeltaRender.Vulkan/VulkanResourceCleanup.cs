using System.Diagnostics.CodeAnalysis;

namespace Delta.Render.Vulkan;

internal static class VulkanResourceCleanup
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup must attempt every native release and aggregate failures without hiding the original operation failure.")]
    internal static void CleanupInReverse<T>(ReadOnlySpan<T> resources, Action<T> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        List<Exception>? failures = null;
        for (var index = resources.Length - 1; index >= 0; index--)
        {
            try
            {
                cleanup(resources[index]);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more Vulkan resources failed during cleanup.", failures);
        }
    }
}
