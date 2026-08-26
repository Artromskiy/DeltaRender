namespace DeltaRender.Vulkan;

internal static class VulkanResourceCleanup
{
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
