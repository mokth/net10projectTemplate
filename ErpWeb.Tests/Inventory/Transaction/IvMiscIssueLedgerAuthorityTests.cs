using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMiscIssue)]
public sealed class IvMiscIssueLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvMiscIssueLedgerAuthorityTests()
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
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_issue_ignores_request_price_and_writes_valuation_fact()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(BalanceReferencePrice, detail.UnitPrice);
        }

        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync();
        Assert.Equal(-1, fact.Direction);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(90m, state.OnHandBaseQty);
        Assert.Equal(180m, state.InventoryValue);
    }

    [Fact]
    public async Task Fifo_issue_consumes_layers_and_ignores_request_price()
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

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "ISSUE_OUT")
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(27m, facts.Sum(x => x.CostAmount));
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
            Assert.Equal(StockValuationSources.Fifo, fact.ValuationSource);
        });

        var history = await verify.IvTrxHistories.SingleAsync();
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        var issueKey = snapshot.ActiveConsumptions[0].IssueKey;
        var activeEvent = new StockReplayPhysicalEvent(
            history.Id, issueKey, -8m);
        var physicalPlan = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId, activeEvent);
        var fifoPlan = await StockReplayFifoRemovalPlanner.BuildAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physicalPlan);
        Assert.Equal(15m, fifoPlan.Projection.FinalQuantity);
        Assert.Equal(55m, fifoPlan.Projection.FinalValue);
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(fifoPlan);
        var forged = fifoPlan with
        {
            Projection = fifoPlan.Projection with { FinalValue = 54m }
        };
        Assert.Equal(StockLedgerErrorCodes.StaleDocument,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoRemovalPlanner.ValidatePreparedPlan(forged))
                .Error.Code);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(rootDb,
            new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "FIFO_REMOVAL_PLAN_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = "PLAN_TEST",
                SourceDocumentId = "FIFO-REMOVE-1",
                SourceDocumentNo = "FIFO-REMOVE-1",
                DocumentRevision = 1,
                PostingRole = "PRIMARY",
                EffectiveAt = issueKey.EffectiveAt,
                Evidence = StockPostingFingerprint.Create(
                    new { Document = "FIFO-REMOVE-1" },
                    new { issueKey.EffectiveAt })
            });
        Assert.NotNull(begin.Context);
        try
        {
            var permit = await StockReplayFifoRemovalPlanner.PrepareWithinRootAsync(
                begin.Context!, fifoPlan);
            Assert.Equal("REMOVE", permit.Operation);
            Assert.Equal(history.Id, permit.RemovedHistoryId);
            Assert.Equal(15m, permit.FinalQuantity);
            Assert.Equal(55m, permit.FinalValue);
            var alteredSnapshot = fifoPlan.Snapshot with
            {
                Layers = fifoPlan.Snapshot.Layers
                    .Select(layer => layer with { RowVersionHex = "FORGED" })
                    .ToArray()
            };
            var alteredProjection = StockReplayFifoTailProjector.RemoveIssue(
                alteredSnapshot, issueKey);
            var companyCode = fifoPlan.CompanyCode;
            var branchCode = fifoPlan.BranchCode;
            var itemCode = fifoPlan.ItemCode;
            var alteredHash = StockPostingFingerprint.Create(new
            {
                companyCode, branchCode, itemCode,
                physicalPlan,
                snapshot = alteredSnapshot,
                projection = alteredProjection
            }, new { }).RequestFingerprint;
            var alteredPlan = fifoPlan with
            {
                Snapshot = alteredSnapshot,
                Projection = alteredProjection,
                PlanHash = alteredHash
            };
            StockReplayFifoRemovalPlanner.ValidatePreparedPlan(alteredPlan);
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoRemovalPlanner.ValidateWithinRootAsync(
                        begin.Context!, alteredPlan))).Error.Code);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }
        await verify.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE IvTrxHistory SET ToStdQty = 1 WHERE Id = {history.Id}");
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            (await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoRemovalPlanner.BuildAsync(
                    verify, InventoryLedgerTestFixture.CompanyCode,
                    InventoryLedgerTestFixture.BranchCode, "A100", physicalPlan)))
                .Error.Code);
    }

    [Fact]
    public async Task Fifo_reader_keeps_original_issue_key_after_cost_only_replacement()
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
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);

        await using var rootDb = await _factory.CreateDbContextAsync();
        var history = await rootDb.IvTrxHistories.SingleAsync();
        var originalFacts = await rootDb.StockValuationFacts
            .Where(f => f.InventoryHistoryId == history.Id)
            .OrderBy(f => f.SplitOrdinal).ToArrayAsync();
        var originalConsumptions = await rootDb.StockFifoLayerConsumptions
            .Where(c => originalFacts.Select(f => f.Id)
                .Contains(c.IssueValuationFactId))
            .OrderBy(c => c.SplitOrdinal).ToArrayAsync();
        Assert.Equal(2, originalFacts.Length);
        Assert.Equal(2, originalConsumptions.Length);
        var originalSequence = await rootDb.StockPostings
            .Where(p => p.Id == history.StockPostingId)
            .Select(p => p.PostingSequence).SingleAsync();

        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(rootDb,
            new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "FIFO_ISSUE_COST_REPLACEMENT_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = "REPLAY_TEST",
                SourceDocumentId = "FIFO-COST-1",
                SourceDocumentNo = "FIFO-COST-1",
                DocumentRevision = 1,
                PostingRole = "PRIMARY",
                EffectiveAt = history.TrxDtTime,
                Evidence = StockPostingFingerprint.Create(
                    new { Document = "FIFO-COST-1" },
                    new { history.TrxDtTime })
            });
        Assert.NotNull(begin.Context);
        var context = begin.Context!;
        var reversals = await StockReplayValuationReversalWriter.AppendAsync(
            context, history.Id, originalFacts.Select(f =>
                new StockReplayValuationReversalLine(
                    f.Id, 1, f.SplitOrdinal)).ToArray());
        var reverseByFact = reversals.ToDictionary(
            f => f.ReversesValuationFactId!.Value);
        foreach (var original in originalFacts)
        {
            var consumption = Assert.Single(originalConsumptions,
                c => c.IssueValuationFactId == original.Id);
            rootDb.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = reverseByFact[original.Id],
                FifoLayerId = consumption.FifoLayerId,
                ConsumedQty = consumption.ConsumedQty,
                ConsumedValue = consumption.ConsumedValue,
                SplitOrdinal = consumption.SplitOrdinal,
                ReversesConsumptionId = consumption.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
            var replacement = new StockValuationFact
            {
                CompanyCode = original.CompanyCode,
                BranchCode = original.BranchCode,
                LedgerEpochId = original.LedgerEpochId,
                StockPostingId = context.Posting.Id,
                PostingLineNo = 2,
                SplitOrdinal = original.SplitOrdinal,
                SourceLineId = original.SourceLineId,
                SourceDocumentType = original.SourceDocumentType,
                SourceDocumentId = original.SourceDocumentId,
                SourceDocumentNo = original.SourceDocumentNo,
                SourceDocumentLine = original.SourceDocumentLine,
                EffectiveAt = original.EffectiveAt,
                BusinessDate = original.BusinessDate,
                PeriodKey = original.PeriodKey,
                ItemCode = original.ItemCode,
                WarehouseCode = original.WarehouseCode,
                LocationCode = original.LocationCode,
                LotId = original.LotId,
                LotNo = original.LotNo,
                ItemStatus = original.ItemStatus,
                BaseUom = original.BaseUom,
                MovementCode = original.MovementCode,
                Direction = original.Direction,
                BaseQty = original.BaseQty,
                CostMethod = original.CostMethod,
                UnitCost = original.UnitCost,
                CostAmount = original.CostAmount,
                BaseCurrency = original.BaseCurrency,
                BaseCostAmount = original.BaseCostAmount,
                ValuationSource = StockValuationSources.Fifo,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = 2,
                InventoryHistoryId = history.Id,
                FromBalLocId = original.FromBalLocId,
                ToBalLocId = original.ToBalLocId,
                OriginalValuationFactId = original.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };
            rootDb.StockValuationFacts.Add(replacement);
            rootDb.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = replacement,
                FifoLayerId = consumption.FifoLayerId,
                ConsumedQty = consumption.ConsumedQty,
                ConsumedValue = consumption.ConsumedValue,
                SplitOrdinal = consumption.SplitOrdinal,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
        await coordinator.CompleteReplayInTransactionAsync(context, [history.Id]);
        await transaction.CommitAsync();

        await using var verify = await _factory.CreateDbContextAsync();
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(2, snapshot.ActiveConsumptions.Count);
        Assert.Equal(originalSequence,
            snapshot.ActiveConsumptions[0].IssueKey.OriginalPostingSequence);
        Assert.NotEqual(context.Posting.PostingSequence,
            snapshot.ActiveConsumptions[0].IssueKey.OriginalPostingSequence);
        Assert.Equal(15m, StockReplayFifoTailProjector.RemoveIssue(snapshot,
            snapshot.ActiveConsumptions[0].IssueKey).FinalQuantity);
        var replacementId = await verify.StockValuationFacts
            .Where(f => f.StockPostingId == context.Posting.Id
                && f.Direction == -1 && f.SplitOrdinal == 0)
            .Select(f => f.Id).SingleAsync();
        var unrelatedOrigin = snapshot.Layers[0].OriginFactId;
        await verify.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET OriginalValuationFactId = {unrelatedOrigin} WHERE Id = {replacementId}");
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            (await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoActiveLayerReader.ReadAsync(
                    verify, InventoryLedgerTestFixture.CompanyCode,
                    InventoryLedgerTestFixture.BranchCode, "A100"))).Error.Code);
    }

    [Fact]
    public async Task Fifo_repricing_writer_appends_a_complete_changed_issue_generation()
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
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);

        var candidateRequest = Request(balId, 1m);
        candidateRequest.TrxDate = FixedToday.AddDays(-1);
        var candidateSave = await mi.SaveNewAsync(candidateRequest);
        Assert.True(candidateSave.Succeeded, candidateSave.ErrorMessage);

        await using var planningDb = await _factory.CreateDbContextAsync();
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        var issueKey = snapshot.ActiveConsumptions[0].IssueKey;
        var nextSequence = await planningDb.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var candidate = new StockReplayPhysicalEvent(999_999,
            new StockReplayEventKey(FixedToday.AddDays(-1),
                StockReplayEventKey.OrdinaryClass, nextSequence,
                "H9999999999", 0), -1m);
        var physical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId, candidate);
        var financial = await StockReplayFifoInsertionPlanner.BuildAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical);
        var changedIssue = Assert.Single(financial.Projection.Issues,
            issue => !issue.IsCandidate);
        Assert.Equal(issueKey, changedIssue.Key);
        Assert.Equal(28m, changedIssue.Value);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(rootDb,
            new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "FIFO_ISSUE_REPRICE_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = "REPLAY_TEST",
                SourceDocumentId = "FIFO-REPRICE-1",
                SourceDocumentNo = "FIFO-REPRICE-1",
                DocumentRevision = 1,
                PostingRole = "PRIMARY",
                EffectiveAt = candidate.Key.EffectiveAt,
                Evidence = StockPostingFingerprint.Create(
                    new { Document = "FIFO-REPRICE-1" },
                    new { candidate.Key.EffectiveAt })
            });
        Assert.NotNull(begin.Context);
        try
        {
            var permit = await StockReplayFifoInsertionPlanner
                .PrepareWithinRootAsync(begin.Context!, financial);
            var replacements = await StockReplayFifoIssueRepricingWriter
                .AppendAsync(begin.Context!, financial, permit, issueKey, 1, 2);
            Assert.Equal(2, replacements.Count);
            Assert.Equal(8m, replacements.Sum(f => f.BaseQty));
            Assert.Equal(28m, replacements.Sum(f => f.CostAmount));
            Assert.All(replacements, f =>
            {
                Assert.Equal(issueKey.EffectiveAt, f.EffectiveAt);
                Assert.Equal(StockValuationSources.Fifo, f.ValuationSource);
                Assert.Equal(2, f.ValuationVersion);
            });
            await rootDb.SaveChangesAsync();
            Assert.Equal(2, await rootDb.StockValuationFacts
                .CountAsync(f => f.StockPostingId == begin.Context!.Posting.Id
                    && f.ReversesValuationFactId != null));
            Assert.Equal(2, await rootDb.StockFifoLayerConsumptions
                .CountAsync(c => c.IssueValuationFactId == replacements[0].Id
                    || c.IssueValuationFactId == replacements[1].Id));

            // The ordinary MI service still enforces its monotonic balance-date
            // guard. Stage the exact replay physical leg under this root instead.
            var batch = await rootDb.IvTrxBatches.SingleAsync(
                b => b.BatchNo == candidateSave.BatchNo);
            var detail = await rootDb.IvTrxBatchDetails.SingleAsync(
                d => d.BatchId == batch.Id);
            batch.BatchStatus = IvBatchStatuses.Posted;
            var balance = await rootDb.IvBalLocs.SingleAsync(b => b.Id == balId);
            balance.StdQty = physical.Projection.FinalQuantity;
            var candidateRow = new IvTrxHistory
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                BatchNo = candidateSave.BatchNo,
                TrxLineNo = detail.TrxLineNo,
                TrxType = IvTrxTypes.MiscellaneousIssue,
                BatchStatus = IvBatchStatuses.Posted,
                ICode = "A100",
                FromBalLocId = balId,
                FrStdQty = 1m
            };
            new IvInventoryHistoryWriter().StampGeneration(
                begin.Context!, [candidateRow], 1);
            rootDb.IvTrxHistories.Add(candidateRow);
            await rootDb.SaveChangesAsync();
            var candidateHistory = await rootDb.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.StockPostingId == begin.Context!.Posting.Id);
            var bound = await StockReplayFifoIssueCandidateBinder.BindAsync(
                begin.Context!, financial, permit, candidateHistory.Id);
            Assert.Equal($"H{candidateHistory.Id:D10}", bound.Key.LineIdentity);
            Assert.Equal(28m, bound.FinancialProjection.Issues
                .Single(i => !i.IsCandidate).Value);
            Assert.Equal(6m, bound.FinancialProjection.FinalQuantity);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueCandidateBinder.BindAsync(
                        begin.Context!, financial, permit, candidateHistory.Id + 1)))
                    .Error.Code);
            detail.FrStdQty = 2m;
            await rootDb.SaveChangesAsync();
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueCandidateBinder.BindAsync(
                        begin.Context!, financial, permit, candidateHistory.Id)))
                    .Error.Code);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Standard_issue_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.Direction == -1);
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
    }

    [Fact]
    public async Task Rollback_creates_exact_reversal()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await mi.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].UnitCost, facts[1].UnitCost);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
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

    private static IvMiscIssueSaveRequest Request(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = fromBalLocId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = TamperPrice
                }
            ]
        };
}
