using System.Runtime.ExceptionServices;

namespace Delta.Render.Vulkan;

internal enum VulkanSessionResourceStage
{
    RenderPass,
    Swapchain,
    SwapchainImages,
    ImageAvailableSemaphore,
    RenderCompleteSemaphore,
    Fence,
    CommandPool,
    CommandBuffer,
    TextAtlas
}

internal static class VulkanSessionResourceStages
{
    internal static readonly VulkanSessionResourceStage[] All =
    [
        VulkanSessionResourceStage.RenderPass,
        VulkanSessionResourceStage.Swapchain,
        VulkanSessionResourceStage.SwapchainImages,
        VulkanSessionResourceStage.ImageAvailableSemaphore,
        VulkanSessionResourceStage.RenderCompleteSemaphore,
        VulkanSessionResourceStage.Fence,
        VulkanSessionResourceStage.CommandPool,
        VulkanSessionResourceStage.CommandBuffer,
        VulkanSessionResourceStage.TextAtlas
    ];
}

internal sealed class VulkanSessionRollbackLedger
{
    private readonly List<(VulkanSessionResourceStage Stage, Action Cleanup)> _owned = new();
    private bool _committed;

    internal void Own(VulkanSessionResourceStage stage, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (_committed)
        {
            throw new InvalidOperationException("The Vulkan session acquisition ledger was already committed.");
        }

        _owned.Add((stage, cleanup));
    }

    internal void Commit()
    {
        _owned.Clear();
        _committed = true;
    }

    internal void Rollback()
    {
        if (_committed)
        {
            return;
        }

        List<Exception>? failures = null;
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            try
            {
                _owned[index].Cleanup();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        _owned.Clear();
        if (failures is not null)
        {
            throw new AggregateException("One or more Vulkan session resources failed during rollback.", failures);
        }
    }

    internal void RollbackPreserving(Exception original)
    {
        ArgumentNullException.ThrowIfNull(original);
        try
        {
            Rollback();
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException("Vulkan session acquisition failed and cleanup also reported an error.", original, cleanupFailure);
        }

        ExceptionDispatchInfo.Capture(original).Throw();
    }
}

internal sealed class VulkanSessionResourceAcquirer
{
    private readonly VulkanSessionRollbackLedger _ledger = new();

    internal void Acquire(Action<VulkanSessionResourceStage> acquire, Action<VulkanSessionResourceStage> cleanup)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(cleanup);

        foreach (var stage in VulkanSessionResourceStages.All)
        {
            acquire(stage);
            _ledger.Own(stage, () => cleanup(stage));
        }

        _ledger.Commit();
    }

    internal void RollbackPreserving(Exception original) => _ledger.RollbackPreserving(original);
}
