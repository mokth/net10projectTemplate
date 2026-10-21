using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionDependencyOwnerReaderTests
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 1, 8, 0, 0);

    [Fact]
    public async Task Accepts_exact_foreign_root_replacement_and_rejects_broken_ownership()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = Posting(10, 1, "1");
        var root = Posting(11, 2, "2");
        db.StockPostings.AddRange(owner, root);
        var run = new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            RootStockPostingId = root.Id, RootRequestId = root.RequestId,
            RootDocumentType = root.SourceDocumentType,
            RootDocumentId = root.SourceDocumentId,
            EarliestEffectiveAt = root.EffectiveAt,
            LatestEffectiveAt = owner.EffectiveAt,
            PlanHash = new string('C', 64),
            CreatedAtUtc = EffectiveAt, CreatedBy = "test"
        };
        db.StockReplayRuns.Add(run);
        var original = new ProductionPoolDependency
        {
            Id = 1, ContributorMovementId = 100,
            ConsumerMovementId = 200, StockPostingId = owner.Id
        };
        db.ProductionPoolDependencyRows.Add(original);
        await db.SaveChangesAsync();
        var consumer = new ProductionBalLotMovement
        {
            Uid = 200, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerVersion = 2, LedgerEpochId = 1,
            StockPostingId = owner.Id, MovementDate = EffectiveAt
        };
        Assert.Null(await ProductionDependencyOwnerReader.ValidateForConsumerAsync(
            db, "DEMO", "HQ", consumer, [original]));

        var inverse = new ProductionPoolDependency
        {
            Id = 2, ContributorMovementId = 100,
            ConsumerMovementId = 200, StockPostingId = root.Id,
            ReversesDependencyId = original.Id
        };
        var replacement = new ProductionPoolDependency
        {
            Id = 3, ContributorMovementId = 100,
            ConsumerMovementId = 200, StockPostingId = root.Id
        };
        db.ProductionPoolDependencyRows.AddRange(inverse, replacement);
        await db.SaveChangesAsync();
        Assert.Contains("without one replay effect", await ProductionDependencyOwnerReader
            .ValidateForConsumerAsync(db, "DEMO", "HQ", consumer, [replacement]));
        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            ReplayRunId = run.Id, RootStockPostingId = root.Id,
            BusinessOwnerPostingId = owner.Id,
            EffectKind = ProductionDependencyOwnerReader.EffectKind,
            AffectedEntityType = ProductionDependencyOwnerReader.AffectedEntityType,
            AffectedEntityId = replacement.Id,
            OriginalEntityId = original.Id,
            CreatedAtUtc = EffectiveAt
        });
        await db.SaveChangesAsync();
        Assert.Null(await ProductionDependencyOwnerReader.ValidateForConsumerAsync(
            db, "DEMO", "HQ", consumer, [replacement]));
        var forgedRoot = new ProductionPoolDependency
        {
            Id = replacement.Id,
            ContributorMovementId = replacement.ContributorMovementId,
            ConsumerMovementId = replacement.ConsumerMovementId,
            StockPostingId = 12
        };
        Assert.Contains("incomplete foreign dependency lineage", await ProductionDependencyOwnerReader
            .ValidateForConsumerAsync(db, "DEMO", "HQ", consumer, [forgedRoot]));
    }

    private static StockPosting Posting(long id, long sequence, string documentId) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = 1,
        PostingSequence = sequence, RequestId = Guid.NewGuid(),
        CommandType = "PRODUCTION_DEPENDENCY_TEST",
        RequestFingerprint = new string('A', 64),
        SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
        SourceDocumentId = documentId, SourceDocumentNo = documentId,
        SourceSnapshotHash = new string('B', 64),
        EffectiveAt = sequence == 1 ? EffectiveAt : EffectiveAt.AddDays(-1),
        BusinessDate = sequence == 1 ? EffectiveAt.Date : EffectiveAt.AddDays(-1).Date,
        PeriodKey = "202610", PostedAtUtc = EffectiveAt,
        PostedBy = "test", SealedAtUtc = EffectiveAt
    };
}
