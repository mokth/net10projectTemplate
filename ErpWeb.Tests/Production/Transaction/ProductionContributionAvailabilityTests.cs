using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task Active_edge_queries_exclude_reversed_originals_and_reversal_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var reversedAllocation = new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockPostingId = 1,
            ReceiptMovementId = 10, OutboundMovementId = 20, BaseQty = 2
        };
        var activeAllocation = new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockPostingId = 1,
            ReceiptMovementId = 11, OutboundMovementId = 21, BaseQty = 3
        };
        var reversedDependency = new ProductionPoolDependency
        {
            StockPostingId = 1, ContributorMovementId = 10, ConsumerMovementId = 20
        };
        var activeDependency = new ProductionPoolDependency
        {
            StockPostingId = 1, ContributorMovementId = 11, ConsumerMovementId = 21
        };
        db.ProductionMovementAllocations.AddRange(reversedAllocation, activeAllocation);
        db.ProductionPoolDependencyRows.AddRange(reversedDependency, activeDependency);
        await db.SaveChangesAsync();

        db.ProductionMovementAllocations.Add(new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockPostingId = 2,
            ReceiptMovementId = 10, OutboundMovementId = 22, BaseQty = 2,
            ReversesAllocationId = reversedAllocation.Id
        });
        db.ProductionPoolDependencyRows.Add(new ProductionPoolDependency
        {
            StockPostingId = 2, ContributorMovementId = 10, ConsumerMovementId = 22,
            ReversesDependencyId = reversedDependency.Id
        });
        await db.SaveChangesAsync();

        Assert.Equal(activeAllocation.Id, await ProductionActiveEdgeResolver.Allocations(db)
            .Select(x => x.Id).SingleAsync());
        Assert.Equal(activeDependency.Id, await ProductionActiveEdgeResolver.Dependencies(db)
            .Select(x => x.Id).SingleAsync());

        db.ProductionBalLotMovements.AddRange(
            new ProductionBalLotMovement
            {
                Uid = 10, ProductionBalLotId = 1,
                MovementType = ProductionBalLotMovementTypes.Issue,
                BaseQty = 10, Uom = "EA", BaseUom = "EA"
            },
            new ProductionBalLotMovement
            {
                Uid = 11, ProductionBalLotId = 1,
                MovementType = ProductionBalLotMovementTypes.Issue,
                BaseQty = 20, Uom = "EA", BaseUom = "EA"
            });
        db.ProductionValuationEvidenceRows.AddRange(
            new ProductionValuationEvidence { MovementId = 10, ProductionBalLotId = 1 },
            new ProductionValuationEvidence { MovementId = 11, ProductionBalLotId = 1 });
        await db.SaveChangesAsync();

        var availability = await ProductionContributionAvailabilityService.ReadAsync(db, 1, null,
            CancellationToken.None);
        Assert.Equal(10m, availability.Single(x => x.Movement.Uid == 10)
            .Contribution.AvailableBaseQty);
        Assert.Equal(17m, availability.Single(x => x.Movement.Uid == 11)
            .Contribution.AvailableBaseQty);
    }
}
