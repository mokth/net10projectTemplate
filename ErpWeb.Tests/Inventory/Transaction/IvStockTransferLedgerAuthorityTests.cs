using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryTransfer)]
public sealed class IvStockTransferLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockTransferLedgerAuthorityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        foreach (var loc in new[] { "BIN1", "BIN2" })
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                WarehouseCode = "MAIN",
                LocCode = loc,
                IsActive = true
            });
        }

        db.MsUoms.Add(new MsUom
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IClassCode = "RAW",
            IsActive = true
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Dated_on_hand_lookup_uses_historical_quantity_after_later_issue()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.IvBalLocs.SingleAsync(b => b.Id == sourceId)).TransDate
                = FixedToday.AddDays(-1);
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = sourceId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 15m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.CompanyCode)
            .Returns(InventoryLedgerTestFixture.CompanyCode);
        current.SetupGet(x => x.BranchCode)
            .Returns(InventoryLedgerTestFixture.BranchCode);
        var lookups = new IvInventoryLookupService(current.Object,
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory), _factory);
        var historical = await lookups.SearchOnHandAsync(
            new IvOnHandSearchRequest
            {
                ICode = "A100", AsOfDate = FixedToday.AddDays(-1)
            });
        Assert.True(historical.Succeeded, historical.ErrorMessage);
        Assert.Equal(1, historical.TotalCount);
        Assert.Equal(15m, Assert.Single(historical.Rows).StdQty);
        var currentDay = await lookups.SearchOnHandAsync(
            new IvOnHandSearchRequest
            {
                ICode = "A100", AsOfDate = FixedToday
            });
        Assert.True(currentDay.Succeeded, currentDay.ErrorMessage);
        Assert.Equal(0, currentDay.TotalCount);
        var beforeEpoch = await lookups.SearchOnHandAsync(
            new IvOnHandSearchRequest
            {
                ICode = "A100", AsOfDate = new DateTime(2020, 1, 1)
            });
        Assert.False(beforeEpoch.Succeeded);
    }

    [Fact]
    public async Task Moving_average_transfer_is_value_neutral_and_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(BalanceReferencePrice, (await db.IvTrxBatchDetails.SingleAsync()).UnitPrice);

        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Direction).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(20m, facts.Single(x => x.MovementCode == "TRANSFER_OUT").CostAmount);
        Assert.Equal(20m, facts.Single(x => x.MovementCode == "TRANSFER_IN").CostAmount);
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
        });
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    [Fact]
    public async Task Fifo_transfer_draft_binds_to_two_existing_piles()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday);
        var saved = await transfer.SaveNewAsync(Request(sourceId, 1m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using var verify = await _factory.CreateDbContextAsync();
        var destinationId = await verify.IvBalLocs.AsNoTracking()
            .Where(b => b.LocCode == "BIN2")
            .Select(b => b.Id).SingleAsync();
        var authority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
            sourceId, destinationId);
        Assert.Equal(1m, authority.Quantity);
        var nextSequence = await verify.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var key = new StockReplayEventKey(FixedToday,
            StockReplayEventKey.OrdinaryClass, nextSequence,
            "H9999999999", 0);
        var source = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, sourceId,
            new StockReplayPhysicalEvent(999_999, key, -1m));
        var destination = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            new StockReplayPhysicalEvent(999_999, key, 1m));
        var plan = await StockReplayFifoTransferInsertionPlanner.BuildDraftAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
            source, destination);
        StockReplayFifoTransferInsertionPlanner.ValidatePreparedPlan(plan);
        Assert.Equal(authority, plan.DraftAuthority);
        Assert.Equal(14m, plan.Source.Projection.FinalQuantity);
        Assert.Equal(1m, plan.Destination.Projection.FinalQuantity);
        Assert.Equal(15m, plan.Projection.FinalQuantity);
        Assert.Equal(55m, plan.Projection.FinalValue);
        await using (var rootDb = await _factory.CreateDbContextAsync())
        await using (var transaction = await rootDb.Database.BeginTransactionAsync())
        {
            var command = await InventoryStockPostingCommandFactory.CreateAsync(
                rootDb, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
                false, FixedToday, CancellationToken.None);
            var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
            Assert.NotNull(begin.Context);
            try
            {
                await StockReplayFifoTransferInsertionPlanner.ValidateWithinRootAsync(
                    begin.Context!, plan);
                var changedDetail = await rootDb.IvTrxBatchDetails
                    .SingleAsync(d => d.BatchId == authority.BatchId);
                changedDetail.FrStdQty = 2m;
                changedDetail.ToStdQty = 2m;
                await rootDb.SaveChangesAsync();
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    (await Assert.ThrowsAsync<StockLedgerException>(() =>
                        StockReplayFifoTransferInsertionPlanner
                            .ValidateWithinRootAsync(begin.Context!, plan)))
                    .Error.Code);
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(begin.Context!);
                await transaction.RollbackAsync();
            }
        }
        var wrongSource = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, sourceId,
            new StockReplayPhysicalEvent(999_999, key, -2m));
        var wrongDestination = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            new StockReplayPhysicalEvent(999_999, key, 2m));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            (await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoTransferInsertionPlanner.BuildDraftAsync(
                    verify, InventoryLedgerTestFixture.CompanyCode,
                    InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
                    wrongSource, wrongDestination))).Error.Code);
        await using (var rootDb = await _factory.CreateDbContextAsync())
        await using (var transaction = await rootDb.Database.BeginTransactionAsync())
        {
            var command = await InventoryStockPostingCommandFactory.CreateAsync(
                rootDb, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
                false, FixedToday, CancellationToken.None);
            var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
            Assert.NotNull(begin.Context);
            var committed = false;
            try
            {
                var written = await StockReplayFifoTransferRootWriter.InsertAsync(
                    begin.Context!, plan, saved.BatchNo);
                var history = await rootDb.IvTrxHistories.AsNoTracking()
                    .SingleAsync(h => h.Id == written.RootHistoryId);
                Assert.Equal(sourceId, history.FromBalLocId);
                Assert.Equal(destinationId, history.ToBalLocId);
                Assert.Equal(begin.Context!.Posting.Id, history.StockPostingId);
                Assert.Equal(14m, (await rootDb.IvBalLocs.AsNoTracking()
                    .SingleAsync(b => b.Id == sourceId)).StdQty);
                Assert.Equal(1m, (await rootDb.IvBalLocs.AsNoTracking()
                    .SingleAsync(b => b.Id == destinationId)).StdQty);
                await rootDb.SaveChangesAsync();
                var facts = await rootDb.StockValuationFacts.AsNoTracking()
                    .Where(f => f.InventoryHistoryId == written.RootHistoryId)
                    .ToArrayAsync();
                Assert.Equal(3m, facts.Single(f =>
                    f.MovementCode == "TRANSFER_OUT").CostAmount);
                Assert.Equal(3m, facts.Single(f =>
                    f.MovementCode == "TRANSFER_IN").CostAmount);
                Assert.Equal(1, await rootDb.StockFifoLayerConsumptions
                    .CountAsync(c => c.IssueValuationFact
                        .InventoryHistoryId == written.RootHistoryId));
                await coordinator.CompleteReplayInTransactionAsync(
                    begin.Context!, written.RequiredActiveHistoryIds);
                await StockReplayFifoTransferReconciliationGate
                    .VerifySealedWithinRootAsync(
                        begin.Context!, plan, written.RootHistoryId);
                var sealedSnapshot = await StockReplayFifoActiveLayerReader
                    .ReadAsync(rootDb, InventoryLedgerTestFixture.CompanyCode,
                        InventoryLedgerTestFixture.BranchCode, "A100");
                Assert.Equal(15m, sealedSnapshot.CurrentQuantity);
                Assert.Equal(55m, sealedSnapshot.CurrentValue);
                Assert.Equal(3m, Assert.Single(sealedSnapshot.ActiveConsumptions
                    .Where(c => c.IssueKey.LineIdentity ==
                        $"H{written.RootHistoryId:D10}")).Value);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(begin.Context!);
                    await transaction.RollbackAsync();
                }
            }
        }
        await using var committedDb = await _factory.CreateDbContextAsync();
        var committedSnapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            committedDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, committedSnapshot.CurrentQuantity);
        Assert.Equal(55m, committedSnapshot.CurrentValue);
        Assert.Single(committedSnapshot.ActiveConsumptions);
        var postedHistoryId = await committedDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).SingleAsync();
        var postedKey = committedSnapshot.ActiveConsumptions[0].IssueKey;
        var sourceRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            committedDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, sourceId,
            new StockReplayPhysicalEvent(postedHistoryId, postedKey, -1m));
        var destinationRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            committedDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            new StockReplayPhysicalEvent(postedHistoryId, postedKey, 1m));
        var removalPlan = await StockReplayFifoTransferRemovalPlanner.BuildAsync(
            committedDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", sourceRemoval,
            destinationRemoval);
        await using (var removalDb = await _factory.CreateDbContextAsync())
        await using (var transaction = await removalDb.Database.BeginTransactionAsync())
        {
            var command = await InventoryStockPostingCommandFactory.CreateAsync(
                removalDb, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, saved.BatchNo,
                true, FixedToday, CancellationToken.None);
            var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var begin = await coordinator.BeginInTransactionAsync(removalDb, command);
            Assert.NotNull(begin.Context);
            var committed = false;
            try
            {
                var written = await StockReplayFifoTransferRemovalRootWriter
                    .RemoveAsync(begin.Context!, removalPlan, saved.BatchNo);
                Assert.Empty(written.RequiredActiveHistoryIds);
                await removalDb.SaveChangesAsync();
                await coordinator.CompleteReplayInTransactionAsync(
                    begin.Context!, written.RequiredActiveHistoryIds);
                await StockReplayFifoTransferReconciliationGate
                    .VerifySealedWithinRootAsync(begin.Context!, removalPlan);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(begin.Context!);
                    await transaction.RollbackAsync();
                }
            }
        }
        await using var restoredDb = await _factory.CreateDbContextAsync();
        var restoredSnapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            restoredDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, restoredSnapshot.CurrentQuantity);
        Assert.Equal(55m, restoredSnapshot.CurrentValue);
        Assert.Empty(restoredSnapshot.ActiveConsumptions);
        Assert.Equal(15m, (await restoredDb.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == sourceId)).StdQty);
        Assert.Equal(0m, (await restoredDb.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == destinationId)).StdQty);
    }

    [Fact]
    public async Task Fifo_backdated_transfer_preserves_a_later_transfer_and_both_piles()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday);
        var later = await transfer.SaveNewAsync(Request(sourceId, 8m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPost = await transfer.PostAsync([later.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);
        var earlierRequest = Request(sourceId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        await using var before = await _factory.CreateDbContextAsync();
        var destinationId = await before.IvBalLocs.AsNoTracking()
            .Where(b => b.LocCode == "BIN2")
            .Select(b => b.Id).SingleAsync();
        var nextSequence = await before.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var key = new StockReplayEventKey(earlierRequest.TrxDate,
            StockReplayEventKey.OrdinaryClass, nextSequence,
            "H9999999999", 0);
        var source = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            before, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, sourceId,
            new StockReplayPhysicalEvent(999_999, key, -1m));
        var destination = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            before, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            new StockReplayPhysicalEvent(999_999, key, 1m));
        var plan = await StockReplayFifoTransferInsertionPlanner.BuildDraftAsync(
            before, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            source, destination);
        Assert.Equal(6m, plan.Source.Projection.FinalQuantity);
        Assert.Equal(9m, plan.Destination.Projection.FinalQuantity);
        Assert.Equal(15m, plan.Projection.FinalQuantity);
        Assert.Equal(55m, plan.Projection.FinalValue);
        Assert.Empty(StockReplayFifoMiRootWriter.ChangedIssues(plan.Projection));
        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            false, earlierRequest.TrxDate, CancellationToken.None);
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        try
        {
            var written = await StockReplayFifoTransferRootWriter.InsertAsync(
                begin.Context!, plan, earlier.BatchNo);
            await rootDb.SaveChangesAsync();
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, written.RequiredActiveHistoryIds);
            await StockReplayFifoTransferReconciliationGate
                .VerifySealedWithinRootAsync(
                    begin.Context!, plan, written.RootHistoryId);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(begin.Context!);
                await transaction.RollbackAsync();
            }
        }
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(6m, (await after.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == sourceId)).StdQty);
        Assert.Equal(9m, (await after.IvBalLocs.AsNoTracking()
            .SingleAsync(b => b.Id == destinationId)).StdQty);
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            after, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, snapshot.CurrentQuantity);
        Assert.Equal(55m, snapshot.CurrentValue);
        Assert.Equal(2, snapshot.ActiveConsumptions
            .Select(c => c.IssueKey).Distinct().Count());
        Assert.All(snapshot.ActiveConsumptions,
            c => Assert.False(c.DepletesPool));
    }

    [Fact]
    public async Task Backdated_fifo_transfer_posts_and_rolls_back_before_later_transfer()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await transfer.SaveNewAsync(Request(sourceId, 8m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await transfer.PostAsync([later.BatchNo])).Succeeded);
        var earlierRequest = Request(sourceId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var post = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(9m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                verify, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, "A100");
            Assert.Equal(15m, snapshot.CurrentQuantity);
            Assert.Equal(55m, snapshot.CurrentValue);
        }
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(8m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(
            restored, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, final.CurrentQuantity);
        Assert.Equal(55m, final.CurrentValue);
        var repost = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        var secondRollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(secondRollback.Succeeded, secondRollback.ErrorMessage);
        await using var twiceRestored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await twiceRestored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(8m, (await twiceRestored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
    }

    [Fact]
    public async Task Backdated_wac_transfer_posts_rolls_back_and_reposts_before_later_transfer()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await transfer.SaveNewAsync(Request(sourceId, 8m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await transfer.PostAsync([later.BatchNo])).Succeeded);
        var earlierRequest = Request(sourceId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var post = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(9m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            var state = await verify.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
            Assert.Equal(15m, state.OnHandBaseQty);
            Assert.Equal(45m, state.InventoryValue);
            var history = await verify.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == earlier.BatchNo && h.EntryRole == "ORIGINAL");
            var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                verify, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, [history]))[history.Id];
            Assert.Equal(3m, facts.Where(f => f.Direction < 0)
                .Sum(f => f.CostAmount));
            Assert.Equal(3m, facts.Where(f => f.Direction > 0)
                .Sum(f => f.CostAmount));
        }
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(8m, (await restored.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
            Assert.Equal(15m, state.OnHandBaseQty);
            Assert.Equal(45m, state.InventoryValue);
        }
        var repost = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        var secondRollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(secondRollback.Succeeded, secondRollback.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var owner = await db.StockPostings.SingleAsync(p =>
                p.SourceDocumentNo == later.BatchNo.ToString()
                && p.PostingRole == "PRIMARY");
            owner.SourceDocumentId = "999999";
            await db.SaveChangesAsync();
        }
        var tampered = await transfer.PostAsync([earlier.BatchNo]);
        Assert.False(tampered.Succeeded);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(8m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(15, false)]
    public async Task Backdated_wac_transfer_preserves_later_issue_or_rejects_shortage(
        int laterIssueQuantity, bool canPost)
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = canPost ? 0m : 1m,
                StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", canPost ? 15m : 16m, 3m);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = sourceId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = laterIssueQuantity, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        if (!canPost)
        {
            int destinationId;
            await using (var db = await _factory.CreateDbContextAsync())
                destinationId = await db.IvBalLocs.Where(b => b.LocCode == "BIN2")
                    .Select(b => b.Id).SingleAsync();
            var reverseRequest = Request(destinationId, 1m);
            reverseRequest.Lines![0].FrLocation = "BIN2";
            reverseRequest.Lines[0].ToLocation = "BIN1";
            var reverse = await transfer.SaveNewAsync(reverseRequest);
            Assert.True(reverse.Succeeded, reverse.ErrorMessage);
            var reversePost = await transfer.PostAsync([reverse.BatchNo]);
            Assert.True(reversePost.Succeeded, reversePost.ErrorMessage);
        }
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var draft = await transfer.SaveNewAsync(request);
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await transfer.PostAsync([draft.BatchNo]);
        if (!canPost)
        {
            Assert.False(posted.Succeeded);
            Assert.Contains("negative", posted.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches
                .SingleAsync(b => b.BatchNo == draft.BatchNo)).BatchStatus);
            Assert.Equal(1m, (await unchanged.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(0m, (await unchanged.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(1m, (await unchanged.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(3m, (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
            Assert.False(await unchanged.IvTrxHistories.AnyAsync(h =>
                h.BatchNo == draft.BatchNo));
            return;
        }
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(1m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(11m, (await verify.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(33m, (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rolledBack = await transfer.RollbackAsync([draft.BatchNo]);
        Assert.True(rolledBack.Succeeded, rolledBack.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(11m, (await restored.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(33m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backdated_transfer_posts_and_rolls_back_before_later_receipt(
        bool fifo)
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                SuppCode = "SUP01", SuppName = "Supplier",
                Currency = "MYR", GlCode = "AP001",
                IsActive = true, RowVersion = Guid.NewGuid().ToByteArray()
            });
            if (fifo)
            {
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                    new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
                ]);
            }
            else
            {
                await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 15m, 3m);
            }
            await db.SaveChangesAsync();
        }
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, FixedToday);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday, VendCode = "SUP01",
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN2",
                Quantity = 2m, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", UnitPrice = 3m,
                PriceConfirmed = true, Reason = "ADJ"
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var receiptPost = await receipt.PostAsync([later.BatchNo]);
        Assert.True(receiptPost.Succeeded, receiptPost.ErrorMessage);
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var draft = await transfer.SaveNewAsync(request);
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await transfer.PostAsync([draft.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(14m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(3m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(17m, (await verify.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(fifo ? 61m : 51m,
                (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([draft.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(2m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(17m, (await restored.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(fifo ? 61m : 51m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Backdated_transfer_posts_and_rolls_back_before_later_other_pair_transfer(
        bool fifo, int thirdPileCount)
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN3", IsActive = true
            });
            if (thirdPileCount == 2)
                db.IvLocations.Add(new IvLocation
                {
                    CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                    BranchCode = InventoryLedgerTestFixture.BranchCode,
                    WarehouseCode = "MAIN", LocCode = "BIN4", IsActive = true
                });
            foreach (var location in thirdPileCount == 2
                ? new[] { "BIN2", "BIN3", "BIN4" }
                : new[] { "BIN2", "BIN3" })
                db.IvBalLocs.Add(new IvBalLoc
                {
                    CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                    BranchCode = InventoryLedgerTestFixture.BranchCode,
                    ICode = "A100", WhCode = "MAIN", LocCode = location,
                    LotNo = string.Empty, IStatus = "ACTIVE",
                    StdQty = 0m, StdUom = "EA", TransDate = FixedToday
                });
            if (fifo)
            {
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                    new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
                ]);
            }
            else
            {
                await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 15m, 3m);
            }
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var laterRequest = Request(sourceId, 4m);
        laterRequest.Lines![0].ToLocation = "BIN3";
        var later = await transfer.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPost = await transfer.PostAsync([later.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);
        if (thirdPileCount == 2)
        {
            var anotherRequest = Request(sourceId, 3m);
            anotherRequest.Lines![0].ToLocation = "BIN4";
            var another = await transfer.SaveNewAsync(anotherRequest);
            Assert.True(another.Succeeded, another.ErrorMessage);
            var anotherPost = await transfer.PostAsync([another.BatchNo]);
            Assert.True(anotherPost.Succeeded, anotherPost.ErrorMessage);
        }
        var earlierRequest = Request(sourceId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(thirdPileCount == 2 ? 7m : 10m,
                (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(1m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(4m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN3")).StdQty);
            if (thirdPileCount == 2)
                Assert.Equal(3m, (await verify.IvBalLocs.SingleAsync(b =>
                    b.LocCode == "BIN4")).StdQty);
            Assert.Equal(fifo ? 55m : 45m,
                (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(thirdPileCount == 2 ? 8m : 11m,
            (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(4m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN3")).StdQty);
        if (thirdPileCount == 2)
            Assert.Equal(3m, (await restored.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN4")).StdQty);
        Assert.Equal(fifo ? 55m : 45m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_fifo_transfer_posts_and_rolls_back_before_later_issue()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = sourceId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var draft = await transfer.SaveNewAsync(request);
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await transfer.PostAsync([draft.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(1m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(11m, (await verify.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(43m, (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([draft.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(11m, (await restored.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(43m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var owner = await db.StockPostings.SingleAsync(p =>
                p.SourceDocumentNo == later.BatchNo.ToString()
                && p.PostingRole == "PRIMARY");
            owner.SourceDocumentId = "999999";
            await db.SaveChangesAsync();
        }
        var tampered = await transfer.PostAsync([draft.BatchNo]);
        Assert.False(tampered.Succeeded);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(43m, (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Fifo_transfer_moves_exact_layer_value()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var outbound = await verify.StockValuationFacts.Where(x => x.MovementCode == "TRANSFER_OUT").ToListAsync();
        var inbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_IN");
        Assert.Equal(27m, outbound.Sum(x => x.CostAmount));
        Assert.Equal(27m, inbound.CostAmount);
        Assert.NotEqual(TamperPrice, inbound.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(15m, state.OnHandBaseQty);
        Assert.Equal(55m, state.InventoryValue);
        var layers = await verify.StockFifoLayers.ToArrayAsync();
        Assert.Equal(state.OnHandBaseQty, layers.Sum(l => l.RemainingQty));
        Assert.Equal(state.InventoryValue, layers.Sum(l => l.RemainingValue));
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(2, snapshot.ActiveConsumptions.Count);
        Assert.All(snapshot.ActiveConsumptions, c => Assert.False(c.DepletesPool));
        var beforeTransfer = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, snapshot.ActiveConsumptions[0].IssueKey);
        Assert.Equal(15m, beforeTransfer.Quantity);
        Assert.Equal(55m, beforeTransfer.Value);
        var nextSequence = await verify.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var candidate = new StockReplayPhysicalEvent(999_999,
            new StockReplayEventKey(FixedToday.AddDays(-1),
                StockReplayEventKey.OrdinaryClass, nextSequence,
                "H9999999999", 0), -1m);
        var physical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId, candidate);
        var originalTransfer = await verify.IvTrxHistories.AsNoTracking()
            .Where(h => h.BatchNo == save.BatchNo && h.EntryRole == "ORIGINAL")
            .SingleAsync();
        var transferHistoryId = originalTransfer.Id;
        var destinationId = originalTransfer.ToBalLocId!.Value;
        var destinationPhysical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            candidate with { SignedDelta = 1m });
        var paired = await StockReplayFifoTransferInsertionPlanner.BuildAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical,
            destinationPhysical);
        StockReplayFifoTransferInsertionPlanner.ValidatePreparedPlan(paired);
        Assert.Equal(6m, paired.Source.Projection.FinalQuantity);
        Assert.Equal(9m, paired.Destination.Projection.FinalQuantity);
        Assert.Equal(15m, paired.Projection.FinalQuantity);
        Assert.Equal(55m, paired.Projection.FinalValue);
        Assert.Equal(3m, Assert.Single(paired.Projection.Issues
            .Where(i => i.IsCandidate)).Value);
        var wrongDestination = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            candidate with { SignedDelta = 2m });
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            (await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoTransferInsertionPlanner.BuildAsync(
                    verify, InventoryLedgerTestFixture.CompanyCode,
                    InventoryLedgerTestFixture.BranchCode, "A100", physical,
                    wrongDestination))).Error.Code);
        var transferKey = snapshot.ActiveConsumptions[0].IssueKey;
        var sourceRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId,
            new StockReplayPhysicalEvent(transferHistoryId, transferKey, -8m));
        var destinationRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, destinationId,
            new StockReplayPhysicalEvent(transferHistoryId, transferKey, 8m));
        var pairedRemoval = await StockReplayFifoTransferRemovalPlanner.BuildAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", sourceRemoval,
            destinationRemoval);
        StockReplayFifoTransferRemovalPlanner.ValidatePreparedPlan(pairedRemoval);
        Assert.Equal(15m, pairedRemoval.Source.Projection.FinalQuantity);
        Assert.Equal(0m, pairedRemoval.Destination.Projection.FinalQuantity);
        Assert.Equal(15m, pairedRemoval.Projection.FinalQuantity);
        Assert.Equal(55m, pairedRemoval.Projection.FinalValue);
        var financial = await StockReplayFifoInsertionPlanner.BuildAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical);
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(financial);
        Assert.Equal(14m, financial.Projection.FinalQuantity);
        Assert.Equal(52m, financial.Projection.FinalValue);
        var forged = financial with
        {
            Projection = financial.Projection with { FinalValue = 53m }
        };
        Assert.Equal(StockLedgerErrorCodes.StaleDocument,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoInsertionPlanner.ValidatePreparedPlan(forged))
                .Error.Code);
        var receiptPhysical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId,
            candidate with { EventId = 999_998, SignedDelta = 1m });
        var receiptFinancial = await StockReplayFifoInsertionPlanner.BuildAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", receiptPhysical,
            receiptValue: 2m, receiptBaseUom: "EA");
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(receiptFinancial);
        Assert.Equal(16m, receiptFinancial.Projection.FinalQuantity);
        Assert.Equal(57m, receiptFinancial.Projection.FinalValue);
        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var command = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = "FIFO_REPLAY_PLAN_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = "PLAN_TEST",
            SourceDocumentId = "FIFO-1",
            SourceDocumentNo = "FIFO-1",
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            EffectiveAt = candidate.Key.EffectiveAt,
            Evidence = StockPostingFingerprint.Create(
                new { Document = "FIFO-1" },
                new { candidate.Key.EffectiveAt })
        };
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        try
        {
            var issuePermit = await StockReplayFifoInsertionPlanner
                .PrepareWithinRootAsync(begin.Context!, financial);
            var receiptPermit = await StockReplayFifoInsertionPlanner
                .PrepareWithinRootAsync(begin.Context!, receiptFinancial);
            Assert.Equal(begin.Context!.Posting.Id, issuePermit.RootPostingId);
            Assert.Equal(financial.Snapshot.SnapshotHash,
                issuePermit.SnapshotHash);
            Assert.Equal(14m, issuePermit.FinalQuantity);
            Assert.Equal(52m, issuePermit.FinalValue);
            Assert.Equal(16m, receiptPermit.FinalQuantity);
            Assert.Equal(57m, receiptPermit.FinalValue);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMiRootWriter.InsertAsync(
                        begin.Context!, financial, issuePermit, 999_999)))
                    .Error.Code);
            Assert.Equal(physical.CurrentQuantity, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
        }
        finally
        {
            if (begin.Context is not null)
                await coordinator.AbortInTransactionAsync(begin.Context);
            await transaction.RollbackAsync();
        }
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var earlier = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = candidate.Key.EffectiveAt,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 1m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = TamperPrice
            }]
        });
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        await using var replayDb = await _factory.CreateDbContextAsync();
        await using var replayTransaction =
            await replayDb.Database.BeginTransactionAsync();
        var replayCommand = await InventoryStockPostingCommandFactory.CreateAsync(
            replayDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            false, candidate.Key.EffectiveAt, CancellationToken.None);
        var replayBegin = await coordinator.BeginInTransactionAsync(
            replayDb, replayCommand);
        Assert.NotNull(replayBegin.Context);
        var replayCommitted = false;
        int insertedHistoryId = 0;
        try
        {
            var permit = await StockReplayFifoInsertionPlanner
                .PrepareWithinRootAsync(replayBegin.Context!, financial);
            var written = await StockReplayFifoMiRootWriter.InsertAsync(
                replayBegin.Context!, financial, permit, earlier.BatchNo);
            insertedHistoryId = written.RootHistoryId;
            Assert.Contains(transferHistoryId,
                written.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                replayBegin.Context!, written.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                replayBegin.Context!, financial, permit);
            await replayTransaction.CommitAsync();
            replayCommitted = true;
        }
        finally
        {
            if (!replayCommitted)
            {
                await coordinator.AbortInTransactionAsync(replayBegin.Context!);
                await replayTransaction.RollbackAsync();
            }
        }
        await using var afterReplay = await _factory.CreateDbContextAsync();
        var replayed = await StockReplayFifoActiveLayerReader.ReadAsync(
            afterReplay, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(14m, replayed.CurrentQuantity);
        Assert.Equal(52m, replayed.CurrentValue);
        Assert.Equal(28m, replayed.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity ==
                $"H{transferHistoryId:D10}").Sum(c => c.Value));
        var activeTransfer = await StockReplayValuationGenerationResolver.ResolveAsync(
            afterReplay, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode,
            [await afterReplay.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == transferHistoryId)],
            CancellationToken.None);
        Assert.Equal(28m, activeTransfer[transferHistoryId]
            .Where(f => f.MovementCode == "TRANSFER_OUT")
            .Sum(f => f.CostAmount));
        var activeInbound = activeTransfer[transferHistoryId]
            .Single(f => f.MovementCode == "TRANSFER_IN");
        Assert.Equal(28m, activeInbound.CostAmount);
        Assert.Equal(88m, activeInbound.TransactionCostAmount);
        Assert.Equal(0.31818182m, activeInbound.ExchangeRate);
        var insertedKey = replayed.ActiveConsumptions.Single(c =>
            c.IssueKey.LineIdentity == $"H{insertedHistoryId:D10}").IssueKey;
        var removalPhysical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            afterReplay, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId,
            new StockReplayPhysicalEvent(insertedHistoryId,
                insertedKey, -1m));
        var removalPlan = await StockReplayFifoRemovalPlanner.BuildAsync(
            afterReplay, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", removalPhysical);
        await using var removalDb = await _factory.CreateDbContextAsync();
        await using var removalTransaction =
            await removalDb.Database.BeginTransactionAsync();
        var removalCommand = await InventoryStockPostingCommandFactory.CreateAsync(
            removalDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            true, insertedKey.EffectiveAt, CancellationToken.None);
        var removalBegin = await coordinator.BeginInTransactionAsync(
            removalDb, removalCommand);
        Assert.NotNull(removalBegin.Context);
        var removalCommitted = false;
        try
        {
            var permit = await StockReplayFifoRemovalPlanner
                .PrepareWithinRootAsync(removalBegin.Context!, removalPlan);
            var removed = await StockReplayFifoMiRootWriter.RemoveAsync(
                removalBegin.Context!, removalPlan, permit, earlier.BatchNo);
            Assert.Equal([transferHistoryId],
                removed.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                removalBegin.Context!, removed.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                removalBegin.Context!, removalPlan, permit);
            await removalTransaction.CommitAsync();
            removalCommitted = true;
        }
        finally
        {
            if (!removalCommitted)
            {
                await coordinator.AbortInTransactionAsync(removalBegin.Context!);
                await removalTransaction.RollbackAsync();
            }
        }
        await using var restoredDb = await _factory.CreateDbContextAsync();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(
            restoredDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, restored.CurrentQuantity);
        Assert.Equal(55m, restored.CurrentValue);
        Assert.Equal(27m, restored.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity ==
                $"H{transferHistoryId:D10}").Sum(c => c.Value));
        var activeTransferKey = restored.ActiveConsumptions.Single(c =>
            c.IssueKey.LineIdentity ==
                $"H{transferHistoryId:D10}" && c.SplitOrdinal == 0).IssueKey;
        var transferSourceRemoval = await StockReplayPhysicalPlanner
            .BuildRemovalAsync(restoredDb,
                InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, balId,
                new StockReplayPhysicalEvent(
                    transferHistoryId, activeTransferKey, -8m));
        var transferDestinationRemoval = await StockReplayPhysicalPlanner
            .BuildRemovalAsync(restoredDb,
                InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, destinationId,
                new StockReplayPhysicalEvent(
                    transferHistoryId, activeTransferKey, 8m));
        var transferRemoval = await StockReplayFifoTransferRemovalPlanner
            .BuildAsync(restoredDb,
                InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, "A100",
                transferSourceRemoval, transferDestinationRemoval);
        await using var transferRemovalDb = await _factory.CreateDbContextAsync();
        await using var transferRemovalTransaction =
            await transferRemovalDb.Database.BeginTransactionAsync();
        var transferRemovalCommand =
            await InventoryStockPostingCommandFactory.CreateAsync(
                transferRemovalDb, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, save.BatchNo,
                true, activeTransferKey.EffectiveAt, CancellationToken.None);
        var transferRemovalBegin = await coordinator.BeginInTransactionAsync(
            transferRemovalDb, transferRemovalCommand);
        Assert.NotNull(transferRemovalBegin.Context);
        var transferRemovalCommitted = false;
        try
        {
            var removed = await StockReplayFifoTransferRemovalRootWriter.RemoveAsync(
                transferRemovalBegin.Context!, transferRemoval, save.BatchNo);
            await transferRemovalDb.SaveChangesAsync();
            await coordinator.CompleteReplayInTransactionAsync(
                transferRemovalBegin.Context!, removed.RequiredActiveHistoryIds);
            await StockReplayFifoTransferReconciliationGate
                .VerifySealedWithinRootAsync(
                    transferRemovalBegin.Context!, transferRemoval);
            await transferRemovalTransaction.CommitAsync();
            transferRemovalCommitted = true;
        }
        finally
        {
            if (!transferRemovalCommitted)
            {
                await coordinator.AbortInTransactionAsync(
                    transferRemovalBegin.Context!);
                await transferRemovalTransaction.RollbackAsync();
            }
        }
        await using var afterTransferRemoval = await _factory.CreateDbContextAsync();
        var noTransfer = await StockReplayFifoActiveLayerReader.ReadAsync(
            afterTransferRemoval, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(15m, noTransfer.CurrentQuantity);
        Assert.Equal(55m, noTransfer.CurrentValue);
        Assert.Empty(noTransfer.ActiveConsumptions);
    }

    [Fact]
    public async Task Fifo_transfer_rollback_restores_the_unchanged_layer_pool()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await transfer.PostAsync([save.BatchNo])).Succeeded);
        var rollback = await transfer.RollbackAsync([save.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(15m, state.OnHandBaseQty);
        Assert.Equal(55m, state.InventoryValue);
        var layers = await verify.StockFifoLayers.OrderBy(l => l.ReceiptEffectiveAt)
            .ToArrayAsync();
        Assert.Equal(new[] { 5m, 10m }, layers.Select(l => l.RemainingQty));
        Assert.Equal(new[] { 15m, 40m }, layers.Select(l => l.RemainingValue));
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Empty(snapshot.ActiveConsumptions);
    }

    [Fact]
    public async Task Fifo_issue_after_transfer_depletes_only_the_later_issue()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var saved = await transfer.SaveNewAsync(Request(balId, 8m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await transfer.PostAsync([saved.BatchNo])).Succeeded);
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var issueSaved = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = balId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    Quantity = 6m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = TamperPrice
                }
            ]
        });
        Assert.True(issueSaved.Succeeded, issueSaved.ErrorMessage);
        Assert.True((await issue.PostAsync([issueSaved.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(9m, state.OnHandBaseQty);
        Assert.Equal(36m, state.InventoryValue);
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(4, snapshot.ActiveConsumptions.Count);
        Assert.Equal(2, snapshot.ActiveConsumptions.Count(c => c.DepletesPool));
        var beforeIssue = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, snapshot.ActiveConsumptions
                .First(c => c.DepletesPool).IssueKey);
        Assert.Equal(15m, beforeIssue.Quantity);
        Assert.Equal(55m, beforeIssue.Value);
    }

    [Fact]
    public async Task Standard_transfer_uses_effective_standard_cost_on_both_legs()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var outbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_OUT");
        var inbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_IN");
        Assert.Equal(65m, outbound.CostAmount);
        Assert.Equal(65m, inbound.CostAmount);
        Assert.Equal(StockValuationSources.Standard, outbound.ValuationSource);
        Assert.Equal(StockValuationSources.Standard, inbound.ValuationSource);
        Assert.NotEqual(TamperPrice, outbound.UnitCost);
        Assert.NotEqual(PurchasePrice, outbound.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(650m, state.InventoryValue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Backdated_standard_transfer_posts_and_rolls_back_before_later_issue(
        bool laterRevision, bool laterRevaluation)
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            if (laterRevision)
            {
                var original = await db.ItemStandardCostRevisions.SingleAsync();
                original.EffectiveTo = FixedToday;
                await db.SaveChangesAsync();
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                    "A100", 7.5m, effectiveFrom: FixedToday);
            }
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = sourceId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        if (laterRevaluation)
        {
            var revaluation = new StandardCostRevaluationService(
                InventoryLedgerTestFixture.CreateCoordinator(_factory),
                new StockCostMethodResolver(), new ItemStandardCostResolver(),
                new StockValueAdjustmentWriter());
            var result = await revaluation.RevalueAsync(
                new StandardCostRevaluationRequest
                {
                    EffectiveAt = FixedToday.AddHours(12),
                    ItemCodes = ["A100"]
                });
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(15m, Assert.Single(result.Value!).RevaluationAmount);
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var draft = await transfer.SaveNewAsync(request);
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var posted = await transfer.PostAsync([draft.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(1m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            var facts = await verify.StockValuationFacts.Where(f =>
                f.SourceDocumentNo == draft.BatchNo.ToString()
                && (f.MovementCode == "TRANSFER_OUT"
                    || f.MovementCode == "TRANSFER_IN")).ToArrayAsync();
            Assert.Equal(2, facts.Length);
            Assert.All(facts, f => Assert.Equal(6.5m, f.CostAmount));
            var state = await verify.StockCostStates.SingleAsync();
            Assert.Equal(11m, state.OnHandBaseQty);
            Assert.Equal(laterRevaluation ? 82.5m
                : laterRevision ? 67.5m : 71.5m,
                state.InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([draft.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(11m, (await restored.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(laterRevaluation ? 82.5m
            : laterRevision ? 67.5m : 71.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var laterPostingId = laterRevaluation ? null
                : (await tamper.IvTrxHistories.SingleAsync(h =>
                    h.BatchNo == later.BatchNo
                    && h.EntryRole == "ORIGINAL")).StockPostingId;
            var laterOwner = laterRevaluation
                ? await tamper.StockPostings.SingleAsync(p =>
                    p.CommandType == "STANDARD_COST_REVALUATION")
                : await tamper.StockPostings.SingleAsync(p =>
                    p.Id == laterPostingId);
            laterOwner.SourceDocumentId = "999999";
            await tamper.SaveChangesAsync();
        }
        var rejected = await transfer.PostAsync([draft.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await unchanged.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(laterRevaluation ? 82.5m
            : laterRevision ? 67.5m : 71.5m,
            (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_standard_transfer_preserves_later_transfer_on_same_pair()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await transfer.SaveNewAsync(Request(sourceId, 4m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await transfer.PostAsync([later.BatchNo])).Succeeded);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(5m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(97.5m,
                (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(4m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(97.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Standard_transfer_rolls_back_after_later_issue_in_open_month()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.IvBalLocs.SingleAsync(b => b.Id == sourceId)).TransDate
                = FixedToday.AddDays(-1);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var earlierRequest = Request(sourceId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        Assert.True((await transfer.PostAsync([earlier.BatchNo])).Succeeded);
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = sourceId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(71.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_standard_transfer_preserves_later_receipt()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA", TransDate = FixedToday
            });
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                SuppCode = "SUP01", SuppName = "Supplier",
                Currency = "MYR", GlCode = "AP001",
                IsActive = true, RowVersion = Guid.NewGuid().ToByteArray()
            });
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
            await db.SaveChangesAsync();
        }
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, FixedToday);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday, VendCode = "SUP01",
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN2",
                Quantity = 2m, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", UnitPrice = 3m,
                PriceConfirmed = true, Reason = "ADJ"
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var earlier = await transfer.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await transfer.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(14m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(3m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(17m, (await verify.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(110.5m,
                (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await transfer.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(2m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(110.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Standard_transfer_rolls_back_and_reposts_before_later_revaluation_only()
    {
        var sourceId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.IvBalLocs.SingleAsync(b => b.Id == sourceId)).TransDate
                = FixedToday.AddDays(-1);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = string.Empty, IStatus = "ACTIVE",
                StdQty = 0m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            var original = await db.ItemStandardCostRevisions.SingleAsync();
            original.EffectiveTo = FixedToday;
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                "A100", 7.5m, effectiveFrom: FixedToday);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
            await db.SaveChangesAsync();
        }
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(sourceId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var draft = await transfer.SaveNewAsync(request);
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        Assert.True((await transfer.PostAsync([draft.BatchNo])).Succeeded);
        var revaluation = new StandardCostRevaluationService(
            InventoryLedgerTestFixture.CreateCoordinator(_factory),
            new StockCostMethodResolver(), new ItemStandardCostResolver(),
            new StockValueAdjustmentWriter());
        var result = await revaluation.RevalueAsync(
            new StandardCostRevaluationRequest
            {
                EffectiveAt = FixedToday.AddHours(12),
                ItemCodes = ["A100"]
            });
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(15m, Assert.Single(result.Value!).RevaluationAmount);
        var rollback = await transfer.RollbackAsync([draft.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        var repost = await transfer.PostAsync([draft.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(14m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN1")).StdQty);
            Assert.Equal(1m, (await verify.IvBalLocs.SingleAsync(b =>
                b.LocCode == "BIN2")).StdQty);
            Assert.Equal(112.5m,
                (await verify.StockCostStates.SingleAsync()).InventoryValue);
        }
        Assert.True((await transfer.RollbackAsync([draft.BatchNo])).Succeeded);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN1")).StdQty);
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync(b =>
            b.LocCode == "BIN2")).StdQty);
        Assert.Equal(112.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Rollback_reverses_both_transfer_facts()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await transfer.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await transfer.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.ToListAsync();
        var originals = facts.Where(x => x.ReversesValuationFactId == null).ToList();
        var reversals = facts.Where(x => x.ReversesValuationFactId != null).ToList();
        Assert.Equal(2, originals.Count);
        Assert.Equal(2, reversals.Count);
        Assert.All(reversals, reversal =>
        {
            var original = originals.Single(x => x.Id == reversal.ReversesValuationFactId);
            Assert.Equal(original.CostAmount, reversal.CostAmount);
            Assert.Equal(StockValuationSources.OriginalReversal, reversal.ValuationSource);
        });
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    private async Task<int> SeedBalanceAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = BalanceReferencePrice,
            TransDate = FixedToday
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvStockTransferSaveRequest Request(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockTransferLineRequest
                {
                    FromBalLocId = fromBalLocId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN2",
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = TamperPrice
                }
            ]
        };
}
