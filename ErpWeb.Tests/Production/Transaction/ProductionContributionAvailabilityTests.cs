using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionContributionAvailabilityTests
{
    [Fact]
    public void Shared_formula_nets_allocations_and_reversals_per_receipt()
    {
        var movements = new[]
        {
            new ProductionBalLotMovement { Uid = 10, MovementType = ProductionBalLotMovementTypes.Issue, BaseQty = 100 },
            new ProductionBalLotMovement { Uid = 11, MovementType = ProductionBalLotMovementTypes.AdjustIn, BaseQty = 20 }
        };
        var allocations = new[]
        {
            new ProductionMovementAllocation { ReceiptMovementId = 10, BaseQty = 80 },
            new ProductionMovementAllocation { ReceiptMovementId = 10, BaseQty = 15, ReversesAllocationId = 1 }
        };

        Assert.Equal(35, ProductionContributionAvailabilityService.CalculateAvailableBaseQty(10, 100, movements, allocations));
        Assert.Equal(20, ProductionContributionAvailabilityService.CalculateAvailableBaseQty(11, 20, movements, allocations));
    }

    [Fact]
    public void Strict_return_allocator_cannot_spend_adjustment_in_contribution()
    {
        var issueOnly = new[] { new ProductionContribution(1, 10, DateTime.UnixEpoch, 1, 1, 20) };
        var plan = new ProductionContributionAllocator().BuildPlan(issueOnly, [new("return-line", 20)]);
        var allocation = Assert.Single(plan);
        Assert.Equal(10, allocation.ReceiptMovementId);
        Assert.Throws<StockLedgerException>(() => new ProductionContributionAllocator().BuildPlan(
            issueOnly, [new("too-large", 21)]));
    }
}
