using Delta.Render.Vulkan;
using Xunit;

namespace Delta.Render.Tests;

public sealed class VulkanSessionRollbackTests
{
    private static readonly VulkanSessionResourceStage[] Stages =
    [
        VulkanSessionResourceStage.RenderPass,
        VulkanSessionResourceStage.Swapchain,
        VulkanSessionResourceStage.SwapchainImages,
        VulkanSessionResourceStage.ImageAvailableSemaphore,
        VulkanSessionResourceStage.RenderCompleteSemaphore,
        VulkanSessionResourceStage.Fence,
        VulkanSessionResourceStage.CommandPool,
        VulkanSessionResourceStage.CommandBuffer
    ];

    [Fact]
    public void FaultAtEachAcquisitionStageRollsBackOnlyAcquiredResourcesInReverseOrder()
    {
        foreach (var failedStage in Stages)
        {
            var order = new List<VulkanSessionResourceStage>();
            var exception = Assert.Throws<InvalidOperationException>(() => AcquireUntilFailure(failedStage, order));

            Assert.Equal(failedStage.ToString(), exception.Message);
            var acquired = Stages.TakeWhile(stage => stage != failedStage).Reverse().ToArray();
            Assert.Equal(acquired, order);
        }
    }

    [Fact]
    public void CleanupFailureDoesNotMaskAcquisitionFailure()
    {
        var order = new List<VulkanSessionResourceStage>();
        var ledger = new VulkanSessionRollbackLedger();
        ledger.Own(VulkanSessionResourceStage.RenderPass, () =>
        {
            order.Add(VulkanSessionResourceStage.RenderPass);
            throw new InvalidOperationException("cleanup");
        });
        ledger.Own(VulkanSessionResourceStage.Swapchain, () => order.Add(VulkanSessionResourceStage.Swapchain));
        var original = new InvalidOperationException("acquisition");

        var aggregate = Assert.Throws<AggregateException>(() => ledger.RollbackPreserving(original));

        Assert.Same(original, aggregate.InnerExceptions[0]);
        Assert.Equal(
            new[] { VulkanSessionResourceStage.Swapchain, VulkanSessionResourceStage.RenderPass },
            order);
    }

    [Fact]
    public void CommitTransfersOwnershipAndRollbackBecomesNoOp()
    {
        var cleanupCount = 0;
        var ledger = new VulkanSessionRollbackLedger();
        ledger.Own(VulkanSessionResourceStage.RenderPass, () => cleanupCount++);

        ledger.Commit();
        ledger.Rollback();
        ledger.Rollback();

        Assert.Equal(0, cleanupCount);
    }

    private static void AcquireUntilFailure(VulkanSessionResourceStage failedStage, List<VulkanSessionResourceStage> order)
    {
        var ledger = new VulkanSessionRollbackLedger();
        try
        {
            foreach (var stage in Stages)
            {
                if (stage == failedStage)
                {
                    throw new InvalidOperationException(stage.ToString());
                }

                var acquiredStage = stage;
                ledger.Own(stage, () => order.Add(acquiredStage));
            }

            ledger.Commit();
        }
        catch (Exception exception)
        {
            ledger.RollbackPreserving(exception);
            throw;
        }
    }
}
