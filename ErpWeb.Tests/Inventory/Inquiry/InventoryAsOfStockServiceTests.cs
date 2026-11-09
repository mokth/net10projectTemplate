using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Tests.Production.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace ErpWeb.Tests.Inventory.Inquiry;
[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class InventoryAsOfStockServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor()).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        _factory = new TestDbContextFactory(options);
    }

    [Fact]
    public async Task Reverses_post_time_inbound_and_caps_at_current_quantity()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.IvBalLocs.Add(new IvBalLoc { Id = 1, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A", WhCode = "W", LocCode = "L", LotNo = "", IStatus = "ACTIVE", StdQty = 100m, RowVersion = [1] });
            db.IvTrxHistories.Add(new IvTrxHistory { CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 1, TrxLineNo = 1,
                TrxDtTime = new DateTime(2026, 1, 1, 15, 0, 0), TrxType = "MR", BatchStatus = "POSTED", ICode = "A",
                ToBalLocId = 1, ToStdQty = 80m });
            await db.SaveChangesAsync();
        }
        await using var read = await _factory.CreateDbContextAsync();
        var result = await new InventoryAsOfStockService().GetAsync(read, "DEMO", "HQ", [1], new DateTime(2026, 1, 1, 10, 30, 0));
        Assert.Equal(100m, result[1].CurrentBaseQty);
        Assert.Equal(20m, result[1].AsOfBaseQty);
        Assert.Equal(20m, result[1].UsableBaseQty);
    }

    [Theory]
    [InlineData("issue", false)]
    [InlineData("issue", true)]
    [InlineData("receipt", false)]
    [InlineData("receipt", true)]
    [InlineData("transfer", false)]
    [InlineData("transfer", true)]
    public async Task V2_as_of_preserves_each_physical_leg_until_its_later_reversal(
        string movement, bool nextDay)
    {
        var (originalAt, reversalAt) = await SeedReversedMovementAsync(movement, nextDay);
        await using var db = await _factory.CreateDbContextAsync();
        var service = new InventoryAsOfStockService();
        var before = await service.GetAsync(db, "DEMO", "HQ", [1, 2], originalAt.AddTicks(-1));
        var during = await service.GetAsync(db, "DEMO", "HQ", [1, 2], originalAt);
        var justBeforeReversal = await service.GetAsync(db, "DEMO", "HQ", [1, 2], reversalAt.AddTicks(-1));
        var after = await service.GetAsync(db, "DEMO", "HQ", [1, 2], reversalAt);

        Assert.Equal(20m, before[1].AsOfBaseQty);
        Assert.Equal(10m, before[2].AsOfBaseQty);
        var expectedFirst = movement == "receipt" ? 24m : 16m;
        var expectedSecond = movement == "transfer" ? 14m : 10m;
        Assert.Equal(expectedFirst, during[1].AsOfBaseQty);
        Assert.Equal(expectedSecond, during[2].AsOfBaseQty);
        Assert.Equal(expectedFirst, justBeforeReversal[1].AsOfBaseQty);
        Assert.Equal(expectedSecond, justBeforeReversal[2].AsOfBaseQty);
        Assert.Equal(20m, after[1].AsOfBaseQty);
        Assert.Equal(10m, after[2].AsOfBaseQty);
    }

    [Fact]
    public async Task V2_as_of_rejects_a_reversal_with_the_wrong_owner()
    {
        var (originalAt, _) = await SeedReversedMovementAsync("issue", nextDay: true);
        await using var db = await _factory.CreateDbContextAsync();
        var changed = await db.StockPostings.Where(p => p.PostingRole == "REVERSAL")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReversesPostingId, (long?)null));
        Assert.Equal(1, changed);
        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            new InventoryAsOfStockService().GetAsync(db, "DEMO", "HQ", [1], originalAt));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
        Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == 1)).StdQty);
    }

    [Fact]
    public async Task V2_as_of_treats_a_reversal_at_the_original_instant_as_cancelled()
    {
        var (originalAt, _) = await SeedReversedMovementAsync("receipt", nextDay: false, sameInstant: true);
        await using var db = await _factory.CreateDbContextAsync();
        var service = new InventoryAsOfStockService();
        Assert.Equal(20m, (await service.GetAsync(db, "DEMO", "HQ", [1],
            originalAt.AddTicks(-1)))[1].AsOfBaseQty);
        Assert.Equal(20m, (await service.GetAsync(db, "DEMO", "HQ", [1],
            originalAt))[1].AsOfBaseQty);
    }

    [Fact]
    public async Task As_of_reversal_uses_the_candidate_posting_sequence()
    {
        var (originalAt, _) = await SeedReversedMovementAsync("receipt", nextDay: false, sameInstant: true);
        await using var db = await _factory.CreateDbContextAsync();
        var before = new StockReplayEventKey(originalAt, StockReplayEventKey.OrdinaryClass,
            0, "", 0);
        var between = new StockReplayEventKey(originalAt, StockReplayEventKey.OrdinaryClass,
            1, "\uffff", int.MaxValue);
        var after = new StockReplayEventKey(originalAt, StockReplayEventKey.OrdinaryClass,
            2, "\uffff", int.MaxValue);
        Assert.Empty(await StockReplayPhysicalAsOfReader.ReadCrossingReversalsAsync(db, "DEMO", "HQ", [1], before, default));
        var crossing = await StockReplayPhysicalAsOfReader.ReadCrossingReversalsAsync(db, "DEMO", "HQ", [1], between, default);
        var reversal = Assert.Single(crossing[1]);
        Assert.Equal(-4m, reversal.SignedDelta);
        Assert.Equal(2, reversal.Key.OriginalPostingSequence);
        Assert.Empty(await StockReplayPhysicalAsOfReader.ReadCrossingReversalsAsync(db, "DEMO", "HQ", [1], after, default));
    }

    private async Task<(DateTime OriginalAt, DateTime ReversalAt)> SeedReversedMovementAsync(
        string movement, bool nextDay, bool sameInstant = false)
    {
        var originalAt = new DateTime(2026, 10, 3, 10, 0, 0);
        var reversalAt = sameInstant ? originalAt : nextDay ? originalAt.AddDays(2) : originalAt.AddHours(1);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await ProductionLedgerTestFixture.SeedActiveEpochAsync(db);
        var epoch = await db.StockLedgerEpochs.SingleAsync();
        db.IvBalLocs.AddRange(
            new IvBalLoc { Id = 1, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A",
                WhCode = "W", LocCode = "L1", IStatus = "ACTIVE", StdQty = 20m, StdUom = "EA" },
            new IvBalLoc { Id = 2, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A",
                WhCode = "W", LocCode = "L2", IStatus = "ACTIVE", StdQty = 10m, StdUom = "EA" });
        var originalOwner = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
            PostingSequence = 1, RequestId = Guid.NewGuid(), SourceModule = "INVENTORY",
            SourceDocumentType = movement, SourceDocumentId = "10", SourceDocumentNo = "10",
            DocumentRevision = 1, PostingRole = "PRIMARY", EffectiveAt = originalAt,
            BusinessDate = originalAt.Date, PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow
        };
        var reverseOwner = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
            PostingSequence = 2, RequestId = Guid.NewGuid(), SourceModule = "INVENTORY",
            SourceDocumentType = movement, SourceDocumentId = "10", SourceDocumentNo = "10",
            DocumentRevision = 1, PostingRole = "REVERSAL", EffectiveAt = reversalAt,
            BusinessDate = reversalAt.Date, PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow,
            ReversesPosting = originalOwner
        };
        db.StockPostings.AddRange(originalOwner, reverseOwner);
        await db.SaveChangesAsync();
        var original = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 10, TrxLineNo = 1,
            TrxType = movement, BatchStatus = IvBatchStatuses.Posted, TrxDtTime = originalAt,
            ICode = "A", IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epoch.Id,
            StockPostingId = originalOwner.Id, PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL",
            FromBalLocId = movement == "receipt" ? null : 1,
            FrStdQty = movement == "receipt" ? null : 4m, FrStdUom = movement == "receipt" ? null : "EA",
            ToBalLocId = movement == "issue" ? null : movement == "receipt" ? 1 : 2,
            ToStdQty = movement == "issue" ? null : 4m, ToStdUom = movement == "issue" ? null : "EA"
        };
        db.IvTrxHistories.Add(original);
        await db.SaveChangesAsync();
        db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 10, TrxLineNo = 1,
            TrxType = movement, BatchStatus = IvBatchStatuses.Posted, TrxDtTime = reversalAt,
            ICode = "A", IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epoch.Id,
            StockPostingId = reverseOwner.Id, PostingLineNo = 1, DocumentRevision = 1,
            EntryRole = "REVERSAL", ReversesHistoryId = original.Id,
            FromBalLocId = original.ToBalLocId, FrStdQty = original.ToStdQty, FrStdUom = original.ToStdUom,
            ToBalLocId = original.FromBalLocId, ToStdQty = original.FrStdQty, ToStdUom = original.FrStdUom
        });
        await db.SaveChangesAsync();
        return (originalAt, reversalAt);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
