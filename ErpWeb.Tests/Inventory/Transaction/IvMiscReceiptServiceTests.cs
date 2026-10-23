using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMiscReceipt)]
public class IvMiscReceiptServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvMiscReceiptServiceTests()
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

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvWarehouses.AddRange(
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                WarehouseDesc = "Main WH",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "EMPTY",
                WarehouseDesc = "No bins",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "INACTIVE",
                IsActive = false
            },
            new IvWarehouse
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                WarehouseCode = "MAIN",
                IsActive = true
            });

        db.IvLocations.AddRange(
            new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                LocDesc = "Bin 1",
                IsActive = true
            },
            new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                LocCode = "DEAD",
                IsActive = false
            });

        db.MsUoms.AddRange(
            new MsUom { CompanyCode = "DEMO", UomCode = "EA", UomDesc = "Each", IsActive = true },
            new MsUom { CompanyCode = "DEMO", UomCode = "KG", UomDesc = "Kilogram", IsActive = true },
            new MsUom { CompanyCode = "DEMO", UomCode = "OLD", IsActive = false });

        db.IvClasses.AddRange(
            new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IDesc = "Raw", IsActive = true },
            new IvClass { CompanyCode = "DEMO", IClassCode = "FG", IDesc = "Finished", IsActive = true },
            new IvClass { CompanyCode = "DEMO", IClassCode = "X", IsActive = false });

        db.IvStatuses.AddRange(
            new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", StatusDesc = "Active", IsActive = true },
            new IvStatus { CompanyCode = "DEMO", IStatus = "DAMAGED", StatusDesc = "Damaged", IsActive = true },
            new IvStatus { CompanyCode = "DEMO", IStatus = "OLD", IsActive = false });

        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Widget A",
                IClassCode = "RAW",
                StdUom = "EA",
                LotControl = false,
                IsActive = true,
                PurchasePrice = 12.5m
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                IDesc = "Lot item",
                IClassCode = "FG",
                StdUom = "KG",
                LotControl = true,
                IsActive = true,
                PurchasePrice = 3m
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "DEAD",
                IDesc = "Inactive",
                IClassCode = "RAW",
                StdUom = "EA",
                IsActive = false
            },
            new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "A100",
                IDesc = "Other co",
                IClassCode = "RAW",
                StdUom = "EA",
                IsActive = true
            });

        db.PoSuppliers.AddRange(
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP02",
                SuppName = "Beta Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUPOFF",
                SuppName = "Inactive Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = false,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Other Co Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                SuppCode = "SUPBR2",
                SuppName = "Branch Two Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Save_BlankLocation_InvalidScope()
    {
        var sut = CreateSut(location: null);
        var result = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidNonLotLineRequest()]
        });

        Assert.False(result.Succeeded);
        Assert.Contains("location", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
    }

    [Fact]
    public async Task Save_stamps_batch_location_from_claims_not_to_location()
    {
        var sut = CreateSut(location: "SITE");
        var result = await sut.SaveNewAsync(ValidNonLotLine(new DateTime(2026, 8, 24)));

        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        Assert.Equal("SITE", batch.LocationCode);
        var line = Assert.Single(batch.Details);
        Assert.Equal("BIN1", line.ToLocation);
        Assert.Equal("SITE", line.LocationCode);
    }

    [Fact]
    public async Task Save_future_dated_new_draft_and_edit_it_without_posting_stock()
    {
        var sut = CreateSut();
        var request = ValidNonLotLine(FixedToday.AddDays(1));
        var saved = await sut.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        request.TrxDate = FixedToday.AddDays(2);
        var updated = await sut.UpdateAsync(saved.BatchNo, request);
        Assert.True(updated.Succeeded, updated.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Equal(FixedToday.AddDays(2), batch.TrxDtTime);
        Assert.Empty(await db.IvBalLocs.ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockPostings.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
        Assert.Empty(await db.StockFifoLayers.ToListAsync());
    }

    [Fact]
    public async Task Post_future_dated_mr_blocks_early_and_succeeds_on_transaction_date()
    {
        var sut = CreateSut(postingBusinessDate: FixedToday);
        var saved = await sut.SaveNewAsync(ValidNonLotLine(FixedToday.AddDays(1)));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("cannot be posted until", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Null(batch.PostedDate);
        Assert.Null(batch.PostedBy);
        Assert.Equal(0, batch.PostedCount);
        Assert.Null(batch.PostingOperationId);
        Assert.Empty(await db.IvBalLocs.ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockPostings.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
        Assert.Empty(await db.StockFifoLayers.ToListAsync());

        var postOnDate = await CreateSut(postingBusinessDate: FixedToday.AddDays(1)).PostAsync([saved.BatchNo]);
        Assert.True(postOnDate.Succeeded, postOnDate.ErrorMessage);
        await using var postedDb = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.Posted,
            (await postedDb.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo)).BatchStatus);
        Assert.Equal(1, await postedDb.IvTrxHistories.CountAsync());
        Assert.Single(await postedDb.IvBalLocs.ToListAsync());
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage)]
    [InlineData(StockCostMethods.Fifo)]
    [InlineData(StockCostMethods.Standard)]
    public async Task Future_draft_posting_matches_a_draft_created_on_its_effective_date(string costMethod)
    {
        var effectiveDate = FixedToday.AddDays(1);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedCostPolicyAsync(db, costMethod);
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "B100",
                IDesc = "Equivalent valued item",
                IClassCode = "RAW",
                StdUom = "EA",
                LotControl = false,
                IsActive = true,
                PurchasePrice = 12.5m
            });
            await db.SaveChangesAsync();
            if (string.Equals(costMethod, StockCostMethods.Standard, StringComparison.Ordinal))
            {
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 12.5m);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "B100", 12.5m);
            }
        }

        var preparedEarly = CreateSut(
            withLedger: true,
            draftBusinessDate: FixedToday,
            postingBusinessDate: effectiveDate);
        var earlySave = await preparedEarly.SaveNewAsync(ValidNonLotLine(effectiveDate, "A100"));
        Assert.True(earlySave.Succeeded, earlySave.ErrorMessage);
        var earlyPost = await preparedEarly.PostAsync([earlySave.BatchNo]);
        Assert.True(earlyPost.Succeeded, earlyPost.ErrorMessage);

        var createdOnDate = CreateSut(
            withLedger: true,
            draftBusinessDate: effectiveDate,
            postingBusinessDate: effectiveDate);
        var onDateSave = await createdOnDate.SaveNewAsync(ValidNonLotLine(effectiveDate, "B100"));
        Assert.True(onDateSave.Succeeded, onDateSave.ErrorMessage);
        var onDatePost = await createdOnDate.PostAsync([onDateSave.BatchNo]);
        Assert.True(onDatePost.Succeeded, onDatePost.ErrorMessage);

        var preparedEarlySnapshot = await ReadCostingSnapshotAsync("A100");
        var createdOnDateSnapshot = await ReadCostingSnapshotAsync("B100");
        Assert.Equal(preparedEarlySnapshot.State, createdOnDateSnapshot.State);
        Assert.Equal(preparedEarlySnapshot.FifoLayers, createdOnDateSnapshot.FifoLayers);
        if (costMethod == StockCostMethods.Fifo)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var history = await db.IvTrxHistories
                .SingleAsync(h => h.ICode == "A100");
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                db, "DEMO", "HQ", "A100");
            var origin = Assert.Single(snapshot.Layers);
            var balLocId = await db.IvBalLocs
                .Where(b => b.ICode == "A100")
                .Select(b => b.Id).SingleAsync();
            var physicalPlan = await StockReplayPhysicalPlanner.BuildRemovalAsync(
                db, "DEMO", "HQ", balLocId,
                new StockReplayPhysicalEvent(history.Id,
                    origin.ReceiptKey!.Value, 10m));
            var fifoPlan = await StockReplayFifoRemovalPlanner.BuildAsync(
                db, "DEMO", "HQ", "A100", physicalPlan);
            StockReplayFifoRemovalPlanner.ValidatePreparedPlan(fifoPlan);
            Assert.Equal(0m, fifoPlan.Projection.FinalQuantity);
            Assert.Equal(0m, fifoPlan.Projection.FinalValue);
        }
    }

    [Fact]
    public async Task Fifo_mr_plan_uses_the_approved_draft_base_cost()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ICode = "A100",
                WhCode = "MAIN",
                LocCode = "BIN1",
                LotNo = string.Empty,
                IStatus = "ACTIVE",
                StdQty = 10m,
                StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(10m, 3m, new DateTime(2026, 3, 1))]);
        }
        var sut = CreateSut(withLedger: true);
        var saved = await sut.SaveNewAsync(ValidNonLotLine(FixedToday));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using var planningDb = await _factory.CreateDbContextAsync();
        var balId = await planningDb.IvBalLocs.Select(b => b.Id).SingleAsync();
        var authority = await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
            planningDb, "DEMO", "HQ", saved.BatchNo, balId);
        Assert.Equal(10m, authority.Quantity);
        Assert.Equal(12.5m, authority.UnitCost);
        Assert.Equal(125m, authority.Value);
        var nextSequence = await planningDb.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var candidate = new StockReplayPhysicalEvent(999_998,
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, nextSequence,
                "H9999999998", 0), 10m);
        var physical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            planningDb, "DEMO", "HQ", balId, candidate);
        var plan = await StockReplayFifoInsertionPlanner.BuildMiscReceiptAsync(
            planningDb, "DEMO", "HQ", saved.BatchNo, physical);
        Assert.Equal(125m, plan.ReceiptValue);
        Assert.Equal(20m, plan.Projection.FinalQuantity);
        Assert.Equal(155m, plan.Projection.FinalValue);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, "DEMO", "HQ", saved.BatchNo, false,
            FixedToday, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        try
        {
            var permit = await StockReplayFifoInsertionPlanner
                .PrepareMiscReceiptWithinRootAsync(begin.Context!, plan,
                    saved.BatchNo);
            Assert.Equal(155m, permit.FinalValue);
            var detail = await rootDb.IvTrxBatchDetails.SingleAsync();
            detail.CostEvidenceType = null;
            await rootDb.SaveChangesAsync();
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoInsertionPlanner.PrepareMiscReceiptWithinRootAsync(
                        begin.Context!, plan, saved.BatchNo))).Error.Code);
            detail.CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved;
            await rootDb.SaveChangesAsync();
            detail.UnitPrice = 99m;
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrReceiptPhysicalWriter.AppendAsync(
                        begin.Context!, plan, permit, saved.BatchNo))).Error.Code);
            Assert.Equal(10m, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            await rootDb.Entry(detail).ReloadAsync();
            var historyId = await StockReplayFifoMrReceiptPhysicalWriter.AppendAsync(
                begin.Context!, plan, permit, saved.BatchNo);
            var bound = await StockReplayFifoMrReceiptCandidateBinder.BindAsync(
                begin.Context!, plan, permit, historyId);
            Assert.Equal($"H{historyId:D10}", bound.Key.LineIdentity);
            Assert.Equal(155m, bound.FinancialProjection.FinalValue);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrReceiptCandidateBinder.BindAsync(
                        begin.Context!, plan, permit, historyId + 1)))
                    .Error.Code);
            detail.CostOverrideReason = "changed after physical write";
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrReceiptValuationWriter.AppendAsync(
                        begin.Context!, plan, permit, historyId, 1)))
                    .Error.Code);
            Assert.Empty(await rootDb.StockValuationFacts.AsNoTracking()
                .Where(f => f.StockPostingId == begin.Context!.Posting.Id)
                .ToArrayAsync());
            await rootDb.Entry(detail).ReloadAsync();
            var trackedHistory = await rootDb.IvTrxHistories.SingleAsync(
                h => h.Id == historyId);
            trackedHistory.CostOverrideReason = "changed after physical write";
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrReceiptValuationWriter.AppendAsync(
                        begin.Context!, plan, permit, historyId, 1)))
                    .Error.Code);
            await rootDb.Entry(trackedHistory).ReloadAsync();
            var valued = await StockReplayFifoMrReceiptValuationWriter.AppendAsync(
                begin.Context!, plan, permit, historyId, 1);
            Assert.True(valued.FactId > 0);
            Assert.True(valued.LayerId > 0);
            Assert.Equal(125m, (await rootDb.StockValuationFacts.SingleAsync(
                f => f.Id == valued.FactId)).CostAmount);
            Assert.Equal(125m, (await rootDb.StockFifoLayers.SingleAsync(
                l => l.Id == valued.LayerId)).OriginalValue);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrReceiptValuationWriter.AppendAsync(
                        begin.Context!, plan, permit, historyId, 1)))
                    .Error.Code);
            var history = await rootDb.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == historyId);
            Assert.Equal(begin.Context!.Posting.Id, history.StockPostingId);
            Assert.Equal(10m, history.ToStdQty);
            Assert.Equal(20m, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            Assert.Equal(IvBatchStatuses.Posted, (await rootDb.IvTrxBatches
                .SingleAsync(b => b.BatchNo == saved.BatchNo)).BatchStatus);
            await StockReplayFifoStateWriter.ApplyMiscReceiptInsertionAsync(
                begin.Context!, plan, permit, valued);
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, [historyId]);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, plan, permit, valued);
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
        await using var verify = await _factory.CreateDbContextAsync();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, "DEMO", "HQ", "A100");
        Assert.Equal(20m, final.CurrentQuantity);
        Assert.Equal(155m, final.CurrentValue);
        Assert.Equal(2, final.Layers.Count);
        var originalReceipt = await verify.IvTrxHistories.AsNoTracking()
            .SingleAsync(h => h.BatchNo == saved.BatchNo
                && h.EntryRole == "ORIGINAL");
        var receiptLayer = final.Layers.Single(l => l.OriginalValue == 125m);
        var removalPhysical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            verify, "DEMO", "HQ", balId,
            new StockReplayPhysicalEvent(originalReceipt.Id,
                receiptLayer.ReceiptKey!.Value, 10m));
        var removalPlan = await StockReplayFifoRemovalPlanner.BuildAsync(
            verify, "DEMO", "HQ", "A100", removalPhysical);
        Assert.Equal(10m, removalPlan.Projection.FinalQuantity);
        Assert.Equal(30m, removalPlan.Projection.FinalValue);
        await using var rollbackDb = await _factory.CreateDbContextAsync();
        await using var rollbackTransaction =
            await rollbackDb.Database.BeginTransactionAsync();
        var rollbackCommand = await InventoryStockPostingCommandFactory
            .CreateAsync(rollbackDb, "DEMO", "HQ", saved.BatchNo,
                true, FixedToday, CancellationToken.None);
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            rollbackDb, rollbackCommand);
        Assert.NotNull(rollbackBegin.Context);
        var rollbackCommitted = false;
        try
        {
            var rollbackPermit = await StockReplayFifoRemovalPlanner
                .PrepareWithinRootAsync(rollbackBegin.Context!, removalPlan);
            var rollbackDetail = await rollbackDb.IvTrxBatchDetails
                .SingleAsync(d => d.BatchNo == saved.BatchNo);
            rollbackDetail.UnitPrice = 99m;
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoMrRemovalRootWriter.RemoveAsync(
                        rollbackBegin.Context!, removalPlan, rollbackPermit,
                        saved.BatchNo))).Error.Code);
            Assert.Equal(20m, (await rollbackDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            await rollbackDb.Entry(rollbackDetail).ReloadAsync();
            var removed = await StockReplayFifoMrRemovalRootWriter.RemoveAsync(
                rollbackBegin.Context!, removalPlan, rollbackPermit,
                saved.BatchNo);
            Assert.Empty(removed.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                rollbackBegin.Context!, removed.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                rollbackBegin.Context!, removalPlan, rollbackPermit);
            await rollbackTransaction.CommitAsync();
            rollbackCommitted = true;
        }
        finally
        {
            if (!rollbackCommitted)
            {
                await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
                await rollbackTransaction.RollbackAsync();
            }
        }
        await using var afterRollback = await _factory.CreateDbContextAsync();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(
            afterRollback, "DEMO", "HQ", "A100");
        Assert.Equal(10m, restored.CurrentQuantity);
        Assert.Equal(30m, restored.CurrentValue);
        Assert.Single(restored.Layers);
        var archivedLayer = await afterRollback.StockFifoLayers.AsNoTracking()
            .SingleAsync(l => l.Id == receiptLayer.LayerId);
        Assert.Equal(StockFifoLayerStatuses.Closed, archivedLayer.Status);
        Assert.Equal(0m, archivedLayer.RemainingQty);
        var reversalHistory = await afterRollback.IvTrxHistories
            .SingleAsync(h => h.EntryRole == "REVERSAL"
                && h.ReversesHistoryId == originalReceipt.Id);
        reversalHistory.ReversesHistoryId = null;
        await afterRollback.SaveChangesAsync();
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            (await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoActiveLayerReader.ReadAsync(
                    afterRollback, "DEMO", "HQ", "A100"))).Error.Code);
    }

    [Fact]
    public async Task Backdated_wac_mr_reprices_later_inventory_mi_in_open_month()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        int pileId;
        await using (var db = await _factory.CreateDbContextAsync())
            pileId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPost = await issue.PostAsync([later.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);

        var earlierDate = FixedToday.AddDays(-2);
        var receipt = CreateSut(withLedger: true,
            enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(ValidNonLotLine(earlierDate));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var disabled = await CreateSut(withLedger: true)
            .PostAsync([saved.BatchNo]);
        Assert.False(disabled.Succeeded);
        var result = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var state = await verify.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
        Assert.Equal(17m, state.OnHandBaseQty);
        Assert.Equal(115.6m, state.InventoryValue);
        Assert.Equal(17m, (await verify.IvBalLocs.SingleAsync()).StdQty);
        var laterHistory = await verify.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
            verify, "DEMO", "HQ", [laterHistory]);
        Assert.Equal(54.4m, activeFacts[laterHistory.Id].Sum(f => f.CostAmount));
        var receiptPosting = await verify.StockPostings.SingleAsync(p =>
            p.SourceDocumentNo == saved.BatchNo.ToString() && p.PostingRole == "PRIMARY");
        Assert.NotNull(receiptPosting.SealedAtUtc);

        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var afterRollback = await _factory.CreateDbContextAsync();
        var restored = await afterRollback.StockCostStates.SingleAsync(s =>
            s.ItemCode == "A100");
        Assert.Equal(7m, restored.OnHandBaseQty);
        Assert.Equal(21m, restored.InventoryValue);
        Assert.Equal(7m, (await afterRollback.IvBalLocs.SingleAsync()).StdQty);
        var laterOriginal = await afterRollback.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        var restoredFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
            afterRollback, "DEMO", "HQ", [laterOriginal]);
        Assert.Equal(24m, restoredFacts[laterOriginal.Id].Sum(f => f.CostAmount));
        Assert.Equal(IvBatchStatuses.New, (await afterRollback.IvTrxBatches
            .SingleAsync(b => b.BatchNo == saved.BatchNo)).BatchStatus);

        var repost = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        await using var afterRepost = await _factory.CreateDbContextAsync();
        var replayed = await afterRepost.StockCostStates.SingleAsync(s =>
            s.ItemCode == "A100");
        Assert.Equal(17m, replayed.OnHandBaseQty);
        Assert.Equal(115.6m, replayed.InventoryValue);
    }

    [Theory]
    [InlineData(false, 6, 18)]
    [InlineData(true, 20, 155)]
    public async Task Backdated_wac_mi_uses_historical_pool_before_later_inventory_movement(
        bool laterIsReceipt, int expectedQuantity, int expectedValue)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        int pileId;
        await using (var db = await _factory.CreateDbContextAsync())
            pileId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        async Task<int> SaveIssueAsync(DateTime date, decimal quantity)
        {
            var saved = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = date,
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = pileId, ICode = "A100",
                    FrWarehouse = "MAIN", FrLocation = "BIN1",
                    FrLotNo = string.Empty, Quantity = quantity, Uom = "EA",
                    IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            return saved.BatchNo;
        }
        if (laterIsReceipt)
        {
            var receipt = CreateSut(withLedger: true);
            var laterReceipt = await receipt.SaveNewAsync(ValidNonLotLine(FixedToday));
            Assert.True(laterReceipt.Succeeded, laterReceipt.ErrorMessage);
            var laterPost = await receipt.PostAsync([laterReceipt.BatchNo]);
            Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);
        }
        else
        {
            var laterBatch = await SaveIssueAsync(FixedToday, 4m);
            var laterPost = await issue.PostAsync([laterBatch]);
            Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);
        }
        var earlierBatch = await SaveIssueAsync(FixedToday.AddDays(-2), 5m);
        var posted = await issue.PostAsync([earlierBatch]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var state = await verify.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
        Assert.Equal(expectedQuantity, state.OnHandBaseQty);
        Assert.Equal(expectedValue, state.InventoryValue);
        Assert.Equal(expectedQuantity, (await verify.IvBalLocs.SingleAsync()).StdQty);
        var earlierHistory = await verify.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == earlierBatch && h.EntryRole == "ORIGINAL");
        var originalFact = await verify.StockValuationFacts.SingleAsync(f =>
            f.InventoryHistoryId == earlierHistory.Id);
        Assert.Equal(15m, originalFact.CostAmount);
        Assert.NotNull((await verify.StockPostings.SingleAsync(p =>
            p.SourceDocumentNo == earlierBatch.ToString()
            && p.PostingRole == "PRIMARY")).SealedAtUtc);

        var rollback = await issue.RollbackAsync([earlierBatch]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var afterRollback = await _factory.CreateDbContextAsync();
        var restored = await afterRollback.StockCostStates.SingleAsync(s =>
            s.ItemCode == "A100");
        Assert.Equal(laterIsReceipt ? 25m : 11m, restored.OnHandBaseQty);
        Assert.Equal(laterIsReceipt ? 170m : 33m, restored.InventoryValue);
        Assert.Equal(restored.OnHandBaseQty,
            (await afterRollback.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(IvBatchStatuses.New, (await afterRollback.IvTrxBatches
            .SingleAsync(b => b.BatchNo == earlierBatch)).BatchStatus);
    }

    [Fact]
    public async Task Backdated_wac_mr_rejects_later_transfer_without_mutation()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        int pileId;
        await using (var db = await _factory.CreateDbContextAsync())
            pileId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday);
        var moved = await transfer.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvStockTransferLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, ToWarehouse = "MAIN",
                ToLocation = "BIN2", Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        Assert.True((await transfer.PostAsync([moved.BatchNo])).Succeeded);
        var receipt = CreateSut(withLedger: true,
            enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(
            ValidNonLotLine(FixedToday.AddDays(-2)));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var rejected = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync(
            b => b.BatchNo == saved.BatchNo)).BatchStatus);
        Assert.Equal(15m, (await verify.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(45m, (await verify.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(15m, await verify.IvBalLocs.SumAsync(b => b.StdQty));
        Assert.False(await verify.IvTrxHistories.AnyAsync(h =>
            h.BatchNo == saved.BatchNo));
    }

    [Theory]
    [InlineData(10, 17, 127.5, 7)]
    [InlineData(3, 10, 40, 0)]
    public async Task Fifo_mr_root_writer_reprices_later_issue_and_commits(
        int receiptQuantity, int finalQuantity, double finalValue,
        int remainingReceiptQuantity)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                WarehouseCode = "MAIN", LocCode = "BIN2",
                IsActive = true
            });
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-1))
            ]);
        }
        int balId;
        await using (var db = await _factory.CreateDbContextAsync())
            balId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 999m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await mi.PostAsync([later.BatchNo])).Succeeded);
        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday);
        var moved = await transfer.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvStockTransferLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, ToWarehouse = "MAIN",
                ToLocation = "BIN2", Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 11m
            }]
        });
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        Assert.True((await transfer.PostAsync([moved.BatchNo])).Succeeded);
        var earlierDate = FixedToday.AddDays(-2);
        var receiptRequest = ValidNonLotLine(earlierDate);
        receiptRequest.Lines!.Single().Quantity = receiptQuantity;
        var earlier = await CreateSut(withLedger: true)
            .SaveNewAsync(receiptRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        await using var planningDb = await _factory.CreateDbContextAsync();
        var laterHistoryId = await planningDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).SingleAsync();
        var transferHistoryId = await planningDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.BatchNo == moved.BatchNo && h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).SingleAsync();
        var nextSequence = await planningDb.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var candidate = new StockReplayPhysicalEvent(999_997,
            new StockReplayEventKey(earlierDate,
                StockReplayEventKey.OrdinaryClass, nextSequence,
                "H9999999997", 0), receiptQuantity);
        var physical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            planningDb, "DEMO", "HQ", balId, candidate);
        var plan = await StockReplayFifoInsertionPlanner.BuildMiscReceiptAsync(
            planningDb, "DEMO", "HQ", earlier.BatchNo, physical);
        Assert.Equal(finalQuantity, plan.Projection.FinalQuantity);
        Assert.Equal((decimal)finalValue, plan.Projection.FinalValue);
        Assert.Equal(52.5m, plan.Projection.Issues.Single(i =>
            i.DepletesPool).Value);
        Assert.Equal(receiptQuantity == 10 ? 50m : 16m,
            plan.Projection.Issues.Single(i => !i.DepletesPool).Value);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, "DEMO", "HQ", earlier.BatchNo, false,
            earlierDate, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        long receiptLayerId = 0;
        try
        {
            var permit = await StockReplayFifoInsertionPlanner
                .PrepareMiscReceiptWithinRootAsync(begin.Context!, plan,
                    earlier.BatchNo);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueRepricingWriter.AppendAsync(
                        begin.Context!, plan, permit,
                        plan.Projection.Issues.Single(i =>
                            i.DepletesPool).Key, 2, 3)))
                    .Error.Code);
            Assert.Equal(3m, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            var written = await StockReplayFifoMrRootWriter.InsertAsync(
                begin.Context!, plan, permit, earlier.BatchNo);
            receiptLayerId = written.Receipt.LayerId;
            Assert.Equal(receiptQuantity == 10 ? 3 : 2,
                written.RequiredActiveHistoryIds.Count);
            Assert.Contains(laterHistoryId, written.RequiredActiveHistoryIds);
            Assert.Equal(receiptQuantity == 10,
                written.RequiredActiveHistoryIds.Contains(transferHistoryId));
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, written.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, plan, permit, written.Receipt);
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
        await using var verify = await _factory.CreateDbContextAsync();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, "DEMO", "HQ", "A100");
        Assert.Equal(finalQuantity, final.CurrentQuantity);
        Assert.Equal((decimal)finalValue, final.CurrentValue);
        Assert.Equal(52.5m, final.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity == $"H{laterHistoryId:D10}")
            .Sum(c => c.Value));
        Assert.Equal(receiptQuantity == 10 ? 50m : 16m,
            final.ActiveConsumptions.Where(c =>
                c.IssueKey.LineIdentity == $"H{transferHistoryId:D10}")
                .Sum(c => c.Value));
        Assert.Equal(receiptQuantity == 10,
            final.ActiveConsumptions.Any(c =>
                c.IssueKey.LineIdentity == $"H{transferHistoryId:D10}"
                && c.LayerId == receiptLayerId));
        Assert.Contains(final.ActiveConsumptions, c =>
            c.IssueKey.LineIdentity == $"H{laterHistoryId:D10}"
            && c.LayerId == receiptLayerId
            && c.Quantity == 3m && c.Value == 37.5m);
        Assert.Equal(remainingReceiptQuantity, final.Layers.Single(l =>
            l.LayerId == receiptLayerId).RemainingQuantity);
        var receiptHistory = await verify.IvTrxHistories.AsNoTracking()
            .SingleAsync(h => h.BatchNo == earlier.BatchNo
                && h.EntryRole == "ORIGINAL");
        var receiptKey = final.Layers.Single(l =>
            l.LayerId == receiptLayerId).ReceiptKey!.Value;
        var removalPhysical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            verify, "DEMO", "HQ", balId,
            new StockReplayPhysicalEvent(receiptHistory.Id,
                receiptKey, receiptQuantity));
        var removalPlan = await StockReplayFifoRemovalPlanner.BuildAsync(
            verify, "DEMO", "HQ", "A100", removalPhysical);
        Assert.Equal(7m, removalPlan.Projection.FinalQuantity);
        Assert.Equal(28m, removalPlan.Projection.FinalValue);
        await using var rollbackDb = await _factory.CreateDbContextAsync();
        await using var rollbackTransaction =
            await rollbackDb.Database.BeginTransactionAsync();
        var rollbackCommand = await InventoryStockPostingCommandFactory
            .CreateAsync(rollbackDb, "DEMO", "HQ", earlier.BatchNo,
                true, earlierDate, CancellationToken.None);
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            rollbackDb, rollbackCommand);
        Assert.NotNull(rollbackBegin.Context);
        var rollbackCommitted = false;
        try
        {
            var rollbackPermit = await StockReplayFifoRemovalPlanner
                .PrepareWithinRootAsync(rollbackBegin.Context!, removalPlan);
            var removed = await StockReplayFifoMrRemovalRootWriter.RemoveAsync(
                rollbackBegin.Context!, removalPlan, rollbackPermit,
                earlier.BatchNo);
            Assert.Equal(receiptQuantity == 10
                    ? new[] { laterHistoryId, transferHistoryId }
                    : [laterHistoryId],
                removed.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                rollbackBegin.Context!, removed.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                rollbackBegin.Context!, removalPlan, rollbackPermit);
            await rollbackTransaction.CommitAsync();
            rollbackCommitted = true;
        }
        finally
        {
            if (!rollbackCommitted)
            {
                await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
                await rollbackTransaction.RollbackAsync();
            }
        }
        await using var afterRollback = await _factory.CreateDbContextAsync();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(
            afterRollback, "DEMO", "HQ", "A100");
        Assert.Equal(7m, restored.CurrentQuantity);
        Assert.Equal(28m, restored.CurrentValue);
        Assert.Equal(27m, restored.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity == $"H{laterHistoryId:D10}")
            .Sum(c => c.Value));
        Assert.Equal(16m, restored.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity ==
                $"H{transferHistoryId:D10}").Sum(c => c.Value));
        Assert.DoesNotContain(restored.Layers, l =>
            l.LayerId == receiptLayerId);
    }

    [Fact]
    public async Task Save_writes_NEW_MR_staging_without_lot_or_balance()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(ValidNonLotLine(new DateTime(2026, 8, 24)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, result.BatchNo);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Null(batch.Remarks);
        var line = Assert.Single(batch.Details);
        Assert.Equal("A100", line.ICode);
        Assert.Equal("MAIN", line.ToWarehouse);
        Assert.Equal("BIN1", line.ToLocation);
        Assert.Equal(string.Empty, line.ToLotNo);
        Assert.Equal("EA", line.ToStdUom);
        Assert.Equal("RAW", line.IClassCode);
        Assert.Null(line.ExpiryDate);
        Assert.Null(line.ToLotId);
        Assert.Null(line.ToBalLocId);
        Assert.Equal(0, await db.IvLots.CountAsync());
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Save_allows_duplicate_detail_lines()
    {
        var sut = CreateSut();
        var line = ValidNonLotLineRequest();
        var result = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines = [Clone(line), Clone(line)]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IvTrxBatchDetails.CountAsync());
    }

    [Fact]
    public async Task Save_writes_header_remark()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            RefNo = "GRN-9",
            Remark = "  found in aisle  ",
            VendCode = "SUP01",
            Lines = [ValidNonLotLineRequest()]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync();
        Assert.Equal("found in aisle", batch.Remarks);
        Assert.Equal("GRN-9", batch.RefNo);
    }

    [Fact]
    public async Task Save_class_override_does_not_update_stock_master()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.IClassCode = "FG";
        var result = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines = [req]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var master = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100");
        Assert.Equal("RAW", master.IClassCode);
        Assert.Equal("FG", (await db.IvTrxBatchDetails.SingleAsync()).IClassCode);
    }

    [Fact]
    public async Task Save_rejects_inactive_item()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ICode = "DEAD";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_cross_company_item()
    {
        var sut = CreateSut();
        // OTHER company also has A100 but DEMO lookup finds DEMO A100 — use code only on OTHER by removing DEMO? 
        // Actually DEMO has A100. Seed a code only on OTHER:
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "XONLY",
                StdUom = "EA",
                IClassCode = "RAW",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var req = ValidNonLotLineRequest();
        req.ICode = "XONLY";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_rejects_inactive_warehouse()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "INACTIVE";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("warehouse", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_cross_branch_warehouse()
    {
        var sut = CreateSut();
        // BR2 MAIN exists but user is HQ — still named MAIN which exists on HQ. Use EMPTY on wrong branch:
        // Seed WH only on BR2:
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                WarehouseCode = "ONLYBR2",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "ONLYBR2";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_requires_location_when_warehouse_has_active_locations()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("location is required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_location_from_other_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WH2",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WH2",
                LocCode = "LOC2",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "WH2";
        req.ToLocation = "BIN1";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("location", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_inactive_location()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLocation = "DEAD";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_empty_warehouse_accepts_empty_location()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "EMPTY";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(string.Empty, (await db.IvTrxBatchDetails.SingleAsync()).ToLocation);
    }

    [Fact]
    public async Task Save_rejects_inactive_uom_class_status()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Uom = "OLD";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidNonLotLineRequest();
        req.IClassCode = "X";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidNonLotLineRequest();
        req.IStatus = "OLD";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_rejects_zero_and_negative_quantity()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Quantity = 0;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
        req.Quantity = -1;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_requires_lot_and_expiry_for_lot_controlled()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ToLotNo = null;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidLotLineRequest();
        req.ExpiryDate = null;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidLotLineRequest();
        req.ExpiryDate = FixedToday.AddDays(-1);
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_lot_today_and_future_accepted_without_creating_IvLot()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ExpiryDate = FixedToday;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.IvTrxBatchDetails.SingleAsync();
        Assert.Equal("260824001", line.ToLotNo);
        Assert.Equal(FixedToday, line.ExpiryDate);
        Assert.Null(line.ToLotId);
        Assert.Equal(0, await db.IvLots.CountAsync());
    }

    [Fact]
    public async Task Save_accepts_backdated_trx_when_expiry_is_after_transaction_date()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ExpiryDate = new DateTime(2026, 8, 23);
        var result = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = new DateTime(2026, 8, 20),
            VendCode = "SUP01",
            Lines = [req]
        });
        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task Save_rejects_expiry_before_future_transaction_date()
    {
        var req = ValidLotLineRequest();
        req.ExpiryDate = FixedToday;
        var result = await CreateSut().SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(1),
            VendCode = "SUP01",
            Lines = [req]
        });

        Assert.False(result.Succeeded);
        Assert.Contains("transaction date", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_non_lot_with_lotno_or_expiry()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLotNo = "SMUGGLE";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidNonLotLineRequest();
        req.ExpiryDate = FixedToday;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_normalizes_null_lot_on_non_lot_item()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLotNo = null;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(string.Empty, (await db.IvTrxBatchDetails.SingleAsync()).ToLotNo);
    }

    [Fact]
    public async Task Save_malformed_payload_returns_fail()
    {
        var sut = CreateSut();
        Assert.False((await sut.SaveNewAsync(null!)).Succeeded);
        Assert.False((await sut.SaveNewAsync(new IvMiscReceiptSaveRequest { VendCode = "SUP01", Lines = [] })).Succeeded);
        Assert.False((await sut.SaveNewAsync(new IvMiscReceiptSaveRequest { VendCode = "SUP01", Lines = [null!] })).Succeeded);

        var missingItem = ValidNonLotLineRequest();
        missingItem.ICode = "";
        Assert.False((await sut.SaveNewAsync(Wrap(missingItem))).Succeeded);

        var missingWh = ValidNonLotLineRequest();
        missingWh.ToWarehouse = "";
        Assert.False((await sut.SaveNewAsync(Wrap(missingWh))).Succeeded);
    }

    [Fact]
    public async Task Save_is_denied_without_ADD()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
    }

    [Fact]
    public async Task Sequential_saves_share_global_IV_BATCH_number()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        var second = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(1, first.BatchNo);
        Assert.Equal(2, second.BatchNo);
    }

    [Fact]
    public async Task Transaction_repo_rejects_update_delete_when_not_NEW()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()))).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        batch.BatchStatus = IvBatchStatuses.Posted;
        await db.SaveChangesAsync();

        var repo = new IvStockTransactionRepository();
        Assert.False(await repo.UpdateAsync(db, batch));
        Assert.False(await repo.DeleteNewAsync(db, "DEMO", "HQ", batch.BatchNo));
    }

    [Fact]
    public async Task Search_is_scoped_to_company_branch_and_MR_only()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()))).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                BatchNo = 9001,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "OTHER-BR"
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                BatchNo = 9002,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "OTHER-CO"
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9003,
                TrxDtTime = FixedToday,
                TrxType = "XI",
                BatchStatus = IvBatchStatuses.New,
                RefNo = "NOT-MR"
            });
            await db.SaveChangesAsync();
        }

        var result = await sut.SearchAsync(new IvMiscReceiptListQuery { Take = 50 });
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.ListPage);
        Assert.Single(result.ListPage.Rows);
        Assert.Equal(1, result.ListPage.Rows[0].BatchNo);
        Assert.Equal(1, result.ListPage.Rows[0].LineCount);
        Assert.Equal(125m, result.ListPage.Rows[0].TotalAmount);
    }

    [Fact]
    public async Task Get_returns_header_and_lines()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var result = await sut.GetAsync(saved.BatchNo);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Document);
        Assert.Equal(saved.BatchNo, result.Document.BatchNo);
        Assert.Equal(IvBatchStatuses.New, result.Document.BatchStatus);
        Assert.Single(result.Document.Lines);
        Assert.Equal("A100", result.Document.Lines[0].ICode);
        Assert.Equal("BIN1", result.Document.Lines[0].ToLocation);
        Assert.False(result.Document.Lines[0].LotControl);
        Assert.Equal("ADJ", result.Document.Lines[0].Reason);
        Assert.Equal("found stock", result.Document.Lines[0].Remarks);
        Assert.Equal("SUP01", result.Document.VendCode);
        Assert.Equal("Alpha Supplier", result.Document.VendName);
    }

    [Fact]
    public async Task Get_fails_for_unknown_or_wrong_type()
    {
        var sut = CreateSut();
        Assert.False((await sut.GetAsync(999)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 77,
                TrxDtTime = FixedToday,
                TrxType = "XI",
                BatchStatus = IvBatchStatuses.New,
                RefNo = "XI-77"
            });
            await db.SaveChangesAsync();
        }

        var wrongType = await sut.GetAsync(77);
        Assert.False(wrongType.Succeeded);
        Assert.Equal("Miscellaneous receipt was not found.", wrongType.ErrorMessage);
    }

    [Fact]
    public async Task Update_succeeds_for_NEW_and_fails_for_POSTED()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var updatedLine = ValidNonLotLineRequest();
        updatedLine.Quantity = 4;
        updatedLine.UnitPrice = 2;
        updatedLine.Remarks = "updated";
        var update = await sut.UpdateAsync(saved.BatchNo, new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            RefNo = "REF-UPD",
            Remark = "header note",
            VendCode = "SUP01",
            Lines = [updatedLine]
        });
        Assert.True(update.Succeeded, update.ErrorMessage);

        var loaded = await sut.GetAsync(saved.BatchNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.Equal("REF-UPD", loaded.Document!.RefNo);
        Assert.Equal("header note", loaded.Document.Remark);
        Assert.Equal(4m, loaded.Document.Lines[0].Quantity);
        Assert.Equal(2m, loaded.Document.Lines[0].UnitPrice);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
            batch.BatchStatus = IvBatchStatuses.Posted;
            await db.SaveChangesAsync();
        }

        var postedUpdate = await sut.UpdateAsync(saved.BatchNo, Wrap(ValidNonLotLineRequest()));
        Assert.False(postedUpdate.Succeeded);
        Assert.Equal("Only NEW miscellaneous receipts can be edited.", postedUpdate.ErrorMessage);
    }

    [Fact]
    public async Task Delete_succeeds_for_NEW_and_fails_for_POSTED()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        var second = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var posted = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == second.BatchNo);
            posted.BatchStatus = IvBatchStatuses.Posted;
            await db.SaveChangesAsync();
        }

        var postedDelete = await sut.DeleteAsync([second.BatchNo]);
        Assert.False(postedDelete.Succeeded);

        var deleted = await sut.DeleteAsync([first.BatchNo]);
        Assert.True(deleted.Succeeded, deleted.ErrorMessage);

        Assert.False((await sut.GetAsync(first.BatchNo)).Succeeded);
        Assert.True((await sut.GetAsync(second.BatchNo)).Succeeded);
    }

    [Fact]
    public async Task Save_allows_blank_vendor_and_rejects_inactive_or_foreign_when_provided()
    {
        var sut = CreateSut();
        var noVendor = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidNonLotLineRequest()]
        });
        Assert.True(noVendor.Succeeded, noVendor.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == noVendor.BatchNo);
            Assert.Null(batch.VendCode);
            Assert.Null(batch.VendName);
        }

        var inactive = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUPOFF",
            Lines = [ValidNonLotLineRequest()]
        });
        Assert.False(inactive.Succeeded);

        var otherBranch = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUPBR2",
            Lines = [ValidNonLotLineRequest()]
        });
        Assert.False(otherBranch.Succeeded);
    }

    [Fact]
    public async Task Save_snapshots_vendor_name_and_ignores_later_rename()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var supplier = await db.PoSuppliers.SingleAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == "SUP01");
            supplier.SuppName = "Renamed Alpha";
            await db.SaveChangesAsync();
        }

        var loaded = await sut.GetAsync(saved.BatchNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.Equal("SUP01", loaded.Document!.VendCode);
        Assert.Equal("Alpha Supplier", loaded.Document.VendName);
    }

    [Fact]
    public async Task Save_requires_reason_and_keeps_remarks_separate()
    {
        var sut = CreateSut();
        var missing = ValidNonLotLineRequest();
        missing.Reason = "   ";
        Assert.False((await sut.SaveNewAsync(Wrap(missing))).Succeeded);

        var tooLong = ValidNonLotLineRequest();
        tooLong.Reason = new string('R', 51);
        Assert.False((await sut.SaveNewAsync(Wrap(tooLong))).Succeeded);

        var withColon = ValidNonLotLineRequest();
        withColon.Reason = "ADJ:SPECIAL";
        withColon.Remarks = "note";
        var saved = await sut.SaveNewAsync(Wrap(withColon));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.IvTrxBatchDetails.SingleAsync();
        Assert.Equal("ADJ:SPECIAL", detail.Reason);
        Assert.Equal("note", detail.Remarks);
    }

    [Fact]
    public async Task Save_copies_supplier_do_to_all_details_and_rejects_overlength()
    {
        var sut = CreateSut();
        var tooLong = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            SupplierDoNo = new string('D', 31),
            Lines = [ValidNonLotLineRequest()]
        });
        Assert.False(tooLong.Succeeded);

        var line = ValidNonLotLineRequest();
        var saved = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            SupplierDoNo = "  DO-001  ",
            Lines = [Clone(line), Clone(line)]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var dos = await db.IvTrxBatchDetails.Select(x => x.DoNo).ToListAsync();
        Assert.Equal(2, dos.Count);
        Assert.All(dos, x => Assert.Equal("DO-001", x));
    }

    [Fact]
    public async Task Update_changes_vendor_reason_and_recopies_do()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            SupplierDoNo = "DO-001",
            Lines = [ValidNonLotLineRequest(), ValidNonLotLineRequest()]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var updatedLine = ValidNonLotLineRequest();
        updatedLine.Reason = "SAMPLE";
        updatedLine.Remarks = "keep me";
        var update = await sut.UpdateAsync(saved.BatchNo, new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP02",
            SupplierDoNo = "DO-002",
            Lines = [updatedLine, Clone(updatedLine)]
        });
        Assert.True(update.Succeeded, update.ErrorMessage);

        var loaded = await sut.GetAsync(saved.BatchNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.Equal("SUP02", loaded.Document!.VendCode);
        Assert.Equal("Beta Supplier", loaded.Document.VendName);
        Assert.Equal("DO-002", loaded.Document.SupplierDoNo);
        Assert.All(loaded.Document.Lines, x => Assert.Equal("SAMPLE", x.Reason));
        Assert.All(loaded.Document.Lines, x => Assert.Equal("keep me", x.Remarks));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.IvTrxBatchDetails.Where(x => x.BatchNo == saved.BatchNo).ToListAsync(),
            x => Assert.Equal("DO-002", x.DoNo));
    }

    [Fact]
    public async Task Get_legacy_null_vendor_does_not_mutate_and_can_post_when_reason_present()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync(x => x.BatchNo == saved.BatchNo);
            batch.VendCode = null;
            batch.VendName = null;
            foreach (var d in batch.Details)
            {
                d.Remarks = "legacy free text";
            }

            await db.SaveChangesAsync();
        }

        var loaded = await sut.GetAsync(saved.BatchNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.Null(loaded.Document!.VendCode);
        Assert.Null(loaded.Document.VendName);
        Assert.Equal("ADJ", loaded.Document.Lines[0].Reason);
        Assert.Equal("legacy free text", loaded.Document.Lines[0].Remarks);

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
    }

    [Fact]
    public async Task Post_fails_when_line_reason_missing()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync(x => x.BatchNo == saved.BatchNo);
            detail.Reason = null;
            await db.SaveChangesAsync();
        }

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("reason", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_succeeds_when_supplier_inactive_after_save_and_sets_lot_supplier()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        var saved = await sut.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            SupplierDoNo = new string('D', 30),
            Lines = [req]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var supplier = await db.PoSuppliers.SingleAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == "SUP01");
            supplier.IsActive = false;
            await db.SaveChangesAsync();
        }

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
            Assert.Equal("SUP01", batch.VendCode);
            Assert.Equal("Alpha Supplier", batch.VendName);

            var lot = await db.IvLots.SingleAsync();
            Assert.Equal("SUP01", lot.SupplierCode);

            var history = await db.IvTrxHistories.SingleAsync();
            Assert.Equal(new string('D', 30), history.DoNo);
        }

        var rb = await sut.RollbackAsync([saved.BatchNo]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync(x => x.BatchNo == saved.BatchNo);
            Assert.Equal("SUP01", batch.VendCode);
            Assert.Equal("Alpha Supplier", batch.VendName);
            Assert.Equal("FOUND", batch.Details.Single().Reason);
            Assert.Equal(new string('D', 30), batch.Details.Single().DoNo);
        }
    }

    [Fact]
    public async Task Post_does_not_overwrite_existing_lot_supplier_code()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.Add(new IvLot
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                LotNo = "260824001",
                SupplierCode = "OLD-SUP",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await sut.PostAsync([saved.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var lot = await verify.IvLots.SingleAsync(x => x.LotNo == "260824001");
        Assert.Equal("OLD-SUP", lot.SupplierCode);
    }

    private static IvMiscReceiptSaveRequest ValidNonLotLine(DateTime trxDate, string itemCode = "A100") =>
        new() { TrxDate = trxDate, RefNo = "AUTO", VendCode = "SUP01", Lines = [ValidNonLotLineRequest(itemCode)] };

    private static IvMiscReceiptSaveRequest Wrap(IvMiscReceiptLineRequest line) =>
        new() { TrxDate = FixedToday, VendCode = "SUP01", Lines = [line] };

    private static IvMiscReceiptLineRequest ValidNonLotLineRequest(string itemCode = "A100") =>
        new()
        {
            ICode = itemCode,
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            Quantity = 10,
            UnitPrice = 12.5m,
            PriceConfirmed = true,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = IvItemStatuses.Active,
            Reason = "ADJ",
            Remarks = "found stock"
        };

    private static IvMiscReceiptLineRequest ValidLotLineRequest() =>
        new()
        {
            ICode = "LOT1",
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            ToLotNo = "260824001",
            Quantity = 2,
            UnitPrice = 3,
            PriceConfirmed = true,
            Uom = "KG",
            IClassCode = "FG",
            IStatus = IvItemStatuses.Active,
            ExpiryDate = FixedToday.AddDays(10),
            Reason = "FOUND"
        };

    private static IvMiscReceiptLineRequest Clone(IvMiscReceiptLineRequest x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            ToWarehouse = x.ToWarehouse,
            ToLocation = x.ToLocation,
            ToLotNo = x.ToLotNo,
            Quantity = x.Quantity,
            Uom = x.Uom,
            IClassCode = x.IClassCode,
            IStatus = x.IStatus,
            PriceConfirmed = x.PriceConfirmed,
            UnitPrice = x.UnitPrice,
            ExpiryDate = x.ExpiryDate,
            Reason = x.Reason,
            Remarks = x.Remarks
        };

    private async Task<(ReceiptCostSnapshot State, FifoLayerSnapshot[] FifoLayers)> ReadCostingSnapshotAsync(
        string itemCode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var balance = await db.IvBalLocs.SingleAsync(x => x.ICode == itemCode);
        var history = await db.IvTrxHistories.SingleAsync(x => x.ICode == itemCode);
        var fact = await db.StockValuationFacts.SingleAsync(x => x.InventoryHistoryId == history.Id);
        var posting = await db.StockPostings.SingleAsync(x => x.Id == fact.StockPostingId);
        var cost = await db.StockCostStates.SingleAsync(x => x.ItemCode == itemCode);
        var fifoLayers = await db.StockFifoLayers
            .Where(x => x.ItemCode == itemCode)
            .OrderBy(x => x.ReceiptEffectiveAt)
            .Select(x => new FifoLayerSnapshot(
                x.ReceiptEffectiveAt,
                x.OriginalQty,
                x.RemainingQty,
                x.OriginalValue,
                x.AccumulatedAdjustment,
                x.RemainingValue,
                x.CurrentUnitCost,
                x.Status,
                x.WarehouseCode,
                x.LotNo))
            .ToArrayAsync();

        return (new ReceiptCostSnapshot(
            balance.StdQty,
            balance.WhCode,
            balance.LocCode,
            balance.LotNo,
            balance.IStatus,
            history.TrxDtTime,
            history.ToStdQty,
            history.ToStdUom,
            history.UnitPrice,
            history.CostEvidenceType,
            posting.EffectiveAt,
            posting.BusinessDate,
            fact.EffectiveAt,
            fact.BusinessDate,
            fact.BaseQty,
            fact.UnitCost,
            fact.CostAmount,
            fact.BaseCostAmount,
            fact.CostMethod,
            fact.ValuationSource,
            fact.ValuationStatus,
            cost.OnHandBaseQty,
            cost.InventoryValue,
            cost.CurrentUnitCost,
            cost.AverageUnitCost), fifoLayers);
    }

    private IvMiscReceiptService CreateSut(
        bool canAdd = true,
        bool canAccess = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canPost = true,
        bool canRollback = true,
        string? location = "SITE",
        DateTime? postingBusinessDate = null,
        DateTime? draftBusinessDate = null,
        bool withLedger = false,
        bool enableBackdatedReplay = false)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Add,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Edit,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Delete,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Post,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canPost);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryMiscReceipt,
                PermissionCodes.Rollback,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canRollback);

        var postingRepo = new IvStockPostingRepository();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: location);
        IIvInventoryHistoryWriter? historyWriter = null;
        IStockPostingCoordinator? coordinator = null;
        if (withLedger)
        {
            historyWriter = new IvInventoryHistoryWriter();
            coordinator = new StockPostingCoordinator(
                _factory,
                tenant,
                new BranchStockTransactionLock(),
                new StockPeriodGuard(),
                new NoActiveStockFreezeGuard(),
                new InventoryValuationService());
        }

        var posting = new IvInventoryPostingService(
            _factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            currentDate: new FixedCurrentDateService(postingBusinessDate ?? DateTime.MaxValue.Date),
            historyWriter: historyWriter,
            coordinator: coordinator,
            configuration: enableBackdatedReplay
                ? new ConfigurationManager
                {
                    ["OpenPeriodBackdatedReplayEnabled"] = "true"
                }
                : null);

        return new IvMiscReceiptService(
            _factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(draftBusinessDate ?? FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            new PoSupplierRepository(_factory),
            new IvUomConversionService(_factory),
            NullLogger<IvMiscReceiptService>.Instance);
    }

    private sealed record ReceiptCostSnapshot(
        decimal BalanceQty,
        string Warehouse,
        string Location,
        string LotNo,
        string ItemStatus,
        DateTime TrxDate,
        decimal? HistoryQty,
        string? HistoryUom,
        decimal? UnitPrice,
        string? CostEvidenceType,
        DateTime PostingEffectiveAt,
        DateTime PostingBusinessDate,
        DateTime FactEffectiveAt,
        DateTime FactBusinessDate,
        decimal BaseQty,
        decimal UnitCost,
        decimal CostAmount,
        decimal BaseCostAmount,
        string CostMethod,
        string ValuationSource,
        string ValuationStatus,
        decimal OnHandBaseQty,
        decimal InventoryValue,
        decimal CurrentUnitCost,
        decimal AverageUnitCost);

    private sealed record FifoLayerSnapshot(
        DateTime EffectiveAt,
        decimal OriginalQty,
        decimal RemainingQty,
        decimal OriginalValue,
        decimal AccumulatedAdjustment,
        decimal RemainingValue,
        decimal CurrentUnitCost,
        string Status,
        string? Warehouse,
        string? LotNo);
}
