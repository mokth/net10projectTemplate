using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionReplayDependencyPlannerTests
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 1, 8, 0, 0);

    [Fact]
    public async Task Orders_active_downstream_edges_and_rejects_a_cycle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = await OpenAsync(connection);
        var owner = await SeedMovementsAsync(db);
        var first = new ProductionPoolDependency
        {
            ContributorMovementId = 10, ConsumerMovementId = 20,
            StockPostingId = owner.Id
        };
        var second = new ProductionPoolDependency
        {
            ContributorMovementId = 20, ConsumerMovementId = 30,
            StockPostingId = owner.Id
        };
        db.ProductionPoolDependencyRows.AddRange(first, second);
        await db.SaveChangesAsync();

        var plan = await ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]);
        Assert.Equal([10L, 20L, 30L], plan.OrderedMovementIds);
        Assert.Equal([first.Id, second.Id], plan.Edges.Select(edge => edge.Id));

        var reversalRoot = await AddPostingAsync(db, 2);
        db.ProductionPoolDependencyRows.Add(new ProductionPoolDependency
        {
            ContributorMovementId = 20, ConsumerMovementId = 30,
            StockPostingId = reversalRoot.Id, ReversesDependencyId = second.Id
        });
        await db.SaveChangesAsync();
        var withoutReversed = await ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]);
        Assert.Equal([10L, 20L], withoutReversed.OrderedMovementIds);
        Assert.Single(withoutReversed.Edges);

        var replacementRoot = await AddPostingAsync(db, 3);
        db.ProductionPoolDependencyRows.AddRange(
            new ProductionPoolDependency
            {
                ContributorMovementId = 20, ConsumerMovementId = 30,
                StockPostingId = replacementRoot.Id
            },
            new ProductionPoolDependency
            {
                ContributorMovementId = 30, ConsumerMovementId = 10,
                StockPostingId = replacementRoot.Id
            });
        await db.SaveChangesAsync();
        var cycle = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, cycle.Error.Code);
        Assert.Contains("10", cycle.Error.Message);
        Assert.Contains("20", cycle.Error.Message);
        Assert.Contains("30", cycle.Error.Message);
    }

    [Fact]
    public async Task Rejects_a_dependency_to_another_tenant_before_recosting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = await OpenAsync(connection);
        var owner = await SeedMovementsAsync(db);
        var foreign = await db.ProductionBalLotMovements.SingleAsync(m => m.Uid == 20);
        foreign.BranchCode = "OTHER";
        await db.SaveChangesAsync();
        db.ProductionPoolDependencyRows.Add(new ProductionPoolDependency
        {
            ContributorMovementId = 10, ConsumerMovementId = 20,
            StockPostingId = owner.Id
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
        Assert.Contains("20", error.Error.Message);
    }

    [Fact]
    public async Task Rejects_foreign_root_dependency_without_replay_owner_evidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = await OpenAsync(connection);
        var owner = await SeedMovementsAsync(db);
        var foreignRoot = await AddPostingAsync(db, 2);
        var original = new ProductionPoolDependency
        {
            ContributorMovementId = 10, ConsumerMovementId = 20,
            StockPostingId = owner.Id
        };
        db.ProductionPoolDependencyRows.Add(original);
        await db.SaveChangesAsync();
        var replacement = new ProductionPoolDependency
        {
            ContributorMovementId = 10, ConsumerMovementId = 20,
            StockPostingId = foreignRoot.Id
        };
        db.ProductionPoolDependencyRows.AddRange(new ProductionPoolDependency
        {
            ContributorMovementId = 10, ConsumerMovementId = 20,
            StockPostingId = foreignRoot.Id,
            ReversesDependencyId = original.Id
        }, replacement);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
        Assert.Contains("without one replay effect", error.Error.Message);

        var run = new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            RootStockPostingId = foreignRoot.Id,
            RootRequestId = foreignRoot.RequestId,
            RootDocumentType = foreignRoot.SourceDocumentType,
            RootDocumentId = foreignRoot.SourceDocumentId,
            EarliestEffectiveAt = EffectiveAt,
            LatestEffectiveAt = EffectiveAt,
            PlanHash = new string('C', 64),
            CreatedAtUtc = EffectiveAt, CreatedBy = "test"
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync();
        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            ReplayRunId = run.Id,
            RootStockPostingId = foreignRoot.Id,
            BusinessOwnerPostingId = owner.Id,
            EffectKind = ProductionDependencyOwnerReader.EffectKind,
            AffectedEntityType = ProductionDependencyOwnerReader.AffectedEntityType,
            AffectedEntityId = replacement.Id,
            OriginalEntityId = original.Id,
            CreatedAtUtc = EffectiveAt
        });
        await db.SaveChangesAsync();
        var plan = await ProductionReplayDependencyPlanner.BuildAsync(db, "DEMO", "HQ", [10]);
        Assert.Equal([10L, 20L], plan.OrderedMovementIds);
        Assert.Equal(replacement.Id, Assert.Single(plan.Edges).Id);
    }

    private static async Task<AppDbContext> OpenAsync(SqliteConnection connection)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<StockPosting> SeedMovementsAsync(AppDbContext db)
    {
        var owner = await AddPostingAsync(db, 1);
        foreach (var (id, line) in new[] { (10L, 1), (20L, 2), (30L, 3) })
            db.ProductionBalLotMovements.Add(new ProductionBalLotMovement
            {
                Uid = id, ProductionBalLotId = id,
                MovementType = ProductionBalLotMovementTypes.Produce,
                Qty = 1m, Uom = "EA", BaseQty = 1m, BaseUom = "EA",
                TotalCost = 1m, UnitCost = 1m,
                PostingLinkId = 0, WorkOrderId = 0,
                LedgerVersion = 2, LedgerEpochId = 1,
                StockPostingId = owner.Id, PostingLineNo = line,
                CompanyCode = "DEMO", BranchCode = "HQ",
                ConversionFactorToBase = 1m,
                ValuationStatus = ProductionPoolValuationService.Verified,
                DocumentType = "TEST", DocumentNo = "1",
                MovementDate = EffectiveAt, CreatedDate = EffectiveAt,
                CreatedBy = "test"
            });
        await db.SaveChangesAsync();
        return owner;
    }

    private static async Task<StockPosting> AddPostingAsync(AppDbContext db, long sequence)
    {
        var posting = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = 1,
            PostingSequence = sequence, RequestId = Guid.NewGuid(),
            CommandType = "PRODUCTION_GRAPH_TEST",
            RequestFingerprint = new string('A', 64),
            SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
            SourceDocumentId = sequence.ToString(), SourceDocumentNo = sequence.ToString(),
            DocumentRevision = 1, PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}", SourceSnapshotHash = new string('B', 64),
            EffectiveAt = EffectiveAt, BusinessDate = EffectiveAt.Date,
            PeriodKey = "2026-10", PostedAtUtc = DateTime.UtcNow,
            PostedBy = "test", SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();
        return posting;
    }
}
