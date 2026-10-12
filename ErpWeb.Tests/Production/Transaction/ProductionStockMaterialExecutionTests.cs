using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockMaterialExecutionTests
{
    [Fact]
    public void Golden_issue_consume_found_return_shortage_and_reversal_preserve_audit_totals()
    {
        var facts = new List<ProductionMaterialExecutionFact>
        {
            new(1, "ISSUE", 100, 100), new(2, "CONSUME", 40, 40, 1),
            new(3, "ADJUST", 20, 20, 1, ProductionBalLotMovementId: 103, ProductionBalLotMovementType: "ADJUST_IN"),
            new(4, "RETURN", 50, 50, 1)
        };
        var totals = ProductionMaterialExecutionFacts.Calculate(facts);
        Assert.Equal(100, totals.EffectiveIssuedQty);
        Assert.Equal(50, totals.EffectiveReturnedQty);
        Assert.Equal(40, totals.EffectiveConsumedQty);
        Assert.Equal(20, totals.NetActiveAdjustmentQty);
        Assert.Equal(30, totals.RemainingProductionMaterialBaseQty);
        facts.Add(new(5, "ADJUST", 5, 5, 1, ProductionBalLotMovementId: 105, ProductionBalLotMovementType: "ADJUST_OUT"));
        Assert.Equal(25, ProductionMaterialExecutionFacts.Calculate(facts).RemainingProductionMaterialBaseQty);
        facts.Add(new(6, "ADJUST", 5, 5, 5, 5, 106, "ADJUST_IN"));
        Assert.Equal(totals, ProductionMaterialExecutionFacts.Calculate(facts));
        facts.Add(new(7, "RETURN_REVERSAL", 50, 50, 4, 4));
        Assert.Equal(0, ProductionMaterialExecutionFacts.Calculate(facts).EffectiveReturnedQty);
        Assert.Equal(80, ProductionMaterialExecutionFacts.Calculate(facts).RemainingProductionMaterialQty);
    }

    [Theory]
    [InlineData(100, "ADJUST_OUT", 20, 80, 20)]
    [InlineData(80, "ADJUST_IN", 20, 100, 0)]
    public void Adjustment_changes_replacement_allowance_without_falsifying_issued_qty(
        decimal issued, string direction, decimal adjustment, decimal supply, decimal allowance)
    {
        var facts = new[] { new ProductionMaterialExecutionFact(1, "ISSUE", issued, issued),
            new(2, "ADJUST", adjustment, adjustment, 1, ProductionBalLotMovementId: 102, ProductionBalLotMovementType: direction) };
        var totals = ProductionMaterialExecutionFacts.Calculate(facts);
        Assert.Equal(issued, totals.EffectiveIssuedQty);
        Assert.Equal(supply, totals.EffectiveSupplyQty);
        Assert.Equal(allowance, totals.OutstandingQty(100));
        var afterReplacement = ProductionMaterialExecutionFacts.Calculate(facts.Append(new(3, "ISSUE", allowance, allowance)));
        Assert.Equal(100, afterReplacement.EffectiveSupplyQty);
        Assert.Equal(issued + allowance, afterReplacement.EffectiveIssuedQty);
        Assert.Equal(0, afterReplacement.OutstandingQty(100));
    }

    [Fact]
    public void Work_order_uom_and_base_uom_are_not_mixed_and_consumption_does_not_reopen_supply()
    {
        var totals = ProductionMaterialExecutionFacts.Calculate([
            new(1, "ISSUE", 10, 250), new(2, "CONSUME", 4, 100, 1),
            new(3, "RETURN", 2, 50, 1), new(4, "RETURN_REVERSAL", 2, 50, 3, 3),
            new(5, "ADJUST", 1, 25, 1, ProductionBalLotMovementId: 105, ProductionBalLotMovementType: "ADJUST_OUT")]);
        Assert.Equal(9, totals.EffectiveSupplyQty);
        Assert.Equal(225, totals.EffectiveSupplyBaseQty);
        Assert.Equal(5, totals.RemainingProductionMaterialQty);
        Assert.Equal(125, totals.RemainingProductionMaterialBaseQty);
        Assert.Equal(1, totals.OutstandingQty(10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RETURN")]
    [InlineData("ISSUE")]
    public void Adjustment_without_exact_balance_direction_fails_closed(string? direction)
    {
        Assert.Throws<InvalidOperationException>(() => ProductionMaterialExecutionFacts.Calculate([
            new(1, "ADJUST", 10, 10, ProductionBalLotMovementId: 101, ProductionBalLotMovementType: direction)]));
    }

    [Theory]
    [InlineData("ADJUST_IN", "ADJUST_OUT")]
    [InlineData("ADJUST_OUT", "ADJUST_IN")]
    public void Adjustment_inverse_collapses_from_quantity_and_chronology_replay(string forward, string inverse)
    {
        var date = new DateTime(2026, 10, 1);
        ProductionBalLotMovement[] rows = [new() { Uid = 1, MovementType = "ISSUE", BaseQty = 100, MovementDate = date },
            new() { Uid = 2, MovementType = forward, BaseQty = 20, MovementDate = date.AddDays(1) },
            new() { Uid = 3, MovementType = inverse, BaseQty = 20, OriginalMovementId = 2, MovementDate = date.AddDays(2) }];
        Assert.Single(ProductionBalLotSignedQty.EffectiveMovements(rows));
        Assert.Equal(100, ProductionBalLotSignedQty.SumEffectiveBaseQty(rows));
        Assert.Equal(date, ProductionBalLotSignedQty.LatestEffectiveMovementDate(rows));
    }

    [Theory]
    [InlineData("RETURN", "RETURN_REVERSAL")]
    [InlineData("ADJUST", "ADJUST")]
    public void Reversed_material_dependency_no_longer_blocks_issue(string forward, string inverse)
    {
        var movement = new ProductionMaterialMovement { Uid = 2, MovementType = forward, OriginalMovementId = 1, SourceIssueMovementId = 1 };
        Assert.True(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency([movement]));
        var reversal = new ProductionMaterialMovement { Uid = 3, MovementType = inverse, OriginalMovementId = 2, ReversesMaterialMovementId = 2, SourceIssueMovementId = 1 };
        Assert.False(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency([movement, reversal]));
        Assert.True(ProductionMaterialMovementTotals.HasBlockingDownstreamDependency([
            movement, reversal, new() { Uid = 4, MovementType = forward, OriginalMovementId = 1 }]));
    }

    [Fact]
    public void Invalid_adjustment_reversal_cannot_cancel_a_forward_fact()
    {
        Assert.Throws<InvalidOperationException>(() => ProductionMaterialExecutionFacts.Calculate([
            new(1, "ADJUST", 20, 20, ProductionBalLotMovementId: 101, ProductionBalLotMovementType: "ADJUST_IN"),
            new(2, "ADJUST", 10, 10, 1, 1, 102, "ADJUST_OUT")]));
    }

    [Fact]
    public void Replacement_issue_basis_and_found_stock_basis_follow_effective_supply()
    {
        Assert.Equal(80, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(100, 100, [(100, 0, -20)]));
        Assert.Equal(100, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(120, 100, [(100, 0, -20)]));
        Assert.Equal(100, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(80, 100, [(100, 0, 20)]));
        Assert.Equal(80, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(100, 100, [(100, 20, 0)]));
        Assert.Equal(100, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(100, 100, [(100, 0, 0)]));
        // One component may need replacement while another has no further allowance.
        Assert.Equal(80, ProductionMaterialExecutionCalc.SupplyAdjustedBasisQty(100, 100, [(100, 0, -20), (50, 0, 0)]));
    }
}
