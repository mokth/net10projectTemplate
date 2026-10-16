using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        }
        finally
        {
            if (begin.Context is not null)
                await coordinator.AbortInTransactionAsync(begin.Context);
            await transaction.RollbackAsync();
        }
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
