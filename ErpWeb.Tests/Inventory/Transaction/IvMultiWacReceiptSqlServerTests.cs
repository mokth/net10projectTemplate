using System.Data.Common;
using System.Text.RegularExpressions;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ErpWeb.Tests.Inventory.Transaction;

public sealed class MultiWacReceiptSqlFactAttribute : FactAttribute
{
    public MultiWacReceiptSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IvMultiWacReceiptSqlServerTests.ConnectionEnvironment)))
            Skip = "Requires a disposable multi-receipt SQL Server database.";
    }
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class IvMultiWacReceiptSqlServerTests
{
    internal const string ConnectionEnvironment = "ERPWEB_MULTI_WAC_SQL_TEST_CONNECTION";
    private static readonly DateTime Today = new(2026, 8, 26);

    [MultiWacReceiptSqlFact]
    public Task Concurrent_multi_item_post_and_rollback_apply_each_line_once() => RunAsync(sameItem: false);

    [MultiWacReceiptSqlFact]
    public Task Concurrent_same_item_post_and_rollback_apply_each_line_once() => RunAsync(sameItem: true);

    [MultiWacReceiptSqlFact]
    public Task Concurrent_repeated_and_other_items_post_and_rollback_apply_each_line_once() =>
        RunAsync(sameItem: false, repeatFirstItem: true);

    [MultiWacReceiptSqlFact]
    public Task Same_item_receipt_rejects_draft_edit_after_planning() => RunAsync(sameItem: true, editRace: true);

    [MultiWacReceiptSqlFact]
    public Task Repeated_and_other_items_reject_draft_edit_after_planning() =>
        RunAsync(sameItem: false, editRace: true, repeatFirstItem: true);

    [MultiWacReceiptSqlFact]
    public Task Repeated_and_other_items_roll_back_database_failures_atomically() =>
        RunAsync(sameItem: false, repeatFirstItem: true, injectFailure: true);

    private static async Task RunAsync(bool sameItem, bool editRace = false,
        bool repeatFirstItem = false, bool injectFailure = false)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironment)
            ?? throw new InvalidOperationException("An isolated SQL Server connection is required.");
        var connectionInfo = new SqlConnectionStringBuilder(connectionString);
        if (!Regex.IsMatch(connectionInfo.InitialCatalog,
                @"\ACodex_ReplayNeutral_Test_[0-9a-f]{12}\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The SQL test requires a dedicated, randomly named scratch database.");

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
                throw new InvalidOperationException("The scratch database must be empty before this test.");
        }

        var barrier = new ReceiptBatchLockBarrier();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(barrier).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
            db.IvLocations.Add(new IvLocation { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true });
            db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
            db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
            db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
            db.IvStockMasters.AddRange(
                new IvStockMaster { CompanyCode = "DEMO", ICode = "A100", IDesc = "Item A", IClassCode = "RAW", StdUom = "EA", IsActive = true },
                new IvStockMaster { CompanyCode = "DEMO", ICode = "B100", IDesc = "Item B", IClassCode = "RAW", StdUom = "EA", IsActive = true });
            db.IvBalLocs.AddRange(
                new IvBalLoc { CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100", WhCode = "MAIN", LocCode = "BIN1", IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA", TransDate = Today.AddDays(-5) },
                new IvBalLoc { CompanyCode = "DEMO", BranchCode = "HQ", ICode = "B100", WhCode = "MAIN", LocCode = "BIN1", IStatus = "ACTIVE", StdQty = 10m, StdUom = "EA", TransDate = Today.AddDays(-5) });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 15m, 3m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "B100", 10m, 4m);
        }

        Dictionary<string, int> piles;
        await using (var db = await factory.CreateDbContextAsync())
            piles = await db.IvBalLocs.ToDictionaryAsync(b => b.ICode, b => b.Id);
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(factory, Today);
        var issueBatches = new Dictionary<string, int>();
        foreach (var (item, quantity, cost) in new[] { ("A100", 8m, 3m), ("B100", 4m, 4m) })
        {
            var draft = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = Today,
                Lines = [new IvMiscIssueLineRequest { FromBalLocId = piles[item], ICode = item,
                    FrWarehouse = "MAIN", FrLocation = "BIN1", FrLotNo = string.Empty,
                    Quantity = quantity, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = cost }]
            });
            Assert.True(draft.Succeeded, draft.ErrorMessage);
            var posted = await issue.PostAsync([draft.BatchNo]);
            Assert.True(posted.Succeeded, posted.ErrorMessage);
            issueBatches[item] = draft.BatchNo;
        }

        var first = InventoryLedgerTestFixture.CreateMiscReceipt(factory, Today, enableBackdatedReplay: true);
        var second = InventoryLedgerTestFixture.CreateMiscReceipt(factory, Today, enableBackdatedReplay: true);
        IReadOnlyList<IvMiscReceiptLineRequest> receiptLines = repeatFirstItem
            ? [
                new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 2m, UnitPrice = 9m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" },
                new IvMiscReceiptLineRequest { ICode = "B100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 5m, UnitPrice = 10m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" },
                new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 3m, UnitPrice = 11m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" }
              ]
            : sameItem
            ? [
                new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 2m, UnitPrice = 9m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" },
                new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 3m, UnitPrice = 11m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" }
              ]
            : [
                new IvMiscReceiptLineRequest { ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 5m, UnitPrice = 9m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" },
                new IvMiscReceiptLineRequest { ICode = "B100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 5m, UnitPrice = 10m, PriceConfirmed = true, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "ADJ" }
              ];
        var receipt = await first.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = Today.AddDays(-2),
            Lines = receiptLines
        });
        Assert.True(receipt.Succeeded, receipt.ErrorMessage);

        if (injectFailure)
        {
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE [StockValuationFact] ADD CONSTRAINT [TestRejectMixedPost] "
                    + "CHECK ([ItemCode] <> N'B100' OR [MovementCode] <> N'RECEIPT_IN')");
            var rejectedPost = await first.PostAsync([receipt.BatchNo]);
            Assert.False(rejectedPost.Succeeded);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receipt.BatchNo)).BatchStatus);
                Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receipt.BatchNo).ToArrayAsync());
                Assert.Empty(await db.StockPostings.Where(p => p.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt
                    && p.SourceDocumentNo == receipt.BatchNo.ToString()).ToArrayAsync());
                var stateA = await db.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
                var stateB = await db.StockCostStates.SingleAsync(s => s.ItemCode == "B100");
                Assert.Equal((7m, 21m), (stateA.OnHandBaseQty, stateA.InventoryValue));
                Assert.Equal((6m, 24m), (stateB.OnHandBaseQty, stateB.InventoryValue));
                Assert.Equal(7m, (await db.IvBalLocs.SingleAsync(b => b.Id == piles["A100"])).StdQty);
                Assert.Equal(6m, (await db.IvBalLocs.SingleAsync(b => b.Id == piles["B100"])).StdQty);
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE [StockValuationFact] DROP CONSTRAINT [TestRejectMixedPost]");
            }
            var posted = await first.PostAsync([receipt.BatchNo]);
            Assert.True(posted.Succeeded, posted.ErrorMessage);
            await AssertReceiptStateAsync(IvBatchStatuses.Posted, 12m, 57.6m, 38.4m, 11m, 66m, 24m, "PRIMARY");
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE [StockValuationFact] ADD CONSTRAINT [TestRejectMixedRollback] "
                    + "CHECK ([ItemCode] <> N'B100' OR [MovementCode] <> N'RECEIPT_IN' OR [ValuationStatus] <> N'REVERSED')");
            var rejectedRollback = await first.RollbackAsync([receipt.BatchNo]);
            Assert.False(rejectedRollback.Succeeded);
            await AssertReceiptStateAsync(IvBatchStatuses.Posted, 12m, 57.6m, 38.4m, 11m, 66m, 24m, "PRIMARY");
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receipt.BatchNo && h.EntryRole == "REVERSAL").ToArrayAsync());
                Assert.Empty(await db.StockPostings.Where(p => p.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt
                    && p.SourceDocumentNo == receipt.BatchNo.ToString() && p.PostingRole == "REVERSAL").ToArrayAsync());
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE [StockValuationFact] DROP CONSTRAINT [TestRejectMixedRollback]");
            }
            var rolledBack = await first.RollbackAsync([receipt.BatchNo]);
            Assert.True(rolledBack.Succeeded, rolledBack.ErrorMessage);
            await AssertReceiptStateAsync(IvBatchStatuses.New, 7m, 21m, 24m, 6m, 24m, 16m, "REVERSAL");
            return;
        }

        if (editRace)
        {
            barrier.Arm();
            var pending = first.PostAsync([receipt.BatchNo]);
            try
            {
                var reached = await Task.WhenAny(barrier.Reached, pending, Task.Delay(TimeSpan.FromSeconds(30)));
                if (reached == pending)
                {
                    var ended = await pending;
                    Assert.Fail($"The request ended before the draft-edit checkpoint: {ended.ErrorMessage}");
                }
                Assert.True(reached == barrier.Reached, "The draft-edit checkpoint was not reached.");
                await using var editor = await factory.CreateDbContextAsync();
                var detail = await editor.IvTrxBatchDetails.Where(d => d.BatchNo == receipt.BatchNo)
                    .OrderBy(d => d.TrxLineNo).FirstAsync();
                detail.Remarks = "Edited after replay planning";
                await editor.SaveChangesAsync();
            }
            finally
            {
                barrier.Release();
            }
            var stale = await pending;
            Assert.False(stale.Succeeded);
            Assert.Contains("changed", stale.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            await using (var unchanged = await factory.CreateDbContextAsync())
            {
                Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receipt.BatchNo)).BatchStatus);
                Assert.Empty(await unchanged.IvTrxHistories.Where(h => h.BatchNo == receipt.BatchNo).ToArrayAsync());
                var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
                Assert.Equal((7m, 21m), (state.OnHandBaseQty, state.InventoryValue));
                Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == piles["A100"])).StdQty);
            }
            var retried = await first.PostAsync([receipt.BatchNo]);
            Assert.True(retried.Succeeded, retried.ErrorMessage);
            await AssertReceiptStateAsync(IvBatchStatuses.Posted, 12m, 57.6m, 38.4m,
                sameItem ? 6m : 11m, sameItem ? 24m : 66m, sameItem ? 16m : 24m, "PRIMARY");
            var rolledBack = await first.RollbackAsync([receipt.BatchNo]);
            Assert.True(rolledBack.Succeeded, rolledBack.ErrorMessage);
            await AssertReceiptStateAsync(IvBatchStatuses.New, 7m, 21m, 24m, 6m, 24m, 16m, "REVERSAL");
            return;
        }

        var posts = await RunContendedAsync(
            () => first.PostAsync([receipt.BatchNo]), () => second.PostAsync([receipt.BatchNo]));
        Assert.Contains(posts, result => result.Succeeded);
        await AssertReceiptStateAsync(IvBatchStatuses.Posted, 12m, sameItem || repeatFirstItem ? 57.6m : 54m,
            sameItem || repeatFirstItem ? 38.4m : 36m, sameItem ? 6m : 11m, sameItem ? 24m : 66m,
            sameItem ? 16m : 24m, "PRIMARY");

        var rollbacks = await RunContendedAsync(
            () => first.RollbackAsync([receipt.BatchNo]), () => second.RollbackAsync([receipt.BatchNo]));
        Assert.Contains(rollbacks, result => result.Succeeded);
        await AssertReceiptStateAsync(IvBatchStatuses.New, 7m, 21m, 24m, 6m, 24m, 16m, "REVERSAL");
        return;

        async Task<IvMiscReceiptOperationResult[]> RunContendedAsync(
            Func<Task<IvMiscReceiptOperationResult>> firstRequest,
            Func<Task<IvMiscReceiptOperationResult>> secondRequest)
        {
            barrier.Arm();
            var firstTask = firstRequest();
            Task<IvMiscReceiptOperationResult>? secondTask = null;
            try
            {
                var reached = await Task.WhenAny(barrier.Reached, firstTask, Task.Delay(TimeSpan.FromSeconds(30)));
                if (reached == firstTask)
                {
                    var ended = await firstTask;
                    Assert.Fail($"The first request ended before the lock checkpoint: {ended.ErrorMessage}");
                }
                Assert.True(reached == barrier.Reached, "The first request did not reach the locked receipt.");
                await using (var probe = new SqlConnection(connectionString))
                {
                    await probe.OpenAsync();
                    await using var transaction = await probe.BeginTransactionAsync();
                    await using var command = probe.CreateCommand();
                    command.Transaction = (SqlTransaction)transaction;
                    command.CommandText = "SELECT APPLOCK_TEST('public', @resource, 'Exclusive', 'Transaction')";
                    command.Parameters.AddWithValue("@resource", BranchStockTransactionLock.ResourceName("DEMO", "HQ"));
                    Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
                }
                secondTask = secondRequest();
                await Task.Delay(750);
                Assert.False(secondTask.IsCompleted, "The competing request finished while the branch lock was held.");
            }
            finally
            {
                barrier.Release();
            }
            Assert.NotNull(secondTask);
            return await Task.WhenAll(firstTask, secondTask);
        }

        async Task AssertReceiptStateAsync(string status, decimal qtyA, decimal valueA, decimal issueValueA,
            decimal qtyB, decimal valueB, decimal issueValueB, string postingRole)
        {
            await using var db = await factory.CreateDbContextAsync();
            var batch = await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receipt.BatchNo);
            Assert.Equal(status, batch.BatchStatus);
            Assert.Equal(1, batch.PostedCount);
            Assert.Equal(postingRole == "REVERSAL" ? 1 : 0, batch.RollbackCount);
            var stateA = await db.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
            var stateB = await db.StockCostStates.SingleAsync(s => s.ItemCode == "B100");
            Assert.Equal((qtyA, valueA), (stateA.OnHandBaseQty, stateA.InventoryValue));
            Assert.Equal((qtyB, valueB), (stateB.OnHandBaseQty, stateB.InventoryValue));
            Assert.Equal(qtyA, (await db.IvBalLocs.SingleAsync(b => b.Id == piles["A100"])).StdQty);
            Assert.Equal(qtyB, (await db.IvBalLocs.SingleAsync(b => b.Id == piles["B100"])).StdQty);
            var issueA = issueBatches["A100"];
            var issueB = issueBatches["B100"];
            var issueHistories = await db.IvTrxHistories.Where(h => h.EntryRole == "ORIGINAL"
                && (h.BatchNo == issueA || h.BatchNo == issueB)).ToArrayAsync();
            Assert.Equal(2, issueHistories.Length);
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(db, "DEMO", "HQ", issueHistories);
            Assert.Equal(issueValueA, active[issueHistories.Single(h => h.ICode == "A100").Id].Sum(f => f.CostAmount));
            Assert.Equal(issueValueB, active[issueHistories.Single(h => h.ICode == "B100").Id].Sum(f => f.CostAmount));
            var root = Assert.Single(await db.StockPostings.Where(p => p.CompanyCode == "DEMO" && p.BranchCode == "HQ"
                && p.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt && p.SourceDocumentNo == receipt.BatchNo.ToString()
                && p.PostingRole == postingRole).ToArrayAsync());
            Assert.NotNull(root.SealedAtUtc);
            var role = postingRole == "PRIMARY" ? "ORIGINAL" : "REVERSAL";
            Assert.Equal(repeatFirstItem ? 3 : 2,
                await db.IvTrxHistories.CountAsync(h => h.StockPostingId == root.Id && h.EntryRole == role));
        }
    }

    private sealed class ReceiptBatchLockBarrier : DbCommandInterceptor
    {
        private int _armed;
        private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Arm()
        {
            _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK)", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            }
            return result;
        }
    }
}
