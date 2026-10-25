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
        var laterHistoryId = await planningDb.IvTrxHistories
            .Where(h => h.BatchNo == save.BatchNo && h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).SingleAsync();
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
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, candidateSave.BatchNo,
            false, candidate.Key.EffectiveAt, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb,
            command);
        Assert.NotNull(begin.Context);
        var committed = false;
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

            var stagedDetail = await rootDb.IvTrxBatchDetails.SingleAsync(
                d => d.BatchNo == candidateSave.BatchNo);
            stagedDetail.FrStdQty = 2m;
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssuePhysicalWriter.AppendAsync(
                        begin.Context!, financial, permit, candidateSave.BatchNo)))
                    .Error.Code);
            Assert.Equal(7m, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            stagedDetail.FrStdQty = 1m;
            var bound = await StockReplayFifoIssuePhysicalWriter.AppendAsync(
                begin.Context!, financial, permit, candidateSave.BatchNo);
            var candidateHistory = await rootDb.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.StockPostingId == begin.Context!.Posting.Id);
            Assert.Equal($"H{candidateHistory.Id:D10}", bound.Key.LineIdentity);
            Assert.Equal(28m, bound.FinancialProjection.Issues
                .Single(i => !i.IsCandidate).Value);
            Assert.Equal(6m, bound.FinancialProjection.FinalQuantity);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueCandidateBinder.BindAsync(
                        begin.Context!, financial, permit, candidateHistory.Id + 1)))
                    .Error.Code);
            var candidateFacts = await StockReplayFifoIssueValuationWriter.AppendAsync(
                begin.Context!, financial, permit, bound.HistoryId, 3);
            Assert.Equal(3m, Assert.Single(candidateFacts).CostAmount);
            await rootDb.SaveChangesAsync();
            Assert.Equal(1, await rootDb.StockFifoLayerConsumptions
                .CountAsync(c => c.IssueValuationFactId == candidateFacts[0].Id));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueValuationWriter.AppendAsync(
                        begin.Context!, financial, permit, bound.HistoryId, 3)))
                    .Error.Code);
            Assert.Equal(StockValuationStatuses.Valued,
                (await rootDb.IvTrxHistories.SingleAsync(
                    h => h.Id == candidateHistory.Id)).ValuationStatus);
            var detail = await rootDb.IvTrxBatchDetails.SingleAsync(
                d => d.BatchNo == candidateSave.BatchNo);
            detail.FrStdQty = 2m;
            await rootDb.SaveChangesAsync();
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueCandidateBinder.BindAsync(
                        begin.Context!, financial, permit, candidateHistory.Id)))
                    .Error.Code);
            detail.FrStdQty = 1m;
            await rootDb.SaveChangesAsync();
            await StockReplayFifoStateWriter.ApplyIssueInsertionAsync(
                begin.Context!, financial, permit);
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, [laterHistoryId, candidateHistory.Id]);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, financial, permit);
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
        var finalSnapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(financial.Projection.FinalQuantity, finalSnapshot.CurrentQuantity);
        Assert.Equal(financial.Projection.FinalValue, finalSnapshot.CurrentValue);
        Assert.Equal(28m, (await StockReplayValuationGenerationResolver.ResolveAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode,
            [await verify.IvTrxHistories.SingleAsync(h => h.Id == laterHistoryId)]))
            [laterHistoryId].Sum(f => f.CostAmount));
    }

    [Fact]
    public async Task Fifo_removed_earlier_issue_reprices_later_issue_and_commits()
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
            (await db.IvBalLocs.SingleAsync(b => b.Id == balId)).TransDate =
                FixedToday.AddDays(-2);
            await db.SaveChangesAsync();
        }
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var earlier = Request(balId, 4m);
        earlier.TrxDate = FixedToday.AddDays(-1);
        var earlierSave = await mi.SaveNewAsync(earlier);
        Assert.True(earlierSave.Succeeded, earlierSave.ErrorMessage);
        Assert.True((await mi.PostAsync([earlierSave.BatchNo])).Succeeded);
        var laterSave = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(laterSave.Succeeded, laterSave.ErrorMessage);
        Assert.True((await mi.PostAsync([laterSave.BatchNo])).Succeeded);

        await using var planningDb = await _factory.CreateDbContextAsync();
        var before = await StockReplayFifoActiveLayerReader.ReadAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(3m, before.CurrentQuantity);
        Assert.Equal(12m, before.CurrentValue);
        var originals = await planningDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.EntryRole == "ORIGINAL")
            .ToDictionaryAsync(h => h.BatchNo);
        var removedHistory = originals[earlierSave.BatchNo];
        var laterHistory = originals[laterSave.BatchNo];
        var removedKey = before.ActiveConsumptions.First(c =>
            c.IssueKey.LineIdentity == $"H{removedHistory.Id:D10}").IssueKey;
        var laterKey = before.ActiveConsumptions.First(c =>
            c.IssueKey.LineIdentity == $"H{laterHistory.Id:D10}").IssueKey;
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId,
            new StockReplayPhysicalEvent(removedHistory.Id, removedKey, -4m));
        var plan = await StockReplayFifoRemovalPlanner.BuildAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical);
        Assert.Equal(7m, plan.Projection.FinalQuantity);
        Assert.Equal(28m, plan.Projection.FinalValue);
        Assert.Equal(27m, Assert.Single(plan.Projection.Issues).Value);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlierSave.BatchNo,
            true, removedKey.EffectiveAt, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        try
        {
            var permit = await StockReplayFifoRemovalPlanner
                .PrepareWithinRootAsync(begin.Context!, plan);
            var stagedDetail = await rootDb.IvTrxBatchDetails.SingleAsync(
                d => d.BatchNo == earlierSave.BatchNo);
            stagedDetail.FrStdQty = 5m;
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueRemovalPhysicalWriter.AppendAsync(
                        begin.Context!, plan, permit, earlierSave.BatchNo)))
                    .Error.Code);
            Assert.Equal(3m, (await rootDb.IvBalLocs.SingleAsync(
                b => b.Id == balId)).StdQty);
            stagedDetail.FrStdQty = 4m;
            var reversalHistoryId = await StockReplayFifoIssueRemovalPhysicalWriter
                .AppendAsync(begin.Context!, plan, permit, earlierSave.BatchNo);
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoIssueRemovalValuationWriter.AppendAsync(
                        begin.Context!, plan, permit, reversalHistoryId + 1, 1)))
                    .Error.Code);
            var removedFacts = await StockReplayFifoIssueRemovalValuationWriter
                .AppendAsync(begin.Context!, plan, permit, reversalHistoryId, 1);
            Assert.Single(removedFacts);
            Assert.Equal(12m, removedFacts.Sum(f => f.CostAmount));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                (await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayFifoStateWriter.ApplyIssueRemovalAsync(
                        begin.Context!, plan, permit))).Error.Code);
            var replacements = await StockReplayFifoIssueRepricingWriter.AppendAsync(
                begin.Context!, plan, permit, laterKey, 2, 3);
            Assert.Equal(27m, replacements.Sum(f => f.CostAmount));
            await StockReplayFifoStateWriter.ApplyIssueRemovalAsync(
                begin.Context!, plan, permit);
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, [laterHistory.Id]);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, plan, permit);
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
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(7m, final.CurrentQuantity);
        Assert.Equal(28m, final.CurrentValue);
        Assert.DoesNotContain(final.ActiveConsumptions,
            c => c.IssueKey == removedKey);
        Assert.Equal(27m, (await StockReplayValuationGenerationResolver.ResolveAsync(
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode,
            [await verify.IvTrxHistories.SingleAsync(h => h.Id == laterHistory.Id)]))
            [laterHistory.Id].Sum(f => f.CostAmount));
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync(
            b => b.BatchNo == earlierSave.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Fifo_mi_root_writer_reprices_two_later_issues()
    {
        var balId = await SeedBalanceAsync(20m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(15m, 4m, new DateTime(2026, 3, 1))
            ]);
            (await db.IvBalLocs.SingleAsync(b => b.Id == balId)).TransDate =
                FixedToday.AddDays(-2);
            await db.SaveChangesAsync();
        }
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var earlierRequest = Request(balId, 3m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await mi.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        Assert.True((await mi.PostAsync([earlier.BatchNo])).Succeeded);
        var laterOne = await mi.SaveNewAsync(Request(balId, 4m));
        Assert.True(laterOne.Succeeded, laterOne.ErrorMessage);
        Assert.True((await mi.PostAsync([laterOne.BatchNo])).Succeeded);
        var laterTwo = await mi.SaveNewAsync(Request(balId, 6m));
        Assert.True(laterTwo.Succeeded, laterTwo.ErrorMessage);
        Assert.True((await mi.PostAsync([laterTwo.BatchNo])).Succeeded);

        await using var planningDb = await _factory.CreateDbContextAsync();
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(7m, snapshot.CurrentQuantity);
        Assert.Equal(28m, snapshot.CurrentValue);
        var histories = await planningDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.EntryRole == "ORIGINAL")
            .ToDictionaryAsync(h => h.BatchNo);
        var removed = histories[earlier.BatchNo];
        var key = snapshot.ActiveConsumptions.First(c =>
            c.IssueKey.LineIdentity == $"H{removed.Id:D10}").IssueKey;
        var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId,
            new StockReplayPhysicalEvent(removed.Id, key, -3m));
        var plan = await StockReplayFifoRemovalPlanner.BuildAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical);
        Assert.Equal(10m, plan.Projection.FinalQuantity);
        Assert.Equal(40m, plan.Projection.FinalValue);
        Assert.Equal(2, plan.Projection.Issues.Count);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            true, key.EffectiveAt, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        try
        {
            var permit = await StockReplayFifoRemovalPlanner.PrepareWithinRootAsync(
                begin.Context!, plan);
            var written = await StockReplayFifoMiRootWriter.RemoveAsync(
                begin.Context!, plan, permit, earlier.BatchNo);
            Assert.Equal(2, written.RequiredActiveHistoryIds.Count);
            Assert.Equal(new[] { histories[laterOne.BatchNo].Id,
                    histories[laterTwo.BatchNo].Id },
                written.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, written.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, plan, permit);
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
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(10m, final.CurrentQuantity);
        Assert.Equal(40m, final.CurrentValue);
        Assert.Equal(12m, final.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity ==
                $"H{histories[laterOne.BatchNo].Id:D10}").Sum(c => c.Value));
        Assert.Equal(23m, final.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity ==
                $"H{histories[laterTwo.BatchNo].Id:D10}").Sum(c => c.Value));
    }

    [Fact]
    public async Task Fifo_mi_root_writer_inserts_earlier_issue_and_commits()
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
        var later = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await mi.PostAsync([later.BatchNo])).Succeeded);
        var earlierRequest = Request(balId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        var earlier = await mi.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);

        await using var planningDb = await _factory.CreateDbContextAsync();
        var laterHistoryId = await planningDb.IvTrxHistories.AsNoTracking()
            .Where(h => h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL")
            .Select(h => h.Id).SingleAsync();
        var nextSequence = await planningDb.StockPostingBranchSequences
            .Select(s => s.LastSequence).SingleAsync() + 1;
        var candidate = new StockReplayPhysicalEvent(999_999,
            new StockReplayEventKey(earlierRequest.TrxDate,
                StockReplayEventKey.OrdinaryClass, nextSequence,
                "H9999999999", 0), -1m);
        var physical = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, balId, candidate);
        var plan = await StockReplayFifoInsertionPlanner.BuildAsync(
            planningDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", physical);

        await using var rootDb = await _factory.CreateDbContextAsync();
        await using var transaction = await rootDb.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var command = await InventoryStockPostingCommandFactory.CreateAsync(
            rootDb, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, earlier.BatchNo,
            false, candidate.Key.EffectiveAt, CancellationToken.None);
        var begin = await coordinator.BeginInTransactionAsync(rootDb, command);
        Assert.NotNull(begin.Context);
        var committed = false;
        try
        {
            var permit = await StockReplayFifoInsertionPlanner.PrepareWithinRootAsync(
                begin.Context!, plan);
            var written = await StockReplayFifoMiRootWriter.InsertAsync(
                begin.Context!, plan, permit, earlier.BatchNo);
            Assert.Equal(2, written.RequiredActiveHistoryIds.Count);
            Assert.Equal(laterHistoryId, written.RequiredActiveHistoryIds[0]);
            Assert.Equal(written.RootHistoryId,
                written.RequiredActiveHistoryIds[1]);
            await coordinator.CompleteReplayInTransactionAsync(
                begin.Context!, written.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(
                begin.Context!, plan, permit);
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
            verify, InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100");
        Assert.Equal(6m, final.CurrentQuantity);
        Assert.Equal(24m, final.CurrentValue);
        Assert.Equal(28m, final.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity == $"H{laterHistoryId:D10}")
            .Sum(c => c.Value));
    }

    [Fact]
    public async Task Wac_backdated_lot_issue_posts_and_rolls_back_before_later_issue()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }

        var ordinary = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await ordinary.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await ordinary.PostAsync([later.BatchNo])).Succeeded);

        var replay = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var earlierRequest = Request(balId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        earlierRequest.Lines![0].FrLotNo = "LOT-1";
        var earlier = await replay.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "OTHER-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await replay.PostAsync([earlier.BatchNo])).Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.False(await unchanged.IvTrxHistories.AnyAsync(h =>
                h.BatchNo == earlier.BatchNo));
            var detail = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "LOT-1";
            await unchanged.SaveChangesAsync();
        }
        var postedResult = await replay.PostAsync([earlier.BatchNo]);
        Assert.True(postedResult.Succeeded, postedResult.ErrorMessage);

        await using (var posted = await _factory.CreateDbContextAsync())
        {
            var balance = await posted.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(6m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            var history = await posted.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == earlier.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.FromLotId);
            Assert.Equal("LOT-1", history.FrLotNo);
            var fact = await posted.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id
                && f.OriginalValuationFactId == null);
            Assert.Equal(lotId, fact.LotId);
            Assert.Equal("LOT-1", fact.LotNo);
            Assert.Equal(3m, fact.CostAmount);
            Assert.Equal(6m, (await posted.StockCostStates.SingleAsync()).OnHandBaseQty);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "OTHER-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await replay.RollbackAsync([earlier.BatchNo])).Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            var detail = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "LOT-1";
            await unchanged.SaveChangesAsync();
        }
        var rollbackResult = await replay.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollbackResult.Succeeded, rollbackResult.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            var balance = await restored.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(7m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            Assert.Equal(21m,
                (await restored.StockCostStates.SingleAsync()).InventoryValue);
        }
    }

    [Fact]
    public async Task Standard_backdated_lot_issue_posts_and_rolls_back_before_later_issue()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
        }

        var ordinary = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await ordinary.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await ordinary.PostAsync([later.BatchNo])).Succeeded);

        var replay = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var earlierRequest = Request(balId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        earlierRequest.Lines![0].FrLotNo = "LOT-1";
        var earlier = await replay.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var post = await replay.PostAsync([earlier.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var posted = await _factory.CreateDbContextAsync())
        {
            var balance = await posted.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(6m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            var history = await posted.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == earlier.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.FromLotId);
            var fact = await posted.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id
                && f.OriginalValuationFactId == null);
            Assert.Equal(lotId, fact.LotId);
            Assert.Equal("LOT-1", fact.LotNo);
            Assert.Equal(6.5m, fact.CostAmount);
            Assert.Equal(39m,
                (await posted.StockCostStates.SingleAsync()).InventoryValue);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "OTHER-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await replay.RollbackAsync([earlier.BatchNo])).Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            var detail = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "LOT-1";
            await unchanged.SaveChangesAsync();
        }
        var rollback = await replay.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            var balance = await restored.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(7m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            Assert.Equal(45.5m,
                (await restored.StockCostStates.SingleAsync()).InventoryValue);
        }
    }

    [Fact]
    public async Task Fifo_backdated_lot_issue_posts_and_rolls_back_before_later_issue()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-20)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-15))
            ]);
        }

        var ordinary = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await ordinary.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await ordinary.PostAsync([later.BatchNo])).Succeeded);

        var replay = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var earlierRequest = Request(balId, 1m);
        earlierRequest.TrxDate = FixedToday.AddDays(-1);
        earlierRequest.Lines![0].FrLotNo = "LOT-1";
        var earlier = await replay.SaveNewAsync(earlierRequest);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var post = await replay.PostAsync([earlier.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using (var posted = await _factory.CreateDbContextAsync())
        {
            var balance = await posted.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(6m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            var history = await posted.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == earlier.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.FromLotId);
            var facts = await posted.StockValuationFacts.Where(f =>
                f.InventoryHistoryId == history.Id
                && f.OriginalValuationFactId == null).ToArrayAsync();
            Assert.Single(facts);
            Assert.Equal(lotId, facts[0].LotId);
            Assert.Equal("LOT-1", facts[0].LotNo);
            Assert.Equal(3m, facts[0].CostAmount);
            Assert.Equal(24m,
                (await posted.StockCostStates.SingleAsync()).InventoryValue);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "OTHER-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await replay.RollbackAsync([earlier.BatchNo])).Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            var detail = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == earlier.BatchNo);
            detail.FrLotNo = "LOT-1";
            await unchanged.SaveChangesAsync();
        }
        var rollback = await replay.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            var balance = await restored.IvBalLocs.SingleAsync(b => b.Id == balId);
            Assert.Equal(7m, balance.StdQty);
            Assert.Equal(FixedToday, balance.TransDate);
            Assert.Equal(28m,
                (await restored.StockCostStates.SingleAsync()).InventoryValue);
        }
    }

    [Theory]
    [InlineData("WAC")]
    [InlineData("FIFO")]
    [InlineData("STANDARD")]
    public async Task Newer_lot_date_without_complete_replay_tail_never_falls_back(
        string method)
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (method == "FIFO")
            {
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                    [new FifoLayerSeed(15m, 3m, FixedToday.AddDays(-5))]);
            }
            else if (method == "STANDARD")
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 3m);
                await InventoryLedgerTestFixture.SeedCostStateAsync(
                    db, "A100", 15m, 3m, StockCostMethods.Standard);
            }
            else
            {
                await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 15m, 3m);
            }
        }
        var replay = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        request.Lines![0].FrLotNo = "LOT-1";
        var saved = await replay.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var rejected = await replay.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("not supported", rejected.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain("is dated", rejected.ErrorMessage ?? string.Empty);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await verify.IvBalLocs.SingleAsync(b => b.Id == balId)).StdQty);
        Assert.Equal(IvBatchStatuses.New,
            (await verify.IvTrxBatches.SingleAsync(b => b.BatchNo == saved.BatchNo)).BatchStatus);
        Assert.False(await verify.IvTrxHistories.AnyAsync(h => h.BatchNo == saved.BatchNo));
    }

    [Fact]
    public async Task Newer_lot_date_keeps_legacy_error_when_replay_is_off()
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var ordinary = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        request.Lines![0].FrLotNo = "LOT-1";
        var saved = await ordinary.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var rejected = await ordinary.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("Stock lot LOT-1 is dated", rejected.ErrorMessage ?? string.Empty);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await verify.IvBalLocs.SingleAsync(b => b.Id == balId)).StdQty);
        Assert.False(await verify.IvTrxHistories.AnyAsync(h => h.BatchNo == saved.BatchNo));
    }

    [Fact]
    public async Task Newer_receipt_lot_date_without_value_tail_never_falls_back()
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 1m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 3m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var rejected = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("not supported", rejected.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain("is dated", rejected.ErrorMessage ?? string.Empty);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await verify.IvBalLocs.SingleAsync(b => b.Id == balId)).StdQty);
        Assert.False(await verify.IvTrxHistories.AnyAsync(h => h.BatchNo == saved.BatchNo));
    }

    [Fact]
    public async Task Backdated_lot_issue_uses_quantity_before_later_receipt()
    {
        var (balId, _) = await SeedLotBalanceAsync(5m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 5m, 3m);
        }

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, FixedToday);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 10m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 3m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var receiptPost = await receipt.PostAsync([later.BatchNo]);
        Assert.True(receiptPost.Succeeded, receiptPost.ErrorMessage);

        var replay = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var invalidRequest = Request(balId, 8m);
        invalidRequest.TrxDate = FixedToday.AddDays(-1);
        invalidRequest.Lines![0].FrLotNo = "LOT-1";
        var invalid = await replay.SaveNewAsync(invalidRequest);
        Assert.True(invalid.Succeeded, invalid.ErrorMessage);
        var rejected = await replay.PostAsync([invalid.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("negative", rejected.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(15m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(45m,
                (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
            Assert.False(await unchanged.IvTrxHistories.AnyAsync(h =>
                h.BatchNo == invalid.BatchNo));
        }

        var validRequest = Request(balId, 4m);
        validRequest.TrxDate = FixedToday.AddDays(-1);
        validRequest.Lines![0].FrLotNo = "LOT-1";
        var valid = await replay.SaveNewAsync(validRequest);
        Assert.True(valid.Succeeded, valid.ErrorMessage);
        var posted = await replay.PostAsync([valid.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(11m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(33m,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
        }
        var rollback = await replay.RollbackAsync([valid.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(15m, (await restored.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(45m,
                (await restored.StockCostStates.SingleAsync()).InventoryValue);
        }
    }

    [Fact]
    public async Task Backdated_wac_mr_receives_before_later_mr_that_created_lot()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.LotControl = true;
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 0m, 0m);
        }

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        IvMiscReceiptSaveRequest Request(DateTime date, decimal quantity, decimal price) =>
            new()
            {
                TrxDate = date,
                Lines = [new IvMiscReceiptLineRequest
                {
                    ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                    ToLotNo = "LOT-1", Quantity = quantity, Uom = "EA",
                    IClassCode = "RAW", IStatus = "ACTIVE",
                    UnitPrice = price, PriceConfirmed = true,
                    ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
                }]
            };

        var later = await receipt.SaveNewAsync(Request(FixedToday, 100m, 2.5m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPost = await receipt.PostAsync([later.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);

        var earlier = await receipt.SaveNewAsync(
            Request(FixedToday.AddDays(-8), 40m, 2.51m));
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var earlierPost = await receipt.PostAsync([earlier.BatchNo]);
        Assert.True(earlierPost.Succeeded, earlierPost.ErrorMessage);

        await using (var posted = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(140m, (await posted.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(350.4m,
                (await posted.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(FixedToday,
                (await posted.IvLots.SingleAsync()).ReceiptDate);
        }

        var rollback = await receipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(250m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_wac_receipt_into_existing_lot_reprices_and_rolls_back()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }

        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 10m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 10m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(98.6m,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
            var history = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.ToLotId);
            var fact = await afterPost.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id);
            Assert.Equal(lotId, fact.LotId);
            Assert.Equal("LOT-1", fact.LotNo);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "WRONG-LOT";
            await tamper.SaveChangesAsync();
        }
        var rejected = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using (var restoreDetail = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await restoreDetail.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(98.6m,
                (await restoreDetail.StockCostStates.SingleAsync()).InventoryValue);
            var detail = await restoreDetail.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "LOT-1";
            await restoreDetail.SaveChangesAsync();
        }

        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(21m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_standard_receipt_into_existing_lot_posts_and_rolls_back()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 15m, 6.5m, StockCostMethods.Standard);
        }

        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 10m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 10m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(110.5m,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
            var history = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.ToLotId);
            var fact = await afterPost.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id);
            Assert.Equal(lotId, fact.LotId);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "WRONG-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await receipt.RollbackAsync([saved.BatchNo])).Succeeded);
        await using (var restoreDetail = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await restoreDetail.IvBalLocs.SingleAsync()).StdQty);
            var detail = await restoreDetail.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "LOT-1";
            await restoreDetail.SaveChangesAsync();
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(45.5m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Backdated_fifo_receipt_into_existing_lot_reprices_and_rolls_back()
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-20)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-15))
            ]);
        }

        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-17),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 10m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 2m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(54m,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
            var history = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.ToLotId);
            var fact = await afterPost.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id);
            Assert.Equal(lotId, fact.LotId);
            Assert.Equal("LOT-1", fact.LotNo);
            var issueHistory = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                afterPost, InventoryLedgerTestFixture.CompanyCode,
                InventoryLedgerTestFixture.BranchCode, [issueHistory]);
            Assert.Equal(21m,
                active[issueHistory.Id].Sum(f => f.CostAmount));
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "WRONG-LOT";
            await tamper.SaveChangesAsync();
        }
        Assert.False((await receipt.RollbackAsync([saved.BatchNo])).Succeeded);
        await using (var restoreDetail = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(17m, (await restoreDetail.IvBalLocs.SingleAsync()).StdQty);
            var detail = await restoreDetail.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ToLotNo = "LOT-1";
            await restoreDetail.SaveChangesAsync();
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(28m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage, 18, 21)]
    [InlineData(StockCostMethods.Standard, 39, 45.5)]
    [InlineData(StockCostMethods.Fifo, 24, 28)]
    public async Task Backdated_lot_scrap_posts_and_rolls_back_before_later_issue(
        string method, decimal postedValue, decimal restoredValue)
    {
        var (balId, lotId) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (method == StockCostMethods.MovingAverage)
            {
                await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
                await InventoryLedgerTestFixture.SeedCostStateAsync(
                    db, "A100", 15m, 3m);
            }
            else if (method == StockCostMethods.Standard)
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(
                    db, "A100", 6.5m);
                await InventoryLedgerTestFixture.SeedCostStateAsync(
                    db, "A100", 15m, 6.5m, StockCostMethods.Standard);
            }
            else
            {
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-20)),
                    new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-15))
                ]);
            }
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var scrap = InventoryLedgerTestFixture.CreateScrap(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await scrap.SaveNewAsync(new IvScrapSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvScrapLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = "LOT-1", Quantity = 1m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 3m, Reason = "DAMAGED"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await scrap.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(postedValue,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
            var history = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.FromLotId);
            var facts = await afterPost.StockValuationFacts.Where(f =>
                f.InventoryHistoryId == history.Id
                && f.OriginalValuationFactId == null).ToArrayAsync();
            Assert.All(facts, f =>
            {
                Assert.Equal(lotId, f.LotId);
                Assert.Equal("LOT-1", f.LotNo);
                Assert.Equal("SCRAP_OUT", f.MovementCode);
            });
        }
        var rollback = await scrap.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(restoredValue,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage)]
    [InlineData(StockCostMethods.Standard)]
    [InlineData(StockCostMethods.Fifo)]
    public async Task Backdated_lot_transfer_reports_unsupported_new_lot_without_mutation(
        string method)
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            if (method == StockCostMethods.MovingAverage)
            {
                await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
                await InventoryLedgerTestFixture.SeedCostStateAsync(
                    db, "A100", 15m, 3m);
            }
            else if (method == StockCostMethods.Standard)
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(
                    db, "A100", 6.5m);
                await InventoryLedgerTestFixture.SeedCostStateAsync(
                    db, "A100", 15m, 6.5m, StockCostMethods.Standard);
            }
            else
            {
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-20)),
                    new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-15))
                ]);
            }
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await transfer.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvStockTransferLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = "LOT-1", ToWarehouse = "MAIN",
                ToLocation = "BIN2", ToLotNo = "LOT-NEW",
                Quantity = 1m, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var rejected = await transfer.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("new destination lot", rejected.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.False(await unchanged.IvLots.AnyAsync(l => l.LotNo == "LOT-NEW"));
        Assert.False(await unchanged.IvTrxHistories.AnyAsync(h =>
            h.BatchNo == saved.BatchNo));
    }

    [Fact]
    public async Task Backdated_lot_receipt_rechecks_expiry_policy_after_draft_change()
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.ExpiryControl = IvExpiryControlModes.Required;
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 1m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 3m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.ExpiryDate = FixedToday.AddDays(-10);
            await tamper.SaveChangesAsync();
        }
        var rejected = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("expiry", rejected.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.False(await unchanged.IvTrxHistories.AnyAsync(h =>
            h.BatchNo == saved.BatchNo));
    }

    [Fact]
    public async Task Backdated_lot_receipt_rechecks_active_destination_masters()
    {
        var (balId, _) = await SeedLotBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var laterRequest = Request(balId, 8m);
        laterRequest.Lines![0].FrLotNo = "LOT-1";
        var later = await issue.SaveNewAsync(laterRequest);
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday.AddDays(-1),
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "LOT-1", Quantity = 1m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE",
                UnitPrice = 3m, PriceConfirmed = true,
                ExpiryDate = FixedToday.AddDays(30), Reason = "FOUND"
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        foreach (var master in new[] { "warehouse", "location", "status" })
        {
            await using (var inactive = await _factory.CreateDbContextAsync())
            {
                if (master == "warehouse")
                    (await inactive.IvWarehouses.SingleAsync()).IsActive = false;
                else if (master == "location")
                    (await inactive.IvLocations.SingleAsync()).IsActive = false;
                else
                    (await inactive.IvStatuses.SingleAsync()).IsActive = false;
                await inactive.SaveChangesAsync();
            }
            var rejected = await receipt.PostAsync([saved.BatchNo]);
            Assert.False(rejected.Succeeded);
            Assert.Contains(master, rejected.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
            await using (var restore = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(7m, (await restore.IvBalLocs.SingleAsync()).StdQty);
                Assert.False(await restore.IvTrxHistories.AnyAsync(h =>
                    h.BatchNo == saved.BatchNo));
                if (master == "warehouse")
                    (await restore.IvWarehouses.SingleAsync()).IsActive = true;
                else if (master == "location")
                    (await restore.IvLocations.SingleAsync()).IsActive = true;
                else
                    (await restore.IvStatuses.SingleAsync()).IsActive = true;
                await restore.SaveChangesAsync();
            }
        }
        Assert.True((await receipt.PostAsync([saved.BatchNo])).Succeeded);
        Assert.True((await receipt.RollbackAsync([saved.BatchNo])).Succeeded);
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

    private async Task<(int BalanceId, int LotId)> SeedLotBalanceAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
        master.LotControl = true;
        var lot = new IvLot
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100", LotNo = "LOT-1", IsActive = true,
            ReceiptDate = FixedToday.AddDays(-20)
        };
        db.IvLots.Add(lot);
        await db.SaveChangesAsync();
        var balance = new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN1",
            LotId = lot.Id, LotNo = lot.LotNo, IStatus = "ACTIVE",
            StdQty = qty, StdUom = "EA", UnitPrice = BalanceReferencePrice,
            TransDate = FixedToday
        };
        db.IvBalLocs.Add(balance);
        await db.SaveChangesAsync();
        return (balance.Id, lot.Id);
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
