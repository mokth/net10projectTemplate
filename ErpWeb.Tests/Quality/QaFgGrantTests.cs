using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.Tests.Production.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaFgGrantTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public QaFgGrantTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Claim_is_noop_when_fg_gate_is_off()
    {
        var (receipt, pools) = SeedReceipt(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
            db, "DEMO", "HQ", receipt, pools);
        Assert.Null(error);
        Assert.Equal(0, await db.QaFgAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_fails_closed_when_gate_is_on_and_no_grant_exists()
    {
        await EnableFgAsync();
        var (receipt, pools) = SeedReceipt(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
            db, "DEMO", "HQ", receipt, pools);
        Assert.NotNull(error);
        Assert.Contains("approved quality inspection", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.QaFgAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_consumes_accepted_qty_and_rejects_over_receipt()
    {
        await EnableFgAsync();
        await SeedGrantAsync(accepted: 6m, rejected: 2m);
        var first = SeedReceipt(qty: 6m, sourceId: 11, batchId: 10);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var error = await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
                db, "DEMO", "HQ", first.Receipt, first.Pools);
            Assert.Null(error);
            await db.SaveChangesAsync();
            Assert.Equal(6m, await db.QaFgAllocations.Where(x => x.Status == QaReceiptAllocationStatuses.Posted).SumAsync(x => x.Qty));
        }

        var over = SeedReceipt(qty: 1m, sourceId: 12, batchId: 11);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var error = await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
                db, "DEMO", "HQ", over.Receipt, over.Pools);
            Assert.NotNull(error);
            Assert.Contains("short", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Restore_reverses_allocation_and_new_generation_can_reclaim()
    {
        await EnableFgAsync();
        await SeedGrantAsync(accepted: 6m, rejected: 0m);
        var first = SeedReceipt(qty: 6m, sourceId: 11, batchId: 10, revision: 1);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Null(await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
                db, "DEMO", "HQ", first.Receipt, first.Pools));
            await db.SaveChangesAsync();
            var qaCase = await db.QaCases.SingleAsync();
            qaCase.Status = QaCaseStatuses.Closed;
            await db.SaveChangesAsync();
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Null(await new QaFgAuthorizationService().RestoreFinishedGoodAsync(db, "DEMO", "HQ", 10));
            await db.SaveChangesAsync();
            Assert.Equal(QaCaseStatuses.Approved, await db.QaCases.Select(x => x.Status).SingleAsync());
            Assert.Equal(0, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Posted));
            Assert.Equal(1, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Reversed));
        }

        var next = SeedReceipt(qty: 6m, sourceId: 22, batchId: 10, revision: 2);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Null(await new QaFgAuthorizationService().ClaimFinishedGoodAsync(
                db, "DEMO", "HQ", next.Receipt, next.Pools));
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Posted && x.SourceId == 22));
            Assert.Equal(1, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Reversed && x.SourceId == 11));
        }
    }

    [Fact]
    public async Task Padj_blocks_fg_staging_when_gate_is_on_and_allows_process_wip()
    {
        await EnableFgAsync();
        var staging = new ProductionBalLot { Uid = 1, BalanceStage = "FG_STAGING", Kind = "WIP", ItemCode = "FG" };
        var wip = new ProductionBalLot { Uid = 2, BalanceStage = "PROCESS_WIP", Kind = "WIP", ItemCode = "WIP" };
        await using var db = await _factory.CreateDbContextAsync();
        var auth = new QaFgAuthorizationService();
        var blocked = await auth.AssertWipAdjustmentAllowedAsync(db, "DEMO", [staging]);
        Assert.NotNull(blocked);
        Assert.Contains("cannot consume FG staging", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await auth.AssertWipAdjustmentAllowedAsync(db, "DEMO", [wip]));
    }

    [Fact]
    public async Task Fg_post_is_unchanged_when_gate_is_off()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = SqliteFactory(connection);
        var fixture = await FinishedGoodReceiptSqlServerTests.CreateFixtureAsync(
            factory, qaFg: new QaFgAuthorizationService());
        var saved = await fixture.Service.SaveAsync(fixture.Draft(6m));
        Assert.True(saved.Succeeded, saved.Message);
        await StampRowVersionAsync(factory, saved.Data!.Id);
        var draft = await fixture.Service.GetAsync(saved.Data.Id);
        var posted = await fixture.Service.PostAsync(new(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        await using var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync();
        Assert.Equal(0, await db.QaFgAllocations.CountAsync());
    }

    [Fact]
    public async Task Fg_post_fails_closed_when_gate_is_on_without_grant()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = SqliteFactory(connection);
        var fixture = await FinishedGoodReceiptSqlServerTests.CreateFixtureAsync(
            factory, qaFg: new QaFgAuthorizationService());
        await EnableFgAsync(factory, fixture.Company);
        var saved = await fixture.Service.SaveAsync(fixture.Draft(6m));
        Assert.True(saved.Succeeded, saved.Message);
        await StampRowVersionAsync(factory, saved.Data!.Id);
        var draft = await fixture.Service.GetAsync(saved.Data.Id);
        var posted = await fixture.Service.PostAsync(new(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid()));
        Assert.False(posted.Succeeded);
        Assert.Contains("approved quality inspection", posted.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync();
        Assert.Equal(0, await db.QaFgAllocations.CountAsync());
        Assert.Equal("NEW", (await fixture.Service.GetAsync(saved.Data.Id)).Data!.Status);
    }

    [Fact]
    public async Task Fg_rollback_reverses_grant_and_new_generation_can_repost()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = SqliteFactory(connection);
        var fixture = await FinishedGoodReceiptSqlServerTests.CreateFixtureAsync(
            factory, valueInventory: true, qaFg: new QaFgAuthorizationService());
        await EnableFgAsync(factory, fixture.Company);
        await SeedGrantAsync(factory, fixture.Company, fixture.PoolId, accepted: 6m);
        var saved = await fixture.Service.SaveAsync(fixture.Draft(6m));
        Assert.True(saved.Succeeded, saved.Message);
        await StampRowVersionAsync(factory, saved.Data!.Id);
        var draft = await fixture.Service.GetAsync(saved.Data.Id);
        var posted = await fixture.Service.PostAsync(new(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        long originalSourceId;
        await using (var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync())
        {
            var allocation = Assert.Single(await db.QaFgAllocations.Where(x => x.Status == QaReceiptAllocationStatuses.Posted).ToListAsync());
            originalSourceId = allocation.SourceId;
            Assert.Equal(6m, allocation.Qty);
        }

        var rolled = await fixture.Service.RollbackAsync(new(posted.Data!.Id, posted.Data.RowVersion, Guid.NewGuid(), "QA reissue"));
        Assert.True(rolled.Succeeded, rolled.Message);
        await using (var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync())
        {
            Assert.Equal(0, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Posted));
            var reversed = Assert.Single(await db.QaFgAllocations.Where(x => x.Status == QaReceiptAllocationStatuses.Reversed).ToListAsync());
            Assert.Equal(originalSourceId, reversed.SourceId);
        }

        await StampRowVersionAsync(factory, rolled.Data!.Id);
        var nextDraft = await fixture.Service.GetAsync(rolled.Data.Id);
        var reposted = await fixture.Service.PostAsync(new(rolled.Data.Id, nextDraft.Data!.RowVersion, Guid.NewGuid()));
        Assert.True(reposted.Succeeded, reposted.Message);
        await using (var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync())
        {
            var postedAlloc = Assert.Single(await db.QaFgAllocations.Where(x => x.Status == QaReceiptAllocationStatuses.Posted).ToListAsync());
            Assert.NotEqual(originalSourceId, postedAlloc.SourceId);
            Assert.Equal(1, await db.QaFgAllocations.CountAsync(x => x.Status == QaReceiptAllocationStatuses.Reversed && x.SourceId == originalSourceId));
        }
    }

    private async Task EnableFgAsync() => await EnableFgAsync(_factory, "DEMO");

    private static async Task EnableFgAsync(IDbContextFactory<AppDbContext> factory, string company)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = company,
            Version = 1,
            Enabled = true,
            FinalFgInspectionEnabled = true,
            IsPublished = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await db.SaveChangesAsync();
    }

    private Task SeedGrantAsync(decimal accepted, decimal rejected) =>
        SeedGrantAsync(_factory, "DEMO", 44, accepted, rejected);

    private static async Task SeedGrantAsync(
        IDbContextFactory<AppDbContext> factory,
        string company,
        long poolId,
        decimal accepted,
        decimal rejected = 0m)
    {
        await using var db = await factory.CreateDbContextAsync();
        var qaCase = new QaCase
        {
            CompanyCode = company,
            BranchCode = "HQ",
            CaseNo = "QC00000001",
            CaseDate = DateTime.Today,
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.FgStagingLot,
            WorkOrderNo = "WO-FG-HQ",
            ProductionBalLotId = poolId,
            EligibilityNote = QaEligibilityNotes.FgGrant,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        };
        var line = new QaCaseLine
        {
            LineNo = 1,
            ItemCode = "FG",
            Uom = "EA",
            Qty = accepted + rejected,
            LotNo = "PROD-1"
        };
        qaCase.Lines.Add(line);
        qaCase.FgGrants.Add(new QaFgGrant
        {
            CompanyCode = company,
            BranchCode = "HQ",
            Case = qaCase,
            CaseLine = line,
            ProductionBalLotId = poolId,
            LineKey = 1,
            WorkOrderNo = "WO-FG-HQ",
            ItemCode = "FG",
            Uom = "EA",
            LotNo = "PROD-1",
            AcceptedQty = accepted,
            RejectedQty = rejected,
            Status = QaReceiptGrantStatuses.Open,
            ApprovedAtUtc = DateTime.UtcNow
        });
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

    private static (ProductionFinishedGoodReceipt Receipt, Dictionary<long, ProductionBalLot> Pools) SeedReceipt(
        decimal qty,
        long sourceId = 1,
        int batchId = 1,
        int revision = 1)
    {
        var pool = new ProductionBalLot
        {
            Uid = 44,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ItemCode = "FG",
            BaseUom = "EA",
            Uom = "EA",
            WorkOrderNo = "WO1",
            LotNo = "PROD-1",
            PhysicalLotNo = "PROD-1",
            BalanceStage = "FG_STAGING"
        };
        var receipt = new ProductionFinishedGoodReceipt
        {
            BatchId = batchId,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocumentRevision = revision
        };
        receipt.Sources.Add(new ProductionFinishedGoodSource
        {
            Id = sourceId,
            BatchId = batchId,
            DocumentRevision = revision,
            ProductionBalLotId = 44,
            BaseQty = qty,
            RequestedQty = qty,
            BaseUom = "EA",
            SourceUom = "EA"
        });
        return (receipt, new Dictionary<long, ProductionBalLot> { [44] = pool });
    }

    private static TestDbContextFactory SqliteFactory(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

    private static async Task StampRowVersionAsync(IDbContextFactory<AppDbContext> factory, int batchId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE PrFinishedGoodReceipt SET RowVersion = X'01' WHERE BatchId = {batchId};");
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
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
