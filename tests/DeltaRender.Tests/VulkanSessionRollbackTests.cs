using DeltaRender.Vulkan;
using Xunit;

namespace DeltaRender.Tests;

public sealed class VulkanSessionRollbackTests
{
    [Fact]
    public void FaultAtEachAcquisitionStageRollsBackOnlyAcquiredResourcesInReverseOrder()
    {
        foreach (var failedStage in VulkanSessionResourceStages.All)
        {
            var order = new List<VulkanSessionResourceStage>();
            var exception = Assert.Throws<InvalidOperationException>(() => AcquireUntilFailure(failedStage, order));

            Assert.Equal(failedStage.ToString(), exception.Message);
            var acquired = VulkanSessionResourceStages.All.TakeWhile(stage => stage != failedStage).Reverse().ToArray();
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
        var acquirer = new VulkanSessionResourceAcquirer();
        acquirer.Acquire(_ => { }, _ => cleanupCount++);

        acquirer.Commit();
        acquirer.Rollback();
        acquirer.Rollback();

        Assert.Equal(0, cleanupCount);
    }

    [Fact]
    public void FailureAfterAllAcquisitionsBeforeCommitRollsBackEveryStage()
    {
        var order = new List<VulkanSessionResourceStage>();
        var acquirer = new VulkanSessionResourceAcquirer();
        acquirer.Acquire(_ => { }, stage => order.Add(stage));
        var original = new InvalidOperationException("finalization");

        var exception = Assert.Throws<InvalidOperationException>(() => acquirer.RollbackPreserving(original));

        Assert.Same(original, exception);
        Assert.Equal(VulkanSessionResourceStages.All.Reverse(), order);
        acquirer.Rollback();
        Assert.Equal(VulkanSessionResourceStages.All.Length, order.Count);
    }

    private static void AcquireUntilFailure(VulkanSessionResourceStage failedStage, List<VulkanSessionResourceStage> order)
    {
        var acquirer = new VulkanSessionResourceAcquirer();
        try
        {
            acquirer.Acquire(
                stage =>
                {
                    if (stage == failedStage)
                    {
                        throw new InvalidOperationException(stage.ToString());
                    }
                },
                stage => order.Add(stage));
        }
        catch (Exception exception)
        {
            acquirer.RollbackPreserving(exception);
            throw;
        }
    }
}
