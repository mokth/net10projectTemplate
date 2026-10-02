using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionBalLotSignedQtyTests
{
    private static ProductionBalLotMovement Mov(
        long uid, string type, decimal baseQty, DateTime date, long? original = null) =>
        new()
        {
            Uid = uid,
            MovementType = type,
            BaseQty = baseQty,
            Qty = baseQty,
            Uom = "PCS",
            BaseUom = "PCS",
            MovementDate = date,
            CreatedDate = date,
            OriginalMovementId = original,
            DocumentType = "TEST",
            DocumentNo = "T1",
            CreatedBy = "t",
        };

    [Fact]
    public void Signed_base_qty_signs_produce_and_consume()
    {
        Assert.Equal(10m, ProductionBalLotSignedQty.SignedBaseQty(ProductionBalLotMovementTypes.Produce, 10m));
        Assert.Equal(-4m, ProductionBalLotSignedQty.SignedBaseQty(ProductionBalLotMovementTypes.Consume, 4m));
        Assert.Equal(4m, ProductionBalLotSignedQty.SignedBaseQty(ProductionBalLotMovementTypes.ConsumeReversal, 4m));
    }

    [Fact]
    public void Effective_movements_collapse_reversal_pairs()
    {
        var d = new DateTime(2026, 1, 1);
        var all = new[]
        {
            Mov(1, ProductionBalLotMovementTypes.Produce, 100m, d),
            Mov(2, ProductionBalLotMovementTypes.Consume, 100m, d.AddHours(1)),
            Mov(3, ProductionBalLotMovementTypes.ConsumeReversal, 100m, d.AddHours(2), original: 2),
        };

        var effective = ProductionBalLotSignedQty.EffectiveMovements(all);
        Assert.Single(effective);
        Assert.Equal(1, effective[0].Uid);
        Assert.True(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 1));
    }

    [Fact]
    public void Produce_rollback_blocked_when_later_consume_remains()
    {
        var d = new DateTime(2026, 1, 1);
        var all = new[]
        {
            Mov(1, ProductionBalLotMovementTypes.Produce, 100m, d),
            Mov(2, ProductionBalLotMovementTypes.Consume, 40m, d.AddHours(1)),
        };
        Assert.False(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 1));
    }

    [Fact]
    public void Produce_then_consume_then_later_produce_blocks_rollback_of_first_produce()
    {
        var d = new DateTime(2026, 1, 1);
        var all = new[]
        {
            Mov(1, ProductionBalLotMovementTypes.Produce, 100m, d),
            Mov(2, ProductionBalLotMovementTypes.Consume, 100m, d.AddHours(1)),
            Mov(3, ProductionBalLotMovementTypes.Produce, 50m, d.AddHours(2)),
        };
        Assert.False(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 1));
        Assert.True(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 3));
    }

    [Fact]
    public void Produce_A_then_B_then_consume_allows_rollback_A()
    {
        var d = new DateTime(2026, 1, 1);
        var all = new[]
        {
            Mov(1, ProductionBalLotMovementTypes.Produce, 50m, d),
            Mov(2, ProductionBalLotMovementTypes.Produce, 50m, d.AddMinutes(1)),
            Mov(3, ProductionBalLotMovementTypes.Consume, 40m, d.AddHours(1)),
        };
        Assert.True(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 1));
        Assert.True(ProductionBalLotSignedQty.CanRollbackProduce(all, produceUid: 2));
    }

    [Fact]
    public void Reconcile_helper_matches_lot_base_qty()
    {
        var d = new DateTime(2026, 1, 1);
        var all = new[]
        {
            Mov(1, ProductionBalLotMovementTypes.Issue, 100m, d),
            Mov(2, ProductionBalLotMovementTypes.Consume, 25m, d.AddHours(1)),
        };
        Assert.True(ProductionBalLotOpening.Reconciles(75m, all));
        Assert.False(ProductionBalLotOpening.Reconciles(80m, all));
    }
}

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionMaterialMovementTotalsTests
{
    [Fact]
    public void Effective_issue_and_consume_net_reversals()
    {
        Assert.Equal(70m, ProductionMaterialMovementTotals.EffectiveIssue(100m, 30m));
        Assert.Equal(40m, ProductionMaterialMovementTotals.EffectiveConsumed(50m, 10m));
    }

    [Fact]
    public void Issue_rollback_blocked_by_effective_consume()
    {
        var movements = new List<ProductionMaterialMovement>
        {
            new() { Uid = 10, MovementType = ProductionMaterialMovementTypes.Consume, OriginalMovementId = 1, Qty = 5m },
        };
        Assert.True(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency(movements));
    }

    [Fact]
    public void Issue_rollback_allowed_when_consume_fully_reversed()
    {
        var movements = new List<ProductionMaterialMovement>
        {
            new() { Uid = 10, MovementType = ProductionMaterialMovementTypes.Consume, OriginalMovementId = 1, Qty = 5m },
            new() { Uid = 11, MovementType = ProductionMaterialMovementTypes.ConsumeReversal, OriginalMovementId = 10, Qty = 5m },
        };
        Assert.False(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency(movements));
    }

    [Fact]
    public void Issue_rollback_blocked_by_return()
    {
        var movements = new List<ProductionMaterialMovement>
        {
            new() { Uid = 10, MovementType = ProductionMaterialMovementTypes.Return, OriginalMovementId = 1, Qty = 1m },
        };
        Assert.True(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency(movements));
    }

    [Fact]
    public void Issue_rollback_blocked_by_adjust()
    {
        var movements = new List<ProductionMaterialMovement>
        {
            new() { Uid = 10, MovementType = ProductionMaterialMovementTypes.Adjustment, OriginalMovementId = 1, Qty = 1m },
        };
        Assert.True(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency(movements));
    }
}

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionBalLotOpeningTests
{
    [Fact]
    public void Opening_uses_base_qty_and_reconstructs_display_qty()
    {
        var result = ProductionBalLotOpening.ComputeRemaining(new ProductionBalLotOpening.IssueContribution(
            IssueMovementId: 1,
            IssueBaseQty: 100m,
            ConversionFactorToBase: 2m,
            UnitCost: 1.5m,
            Downstream:
            [
                (ProductionMaterialMovementTypes.Consume, 20m, 1),
                (ProductionMaterialMovementTypes.ConsumeReversal, 10m, 99),
            ]));

        Assert.True(result.Succeeded);
        Assert.Equal(90m, result.RemainingBaseQty);
        Assert.Equal(45m, result.RemainingQty);
        Assert.Equal(135m, result.RemainingTotalCost);
    }

    [Fact]
    public void Opening_stops_on_return()
    {
        var result = ProductionBalLotOpening.ComputeRemaining(new ProductionBalLotOpening.IssueContribution(
            1, 50m, 1m, 1m,
            [(ProductionMaterialMovementTypes.Return, 5m, 1)]));
        Assert.False(result.Succeeded);
        Assert.Contains("RETURN", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opening_stops_on_adjust()
    {
        var result = ProductionBalLotOpening.ComputeRemaining(new ProductionBalLotOpening.IssueContribution(
            1, 50m, 1m, 1m,
            [(ProductionMaterialMovementTypes.Adjustment, 5m, 1)]));
        Assert.False(result.Succeeded);
        Assert.Contains("ADJUST", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fully_reversed_issue_omits_active_pile()
    {
        var result = ProductionBalLotOpening.ComputeRemaining(new ProductionBalLotOpening.IssueContribution(
            1, 50m, 1m, 2m,
            [(ProductionMaterialMovementTypes.IssueReversal, 50m, 1)]));
        Assert.True(result.Succeeded);
        Assert.Equal(0m, result.RemainingBaseQty);
    }
}
