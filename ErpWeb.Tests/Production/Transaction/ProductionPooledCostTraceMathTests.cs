using ErpWeb.Core.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionPooledCostTraceMathTests
{
    [Fact]
    public void Proportional_component_allocation_reconciles()
    {
        var result = ProductionPooledCostTraceMath.Allocate(
            poolBaseQty: 10m,
            poolValue: 130m,
            outboundBaseQty: 5m,
            [
                new("material", "MATERIAL", 110m),
                new("labour", "LABOUR", 20m)
            ]);

        Assert.Equal(65m, result.OutboundValue);
        Assert.Equal(55m, result.Allocations.Single(x => x.Key == "material").Amount);
        Assert.Equal(10m, result.Allocations.Single(x => x.Key == "labour").Amount);
        Assert.Equal(65m, result.Allocations.Sum(x => x.Amount));
    }

    [Fact]
    public void Largest_atom_receives_the_deterministic_money_residual()
    {
        var atoms = new[]
        {
            new ProductionPooledCostTraceMath.ComponentAtom("b", "MATERIAL", 1m),
            new ProductionPooledCostTraceMath.ComponentAtom("a", "LABOUR", 1m),
            new ProductionPooledCostTraceMath.ComponentAtom("c", "OTHER", 1m)
        };

        var first = ProductionPooledCostTraceMath.Allocate(3m, 1m, 1m, atoms);
        var second = ProductionPooledCostTraceMath.Allocate(3m, 1m, 1m, atoms.Reverse().ToArray());

        Assert.Equal(0.333334m, first.Allocations.Single(x => x.Key == "a").Amount);
        Assert.Equal(first.Allocations.OrderBy(x => x.Key).Select(x => x.Amount),
            second.Allocations.OrderBy(x => x.Key).Select(x => x.Amount));
        Assert.Equal(1m, first.Allocations.Sum(x => x.Amount));
        Assert.All(first.Allocations, x => Assert.True(x.Amount >= 0m));
    }

    [Fact]
    public void Final_depletion_transfers_every_atom_exactly()
    {
        var atoms = new[]
        {
            new ProductionPooledCostTraceMath.ComponentAtom("material", "MATERIAL", 110m),
            new ProductionPooledCostTraceMath.ComponentAtom("labour", "LABOUR", 20m)
        };

        var allocation = ProductionPooledCostTraceMath.Allocate(10m, 130m, 10m, atoms);
        var remaining = ProductionPooledCostTraceMath.Subtract(atoms, allocation.Allocations);

        Assert.Equal(130m, allocation.OutboundValue);
        Assert.All(remaining, x => Assert.Equal(0m, x.Amount));
    }

    [Fact]
    public void Zero_value_outbound_is_valid_for_positive_quantity()
    {
        var result = ProductionPooledCostTraceMath.Allocate(
            10m,
            0m,
            3m,
            [new("verified-zero", "UNCLASSIFIED_VERIFIED", 0m)]);

        Assert.Equal(0m, result.OutboundValue);
        Assert.Equal(0m, result.Allocations.Single().Amount);
    }

    [Fact]
    public void Explicit_stored_value_is_used_and_cannot_exceed_pool()
    {
        var atoms = new[] { new ProductionPooledCostTraceMath.ComponentAtom("material", "MATERIAL", 100m) };

        var result = ProductionPooledCostTraceMath.Allocate(10m, 100m, 5m, 41.2345678m, atoms);
        Assert.Equal(41.234568m, result.OutboundValue);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionPooledCostTraceMath.Allocate(10m, 100m, 5m, 100.000001m, atoms));
    }
}
