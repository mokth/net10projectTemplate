using ErpWeb.Core.Production;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionIssueEffectiveCostReaderTests
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 1, 8, 0, 0);

    [Fact]
    public void Reads_the_complete_corrected_FIFO_issue_generation()
    {
        var (movement, material, history) = Issue();
        var first = Fact(1, 2m, 6m);
        var second = Fact(2, 3m, 12m);
        var reverseFirst = Reverse(first, 3);
        var reverseSecond = Reverse(second, 4);
        var replacementFirst = Fact(5, 2m, 10m);
        var replacementSecond = Fact(6, 3m, 18m);

        var active = StockReplayValuationGenerationResolver.ResolveActive(
            [history], [first, second, reverseFirst, reverseSecond,
                replacementFirst, replacementSecond]);
        var cost = ProductionIssueEffectiveCostReader.FromActiveGeneration(
            movement, material, history, active[history.Id]);

        Assert.Equal(100L, cost.MovementId);
        Assert.Equal(200L, cost.MaterialMovementId);
        Assert.Equal(7, cost.InventoryHistoryId);
        Assert.Equal(5m, cost.BaseQty);
        Assert.Equal(28m, cost.TotalCost);
        Assert.Equal(5.6m, cost.UnitCost);
        Assert.Equal([5L, 6L], cost.ActiveValuationFactIds);
    }

    [Fact]
    public void Rejects_a_fact_linked_to_a_different_material_movement()
    {
        var (movement, material, history) = Issue();
        var fact = Fact(1, 5m, 20m);
        fact.ProductionMovementId = 201;

        var error = Assert.Throws<StockLedgerException>(() =>
            ProductionIssueEffectiveCostReader.FromActiveGeneration(
                movement, material, history, [fact]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public void Rejects_an_incomplete_FIFO_replacement()
    {
        var (movement, material, history) = Issue();
        var original = Fact(1, 5m, 20m);
        var reverse = Reverse(original, 2);
        var partial = Fact(3, 2m, 10m);

        var error = Assert.Throws<StockLedgerException>(() =>
            StockReplayValuationGenerationResolver.ResolveActive(
                [history], [original, reverse, partial]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public async Task Reads_sealed_issue_and_rejects_reversed_owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = Posting(10, 1);
        db.StockPostings.Add(owner);
        var (movement, material, history) = Issue();
        movement.ProductionBalLotId = 1;
        movement.Qty = 5m;
        movement.Uom = "EA";
        movement.BaseUom = "EA";
        movement.TotalCost = 20m;
        movement.UnitCost = 4m;
        movement.DocumentType = "TEST";
        movement.DocumentNo = "1";
        movement.CreatedDate = EffectiveAt;
        movement.CreatedBy = "test";
        material.Qty = 5m;
        material.Uom = "EA";
        material.BaseUom = "EA";
        material.CreatedDate = EffectiveAt;
        material.CreatedBy = "test";
        history.TrxType = IvTrxTypes.IssueToProduction;
        history.BatchStatus = "POSTED";
        var fact = Fact(1, 5m, 20m);
        fact.EffectiveAt = EffectiveAt;
        fact.BusinessDate = EffectiveAt.Date;
        fact.CreatedAtUtc = EffectiveAt;
        db.ProductionBalLotMovements.Add(movement);
        db.ProductionMaterialMovements.Add(material);
        db.IvTrxHistories.Add(history);
        db.StockValuationFacts.Add(fact);
        await db.SaveChangesAsync();

        var costs = await ProductionIssueEffectiveCostReader.ReadAsync(db, "DEMO", "HQ", [100]);
        Assert.Equal(20m, costs[100].TotalCost);

        var correction = Posting(11, 2);
        correction.PostingRole = "REVERSAL";
        var factReversal = Reverse(fact, 2);
        var replacement = Fact(3, 5m, 25m);
        replacement.EffectiveAt = EffectiveAt;
        replacement.BusinessDate = EffectiveAt.Date;
        replacement.CreatedAtUtc = EffectiveAt;
        db.StockPostings.Add(correction);
        db.StockValuationFacts.AddRange(factReversal, replacement);
        await db.SaveChangesAsync();
        var corrected = await ProductionIssueEffectiveCostReader.ReadAsync(db, "DEMO", "HQ", [100]);
        Assert.Equal(25m, corrected[100].TotalCost);
        Assert.Equal([3L], corrected[100].ActiveValuationFactIds);

        var reversal = Posting(12, 3);
        reversal.ReversesPostingId = owner.Id;
        reversal.PostingRole = "REVERSAL";
        reversal.DocumentRevision = 1;
        db.StockPostings.Add(reversal);
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionIssueEffectiveCostReader.ReadAsync(db, "DEMO", "HQ", [100]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public async Task Corrects_an_unconsumed_material_pool_within_the_inventory_replay_root()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = Posting(10, 1);
        var root = Posting(11, 2);
        root.SealedAtUtc = null;
        root.SourceDocumentId = "2";
        root.SourceDocumentNo = "2";
        root.EffectiveAt = EffectiveAt.AddDays(-1);
        root.BusinessDate = root.EffectiveAt.Date;
        var (movement, material, history) = Issue();
        movement.ProductionBalLotId = 1;
        movement.Qty = 5m;
        movement.Uom = "EA";
        movement.TotalCost = 20m;
        movement.UnitCost = 4m;
        movement.DocumentType = "TEST";
        movement.DocumentNo = "1";
        movement.CreatedDate = EffectiveAt;
        movement.CreatedBy = "test";
        material.ProductionBalLotId = 1;
        material.Qty = 5m;
        material.Uom = "EA";
        material.BaseUom = "EA";
        material.TotalCost = 20m;
        material.UnitCost = 4m;
        material.CreatedDate = EffectiveAt;
        material.CreatedBy = "test";
        history.BatchStatus = "POSTED";
        var originalFact = Fact(1, 5m, 20m);
        db.StockPostings.AddRange(owner, root);
        var lot = new ProductionBalLot
        {
            Uid = 1, CompanyCode = "DEMO", BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "A100", Qty = 5m, Uom = "EA",
            BaseQty = 5m, BaseUom = "EA", TotalCost = 20m,
            AverageUnitCost = 4m
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
        {
            ProductionBalLotId = 1, Generation = 1,
            Status = ProductionPoolValuationService.Verified,
            TrackedBaseQty = 5m, TrackedValue = 20m
        });
        db.ProductionValuationEvidenceRows.Add(new ProductionValuationEvidence
        {
            MovementId = movement.Uid, ProductionBalLotId = 1,
            Generation = 1, Status = ProductionPoolValuationService.Verified,
            Basis = "STOCK_VALUATION:FIFO", Price = 4m,
            InventoryHistoryId = history.Id
        });
        db.ProductionBalLotMovements.Add(movement);
        db.ProductionMaterialMovements.Add(material);
        db.IvTrxHistories.Add(history);
        db.StockValuationFacts.Add(originalFact);
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            RootStockPostingId = root.Id, RootRequestId = root.RequestId,
            RootDocumentType = root.SourceDocumentType,
            RootDocumentId = root.SourceDocumentId,
            EarliestEffectiveAt = root.EffectiveAt,
            LatestEffectiveAt = owner.EffectiveAt,
            PlanHash = new string('C', 64),
            CreatedAtUtc = EffectiveAt, CreatedBy = "test"
        });
        await db.SaveChangesAsync();
        lot.OriginalIssueMovementId = material.Uid;
        await db.SaveChangesAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        var context = new StockPostingContext(db, new StockLedgerEpoch
        {
            Id = 1, CompanyCode = "DEMO", BranchCode = "HQ"
        }, root, "test");
        var expected = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        db.StockValuationFacts.AddRange(
            Reverse(originalFact, 2), Fact(3, 5m, 25m));
        await db.SaveChangesAsync();

        var unresolvedAllocation = new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockPostingId = owner.Id,
            ReceiptMovementId = movement.Uid,
            OutboundMovementId = 300, BaseQty = 1m,
            CreatedAtUtc = EffectiveAt
        };
        db.ProductionMovementAllocations.Add(unresolvedAllocation);
        await db.SaveChangesAsync();
        var dependent = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, expected));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, dependent.Error.Code);
        db.ProductionMovementAllocations.Remove(unresolvedAllocation);
        await db.SaveChangesAsync();

        var result = await ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
            context, expected);
        Assert.NotNull(result.Replacement);
        Assert.Null(result.Reversal);
        Assert.Equal(25m, (await db.ProductionBalLots.SingleAsync()).TotalCost);
        Assert.Equal(5m, (await db.ProductionBalLots.SingleAsync()).AverageUnitCost);
        Assert.Equal(25m, (await db.ProductionPoolValuationRows.SingleAsync()).TrackedValue);
        Assert.Equal(20m, (await db.ProductionBalLotMovements.SingleAsync()).TotalCost);
        Assert.Equal(20m, (await db.ProductionMaterialMovements.SingleAsync()).TotalCost);
        var effect = await db.StockReplayEffects.SingleAsync();
        Assert.Equal(owner.Id, effect.BusinessOwnerPostingId);
        Assert.Equal(result.Replacement!.Id, effect.AffectedEntityId);
        Assert.Equal(movement.Uid, effect.OriginalEntityId);
        var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        Assert.Equal(25m, current.TotalCost);
        var repeat = await ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
            context, current);
        Assert.Null(repeat.Replacement);
        Assert.Null(repeat.Reversal);
        var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionLeafIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, expected));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.StockReplayEffects.ToArrayAsync());
        Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
        Assert.Equal(20m, (await db.ProductionBalLots.SingleAsync()).TotalCost);
    }

    [Theory]
    [InlineData(2, 8, 3, 12, 10, 15)]
    [InlineData(5, 20, 0, 0, 25, 0)]
    public async Task Corrects_one_consumed_issue_with_terminal_pool_average_adjustment(
        int consumedQty, int consumedValue, int remainingQty, int remainingValue,
        int correctedConsumedValue, int correctedRemainingValue)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.ProductionStockAdjustmentWrite = true;
        var owner = Posting(10, 1);
        var adjustmentOwner = Posting(11, 2);
        adjustmentOwner.SourceDocumentId = "2";
        adjustmentOwner.SourceDocumentNo = "2";
        adjustmentOwner.SourceDocumentType = ProductionDocumentTypes.ProductionStockAdjustment;
        var root = Posting(13, 3);
        root.SealedAtUtc = null;
        root.SourceDocumentId = "3";
        root.SourceDocumentNo = "3";
        root.EffectiveAt = EffectiveAt.AddDays(-1);
        root.BusinessDate = root.EffectiveAt.Date;
        db.StockPostings.AddRange(owner, adjustmentOwner, root);
        var (issue, material, history) = Issue();
        issue.ProductionBalLotId = 1;
        issue.Qty = 5m;
        issue.Uom = "EA";
        issue.TotalCost = 20m;
        issue.UnitCost = 4m;
        material.ProductionBalLotId = 1;
        material.Qty = 5m;
        material.Uom = "EA";
        material.BaseUom = "EA";
        material.TotalCost = 20m;
        material.UnitCost = 4m;
        history.BatchStatus = "POSTED";
        var adjustment = new ProductionBalLotMovement
        {
            Uid = 101, ProductionBalLotId = 1,
            MovementType = ProductionBalLotMovementTypes.AdjustOut,
            Qty = consumedQty, Uom = "EA", BaseQty = consumedQty, BaseUom = "EA",
            TotalCost = consumedValue, UnitCost = 4m,
            CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerVersion = 2, LedgerEpochId = 1,
            StockPostingId = adjustmentOwner.Id,
            ItemCode = "A100", MovementDate = EffectiveAt,
            DocumentType = ProductionDocumentTypes.ProductionStockAdjustment,
            ValuationStatus = ProductionPoolValuationService.Verified
        };
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            Uid = 1, CompanyCode = "DEMO", BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "A100", Qty = remainingQty, Uom = "EA",
            BaseQty = remainingQty, BaseUom = "EA",
            TotalCost = remainingValue,
            AverageUnitCost = remainingQty > 0 ? 4m : 0m
        });
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
        {
            ProductionBalLotId = 1, Generation = 1,
            Status = ProductionPoolValuationService.Verified,
            TrackedBaseQty = remainingQty, TrackedValue = remainingValue
        });
        db.ProductionBalLotMovements.AddRange(issue, adjustment);
        db.ProductionMaterialMovements.Add(material);
        db.IvTrxHistories.Add(history);
        var originalFact = Fact(1, 5m, 20m);
        db.StockValuationFacts.Add(originalFact);
        db.ProductionValuationEvidenceRows.AddRange(
            new ProductionValuationEvidence
            {
                MovementId = issue.Uid, ProductionBalLotId = 1,
                Generation = 1, Status = ProductionPoolValuationService.Verified,
                Basis = "STOCK_VALUATION:FIFO", InventoryHistoryId = history.Id
            },
            new ProductionValuationEvidence
            {
                MovementId = adjustment.Uid, ProductionBalLotId = 1,
                Generation = 1, Status = ProductionPoolValuationService.Verified,
                Basis = "POOLED_AVERAGE"
            });
        db.ProductionMovementAllocations.Add(new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockPostingId = adjustmentOwner.Id,
            ReceiptMovementId = issue.Uid,
            OutboundMovementId = adjustment.Uid,
            BaseQty = consumedQty, CreatedAtUtc = EffectiveAt
        });
        db.ProductionPoolDependencyRows.Add(new ProductionPoolDependency
        {
            ContributorMovementId = issue.Uid,
            ConsumerMovementId = adjustment.Uid,
            StockPostingId = adjustmentOwner.Id
        });
        db.ProductionStockAdjustmentFacts.Add(new ProductionStockAdjustmentFact
        {
            BatchId = 2, AdjustmentLineId = 1,
            StockPostingId = adjustmentOwner.Id,
            ProductionMovementId = adjustment.Uid,
            BaseQty = consumedQty, TotalValue = consumedValue, UnitCost = 4m,
            CostBasisCode = "POOLED_AVERAGE"
        });
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            RootStockPostingId = root.Id, RootRequestId = root.RequestId,
            RootDocumentType = root.SourceDocumentType,
            RootDocumentId = root.SourceDocumentId,
            EarliestEffectiveAt = root.EffectiveAt,
            LatestEffectiveAt = EffectiveAt,
            PlanHash = new string('C', 64),
            CreatedAtUtc = EffectiveAt, CreatedBy = "test"
        });
        await db.SaveChangesAsync();
        var lot = await db.ProductionBalLots.SingleAsync();
        lot.OriginalIssueMovementId = material.Uid;
        await db.SaveChangesAsync();
        db.ProductionStockAdjustmentWrite = false;

        await using var transaction = await db.Database.BeginTransactionAsync();
        var context = new StockPostingContext(db, new StockLedgerEpoch
        {
            Id = 1, CompanyCode = "DEMO", BranchCode = "HQ"
        }, root, "test");
        var expected = await ProductionEffectiveCostResolver.ResolveAsync(
            db, "DEMO", "HQ", [issue.Uid, adjustment.Uid]);
        var inverse = Reverse(originalFact, 2);
        inverse.StockPostingId = root.Id;
        var replacement = Fact(3, 5m, 25m);
        replacement.StockPostingId = root.Id;
        db.StockValuationFacts.AddRange(inverse, replacement);
        await db.SaveChangesAsync();

        var unrelatedAllocation = new ProductionMovementAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockPostingId = adjustmentOwner.Id,
            ReceiptMovementId = issue.Uid,
            OutboundMovementId = 999,
            BaseQty = 1m, CreatedAtUtc = EffectiveAt
        };
        db.ProductionMovementAllocations.Add(unrelatedAllocation);
        await db.SaveChangesAsync();
        var dependent = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionTerminalAdjustmentIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, expected[issue.Uid], expected[adjustment.Uid]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, dependent.Error.Code);
        Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
        db.ProductionMovementAllocations.Remove(unrelatedAllocation);
        await db.SaveChangesAsync();

        var result = await ProductionTerminalAdjustmentIssueCostCorrectionWriter
            .AppendWithinRootAsync(context, expected[issue.Uid], expected[adjustment.Uid]);
        Assert.NotNull(result.Issue.Replacement);
        Assert.NotNull(result.Adjustment.Replacement);
        Assert.Equal(correctedRemainingValue, result.RemainingValue);
        Assert.Equal(correctedRemainingValue, (await db.ProductionBalLots.SingleAsync()).TotalCost);
        Assert.Equal(remainingQty > 0 ? 5m : 0m,
            (await db.ProductionBalLots.SingleAsync()).AverageUnitCost);
        Assert.Equal(correctedRemainingValue,
            (await db.ProductionPoolValuationRows.SingleAsync()).TrackedValue);
        Assert.Equal(2, await db.StockReplayEffects.CountAsync());
        var effective = await ProductionEffectiveCostResolver.ResolveAsync(
            db, "DEMO", "HQ", [issue.Uid, adjustment.Uid]);
        Assert.Equal(25m, effective[issue.Uid].TotalCost);
        Assert.Equal(correctedConsumedValue, effective[adjustment.Uid].TotalCost);
        var repeat = await ProductionTerminalAdjustmentIssueCostCorrectionWriter
            .AppendWithinRootAsync(context, effective[issue.Uid], effective[adjustment.Uid]);
        Assert.Null(repeat.Issue.Replacement);
        Assert.Null(repeat.Adjustment.Replacement);
        var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionTerminalAdjustmentIssueCostCorrectionWriter.AppendWithinRootAsync(
                context, expected[issue.Uid], expected[adjustment.Uid]));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
        Assert.Equal(20m, (await db.ProductionBalLotMovements.SingleAsync(m =>
            m.Uid == issue.Uid)).TotalCost);
        Assert.Equal(consumedValue, (await db.ProductionBalLotMovements.SingleAsync(m =>
            m.Uid == adjustment.Uid)).TotalCost);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
        Assert.Equal(remainingValue, (await db.ProductionBalLots.SingleAsync()).TotalCost);
    }

    private static (ProductionBalLotMovement Movement, ProductionMaterialMovement Material,
        IvTrxHistory History) Issue()
    {
        var movement = new ProductionBalLotMovement
        {
            Uid = 100, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerVersion = 2, LedgerEpochId = 1, StockPostingId = 10,
            MovementType = ProductionBalLotMovementTypes.Issue,
            ValuationStatus = ProductionPoolValuationService.Verified,
            ItemCode = "A100", BaseQty = 5m, BaseUom = "EA",
            InventoryHistoryId = 7, MovementDate = EffectiveAt
        };
        var material = new ProductionMaterialMovement
        {
            Uid = 200, ProductionBalLotMovementId = movement.Uid,
            CompanyCode = "DEMO", BranchCode = "HQ",
            MovementType = ProductionMaterialMovementTypes.Issue,
            ItemCode = "A100", BaseQty = 5m,
            InventoryHistoryId = 7, StockPostingId = 10,
            MovementDate = EffectiveAt
        };
        var history = new IvTrxHistory
        {
            Id = 7, CompanyCode = "DEMO", BranchCode = "HQ",
            ICode = "A100", LedgerVersion = 2, LedgerEpochId = 1,
            StockPostingId = 10, EntryRole = "ORIGINAL",
            FrStdQty = 5m, ToStdQty = 0m, TrxDtTime = EffectiveAt,
            TrxType = IvTrxTypes.IssueToProduction
        };
        return (movement, material, history);
    }

    private static StockValuationFact Fact(long id, decimal qty, decimal value) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ",
        LedgerEpochId = 1, StockPostingId = id <= 2 ? 10 : 11,
        PostingLineNo = 1, SplitOrdinal = (int)id,
        InventoryHistoryId = 7, ProductionMovementId = 200,
        ItemCode = "A100", BaseUom = "EA", EffectiveAt = EffectiveAt,
        Direction = -1,
        MovementCode = "PRODUCTION_MATERIAL_OUT",
        BaseQty = qty, CostAmount = value,
        CostMethod = StockCostMethods.Fifo,
        ValuationStatus = StockValuationStatuses.Valued
    };

    private static StockValuationFact Reverse(StockValuationFact prior, long id) => new()
    {
        Id = id, CompanyCode = prior.CompanyCode, BranchCode = prior.BranchCode,
        LedgerEpochId = prior.LedgerEpochId, StockPostingId = 11,
        PostingLineNo = 2, SplitOrdinal = (int)id,
        InventoryHistoryId = 8, ProductionMovementId = prior.ProductionMovementId,
        ItemCode = prior.ItemCode, Direction = 1,
        MovementCode = prior.MovementCode,
        BaseQty = prior.BaseQty, CostAmount = prior.CostAmount,
        CostMethod = prior.CostMethod,
        ValuationStatus = StockValuationStatuses.Reversed,
        ReversesValuationFactId = prior.Id
    };

    private static StockPosting Posting(long id, long sequence) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = 1,
        PostingSequence = sequence, RequestId = Guid.NewGuid(),
        CommandType = "PRODUCTION_ISSUE_COST_TEST",
        RequestFingerprint = new string('A', 64),
        SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
        SourceDocumentId = "1", SourceDocumentNo = "1",
        SourceSnapshotHash = new string('B', 64),
        EffectiveAt = EffectiveAt, BusinessDate = EffectiveAt.Date,
        PeriodKey = "202610", PostedAtUtc = EffectiveAt,
        PostedBy = "test", SealedAtUtc = EffectiveAt
    };
}
