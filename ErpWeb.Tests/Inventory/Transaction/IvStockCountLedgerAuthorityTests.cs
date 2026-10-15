using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]
public sealed class IvStockCountLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal PurchasePrice = 777m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockCountLedgerAuthorityTests()
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
        await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
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
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 100m,
            StdUom = "EA",
            UnitPrice = null,
            TransDate = FixedToday
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Positive_count_variance_uses_current_cost_not_purchase_price()
    {
        var service = InventoryLedgerTestFixture.CreateStockCount(_factory, FixedToday);
        var save = await service.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = FixedToday,
            WHCode = "MAIN",
            IncludeZeroQty = true
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.GenerateAsync(save.Id, discardCounts: false)).Succeeded);

        var sheet = await service.GetAsync(save.CountNo!);
        Assert.True(sheet.Succeeded, sheet.ErrorMessage);
        var line = Assert.Single(sheet.Document!.Lines);
        Assert.Equal(PurchasePrice, line.SnapshotUnitPrice);

        var counted = await service.SaveCountsAsync(
            sheet.Document.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 110m }],
            null);
        Assert.True(counted.Succeeded, counted.ErrorMessage);
        var post = await service.PostAsync(sheet.Document.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(110m, state.OnHandBaseQty);
        Assert.Equal(220m, state.InventoryValue);
    }

    [Fact]
    public async Task Count_owned_fast_adjustment_keeps_midnight_history_under_end_of_day_anchor()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var balanceId = await db.IvBalLocs.AsNoTracking().Select(b => b.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FAST", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balanceId,
            ICode = "A100", IStatus = "ACTIVE", PhysicalQty = 90m,
            SystemQty = 100m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 2m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        db.ChangeTracker.Clear();
        var sheetPlan = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.Equal(-10m, Assert.Single(sheetPlan.Lines).Projection.Candidate.SignedDelta);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        const int batchNo = 700;
        var batch = new IvTrxBatch
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            BatchNo = batchNo, TrxDtTime = FixedToday,
            TrxType = IvTrxTypes.StockAdjustment, BatchStatus = IvBatchStatuses.New,
            RefNo = "CC-FAST"
        };
        batch.Details.Add(new IvTrxBatchDetail
        {
            CompanyCode = batch.CompanyCode, BranchCode = batch.BranchCode,
            BatchNo = batchNo, TrxLineNo = 1, TrxType = IvTrxTypes.StockAdjustment,
            ICode = "A100", IStatus = "ACTIVE", IClassCode = "RAW",
            FromBalLocId = balanceId, FrWarehouse = "MAIN", FrLocation = "BIN1",
            FrLotNo = "", FrStdQty = 10m, FrStdUom = "EA",
            Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(
                IvAdjustmentReasons.Count, "CC-FAST")
        });
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(
                begin.Context!, sheetPlan);
            db.StockCountCostBases.Add(basis);
            db.IvTrxBatches.Add(batch);
            var trackedHeader = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
            trackedHeader.Status = IvStockCountStatuses.Posted;
            trackedHeader.PostedBatchNo = batchNo;
            await db.SaveChangesAsync();
            var result = await posting.PostStockCountAdjustmentInTransactionAsync(
                begin.Context!, db, batch.CompanyCode, batch.BranchCode,
                "TEST", batchNo, FixedToday);
            Assert.True(result.Succeeded, result.ErrorMessage);
            var history = Assert.Single(db.ChangeTracker.Entries<IvTrxHistory>()
                .Where(e => e.State == EntityState.Added).Select(e => e.Entity));
            Assert.Equal(FixedToday, history.TrxDtTime);
            Assert.Equal(begin.Context!.Posting.Id, history.StockPostingId);
            Assert.Equal(0, history.DocumentRevision);
            Assert.Equal("ORIGINAL", history.EntryRole);
            await db.SaveChangesAsync();
            var generations = await StockCountInitialGenerationWriter.AppendAsync(
                begin.Context!, permit, batchNo);
            var generation = Assert.Single(generations);
            Assert.Equal(-10m, generation.SignedAdjustmentQty);
            Assert.Equal(history.Id, generation.AdjustmentHistoryId);
            Assert.Equal(batchNo, generation.AdjustmentBatchNo);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_inbound_generation_values_and_seals_with_approved_frozen_cost(
        bool standardCost)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        long? standardRevisionId = null;
        if (standardCost)
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 2m, StockCostMethods.Standard);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
            standardRevisionId = await db.ItemStandardCostRevisions.AsNoTracking()
                .Select(r => r.Id).SingleAsync();
        }
        var balanceId = await db.IvBalLocs.AsNoTracking().Select(b => b.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-INBOUND", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balanceId,
            ICode = "A100", IStatus = "ACTIVE", PhysicalQty = 110m,
            SystemQty = 100m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var approvedAt = DateTime.UtcNow;
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 2m,
            CostBasisKind = standardCost
                ? StockCountCostBasisKinds.StandardRevision
                : StockCountCostBasisKinds.ManualApproved,
            StandardCostRevisionId = standardRevisionId,
            CostEvidenceType = standardCost ? null : StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = standardCost ? null : "Verified count cost",
            CostApprovedBy = standardCost ? null : "manager",
            CostApprovedAtUtc = standardCost ? null : approvedAt,
            CreatedAtUtc = approvedAt
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        if (standardCost)
        {
            basis.FrozenFallbackUnitCost = 3m;
            var incorrectRevisionCost = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountAnchorCommandFactory.CreateAsync(
                    db, header.CompanyCode, header.BranchCode, header.Id, [basis]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                incorrectRevisionCost.Error.Code);
            basis.FrozenFallbackUnitCost = 2m;
        }
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(root, sheet);
            basis.FrozenFallbackUnitCost = 3m;
            Assert.False(StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(root, [basis]));
            basis.FrozenFallbackUnitCost = 2m;
            db.StockCountCostBases.Add(basis);
            await db.SaveChangesAsync();
            const int batchNo = 701;
            var batch = new IvTrxBatch
            {
                CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
                BatchNo = batchNo, TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.StockAdjustment, BatchStatus = IvBatchStatuses.New,
                RefNo = header.CountNo
            };
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = batch.CompanyCode, BranchCode = batch.BranchCode,
                BatchNo = batchNo, TrxLineNo = 1, TrxType = IvTrxTypes.StockAdjustment,
                ICode = "A100", IStatus = "ACTIVE", IClassCode = "RAW",
                ToBalLocId = balanceId, ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "", ToStdQty = 10m, ToStdUom = "EA",
                UnitPrice = 2m, StockCountCostBasisId = basis.Id,
                CostEvidenceType = standardCost ? null : InventoryCostEvidenceTypes.ManualApproved,
                CostOverrideReason = basis.CostOverrideReason,
                CostApprovedBy = basis.CostApprovedBy,
                CostApprovedAtUtc = basis.CostApprovedAtUtc,
                Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(
                    IvAdjustmentReasons.Count, header.CountNo)
            });
            db.IvTrxBatches.Add(batch);
            var trackedHeader = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
            trackedHeader.Status = IvStockCountStatuses.Posted;
            trackedHeader.PostedBatchNo = batchNo;
            await db.SaveChangesAsync();

            var result = await posting.PostStockCountAdjustmentInTransactionAsync(
                root, db, batch.CompanyCode, batch.BranchCode,
                "TEST", batchNo, FixedToday);
            Assert.True(result.Succeeded, result.ErrorMessage);
            await db.SaveChangesAsync();
            var countHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.StockPostingId == root.Posting.Id);
            var orphan = await Assert.ThrowsAsync<StockLedgerException>(() =>
                coordinator.CompleteReplayInTransactionAsync(root,
                    [countHistory.Id]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, orphan.Error.Code);
            Assert.Null(root.Posting.SealedAtUtc);
            var generations = await StockCountInitialGenerationWriter.AppendAsync(
                root, permit, batchNo);
            Assert.Equal(10m, Assert.Single(generations).SignedAdjustmentQty);
            await coordinator.CompleteInTransactionAsync(root);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }

        db.ChangeTracker.Clear();
        var fact = await db.StockValuationFacts.SingleAsync(f => f.MovementCode == "ADJUST_IN");
        Assert.Equal(10m, fact.BaseQty);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        // Existing on-hand WAC is authoritative; its approved basis remains
        // frozen fallback evidence. Standard uses the event-date revision.
        Assert.Equal(standardCost ? StockValuationSources.Standard
            : StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.Equal(110m, (await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == (standardCost ? StockCostMethods.Standard
                : StockCostMethods.MovingAverage))).OnHandBaseQty);
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
        var beforeCount = await StockReplayPhysicalAsOfReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            FixedToday.AddHours(12));
        var afterCount = await StockReplayPhysicalAsOfReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            StockReplayEventKey.CountEndOfDay(FixedToday));
        Assert.Equal(100m, beforeCount[balanceId].AsOfBaseQty);
        Assert.Equal(110m, afterCount[balanceId].AsOfBaseQty);
    }

    [Fact]
    public async Task Generic_adjustment_post_and_rollback_reject_a_count_generation_batch()
    {
        var service = InventoryLedgerTestFixture.CreateStockCount(_factory, FixedToday);
        var saved = await service.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = FixedToday, WHCode = "MAIN", IncludeZeroQty = true
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await service.GenerateAsync(saved.Id, discardCounts: false)).Succeeded);
        var sheet = (await service.GetAsync(saved.CountNo!)).Document!;
        var line = Assert.Single(sheet.Lines);
        Assert.True((await service.SaveCountsAsync(sheet.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 110m }],
            null)).Succeeded);
        var posted = await service.PostAsync(sheet.Id);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        var batchNo = posted.PostedBatchNo!.Value;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
            var history = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == batchNo);
            var anchor = await db.StockPostings.SingleAsync(p => p.Id == history.StockPostingId);
            db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                StockCountLineId = line.Id,
                AnchorPostingId = anchor.Id, WritePostingId = anchor.Id,
                AnchorPostingSequence = anchor.PostingSequence,
                EffectiveAt = StockReplayEventKey.CountEndOfDay(FixedToday),
                SignedAdjustmentQty = 10m, AdjustmentBatchNo = batchNo,
                AdjustmentHistoryId = history.Id, CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var repost = await posting.PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);
        Assert.False(repost.Succeeded);
        Assert.Contains("Stock Count workflow", repost.ErrorMessage);
        var rollback = await posting.RollbackAsync(IvTrxTypes.StockAdjustment, [batchNo]);
        Assert.False(rollback.Succeeded);
        Assert.Contains("Stock Count workflow", rollback.ErrorMessage);
        var countRollback = await service.RollbackAsync(sheet.Id, "recount");
        Assert.False(countRollback.Succeeded);
        Assert.Contains("Replay-aware rollback", countRollback.ErrorMessage);
        var recover = await service.RecoverAsync(sheet.Id);
        Assert.False(recover.Succeeded);
        Assert.Contains("Reconcile active lineage", recover.ErrorMessage);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.Posted,
            (await verify.IvTrxBatches.SingleAsync(b => b.BatchNo == batchNo)).BatchStatus);
        Assert.Equal(IvStockCountStatuses.Posted,
            (await verify.IvStockCountHdrs.SingleAsync(h => h.Id == sheet.Id)).Status);

        // Even a stale ROLLED_BACK header cannot invite legacy edits, cancel,
        // or repost while an append-only V2 generation still owns the sheet.
        var staleHeader = await verify.IvStockCountHdrs.SingleAsync(h => h.Id == sheet.Id);
        staleHeader.Status = IvStockCountStatuses.RolledBack;
        await verify.SaveChangesAsync();
        var recount = await service.SaveCountsAsync(sheet.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 111m }],
            null);
        Assert.False(recount.Succeeded);
        Assert.Contains("posted count evidence", recount.ErrorMessage);
        var cancel = await service.CancelAsync([sheet.Id]);
        Assert.False(cancel.Succeeded);
        Assert.Contains("V2 count generations", cancel.ErrorMessage);
        var countRepost = await service.PostAsync(sheet.Id);
        Assert.False(countRepost.Succeeded);
        Assert.Contains("Replay-aware repost", countRepost.ErrorMessage);
    }
}
