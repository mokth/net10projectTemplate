using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Locked authority for one invoice variance affected by a receipt replay.</summary>
public sealed class StockReplayPurchaseVarianceWritePermit
{
    internal StockReplayPurchaseVarianceWritePermit(StockPostingContext context,
        StockReplayPurchaseVarianceGeneration generation, StockReplayWacStep step,
        string planHash, long watermark, DateTime latestEffectiveAt, int affectedHistories)
    {
        RootPostingId = context.Posting.Id;
        Generation = generation;
        Step = step;
        PlanHash = planHash;
        Watermark = watermark;
        LatestEffectiveAt = latestEffectiveAt;
        AffectedHistories = affectedHistories;
    }

    internal long RootPostingId { get; }
    internal StockReplayPurchaseVarianceGeneration Generation { get; }
    internal StockReplayWacStep Step { get; }
    internal string PlanHash { get; }
    internal long Watermark { get; }
    internal DateTime LatestEffectiveAt { get; }
    internal int AffectedHistories { get; }
}

/// <summary>Replaces a proven invoice variance without changing its commercial settlement.</summary>
internal static class StockReplayWacPurchaseVarianceWriter
{
    internal static async Task AppendAsync(StockPostingContext context, StockReplayWacWritePermit wacPermit,
        StockReplayPurchaseVarianceWritePermit purchasePermit, StockReplayWacEvent movement,
        StockReplayWacStep step, int reversalLine, int replacementLine, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var expected = purchasePermit.Generation;
        if (context.Db.Database.CurrentTransaction is null || context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.PostingRole is not ("PRIMARY" or "REVERSAL")
            || (context.Posting.PostingRole == "PRIMARY") != (context.Posting.ReversesPostingId is null)
            || context.Posting.EffectiveAt > expected.OriginalFact.EffectiveAt
            || context.Posting.PeriodKey != expected.OriginalFact.PeriodKey
            || purchasePermit.RootPostingId != context.Posting.Id || wacPermit.RootPostingId != context.Posting.Id
            || purchasePermit.PlanHash != wacPermit.PlanHash || purchasePermit.Step != step
            || movement.EventId != long.MaxValue - expected.OriginalFact.Id || step.EventId != movement.EventId
            || movement.OldSignedValue != expected.Active.InventoryAdjustmentAmount
            || movement.FixedSignedValue != movement.OldSignedValue || movement.OldSignedQuantity != 0m
            || movement.PurchaseVariance != new StockReplayPurchaseVariance(
                expected.Original.TotalAdjustmentAmount, expected.Original.BaseQty)
            || step.SignedQuantity != 0m || step.SignedValue == 0m || step.SignedValue == movement.OldSignedValue
            || step.QuantityBefore <= 0m || step.ValueBefore < 0m
            || reversalLine <= 0 || replacementLine <= 0 || reversalLine == replacementLine
            || purchasePermit.LatestEffectiveAt < expected.OriginalFact.EffectiveAt)
            throw Invalid("The purchase variance has no matching locked replay authority.");

        var db = context.Db;
        var ids = expected.ChainIds;
        var rows = await db.PurchaseCostAdjustments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        if (rows.Count != ids.Length || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a =>
                a.ReversesAdjustmentId.HasValue && ids.Contains(a.ReversesAdjustmentId.Value)
                && !ids.Contains(a.Id), ct))
            throw Invalid("The purchase variance generation changed after planning.");
        var chain = ids.Select(id => rows[id]).ToArray();
        var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, chain, ct);
        if (generation is null || generation.Fingerprint != expected.Fingerprint
            || generation.Active.Id != expected.Active.Id || generation.ActiveFact.Id != expected.ActiveFact.Id)
            throw Invalid("The purchase variance evidence changed after planning.");
        var allocation = StockPurchaseVarianceAllocator.Allocate(generation.Original.TotalAdjustmentAmount,
            generation.Original.BaseQty, Math.Min(step.QuantityBefore, generation.Original.BaseQty),
            step.ValueBefore, StockCostMethods.MovingAverage);
        if (allocation.Inventory != step.SignedValue || step.ValueAfter != StockLedgerPrecision.Money(step.ValueBefore + step.SignedValue)
            || step.QuantityBefore != step.QuantityAfter || step.ValueAfter < 0m)
            throw Invalid("The projected purchase variance does not reconcile.");
        var usedLines = await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.StockPostingId == context.Posting.Id
            && (f.PostingLineNo == reversalLine || f.PostingLineNo == replacementLine), ct);
        if (usedLines || db.ChangeTracker.Entries<StockValuationFact>().Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalLine || e.Entity.PostingLineNo == replacementLine)))
            throw Invalid("A purchase variance replay line is already used.");

        var oldFact = generation.ActiveFact;
        var rootFact = generation.OriginalFact;
        var reverseFact = CopyFact(oldFact, context, reversalLine, -oldFact.Direction, oldFact.CostAmount,
            StockValuationSources.OriginalReversal, StockValuationStatuses.Reversed, oldFact.ValuationVersion,
            rootFact.Id, oldFact.Id);
        var replacementFact = CopyFact(rootFact, context, replacementLine, step.SignedValue > 0m ? 1 : -1,
            Math.Abs(step.SignedValue), StockValuationSources.PurchasePriceVariance, StockValuationStatuses.Valued,
            checked(oldFact.ValuationVersion + 1), rootFact.Id, null);
        db.StockValuationFacts.AddRange(reverseFact, replacementFact);
        var reversedAdjustment = CopyAdjustment(generation.Active, context,
            -generation.Active.TotalAdjustmentAmount, -generation.Active.InventoryAdjustmentAmount,
            -generation.Active.ConsumedVarianceAmount, reverseFact);
        reversedAdjustment.ReversesAdjustmentId = generation.Active.Id;
        var replacementAdjustment = CopyAdjustment(generation.Original, context,
            generation.Original.TotalAdjustmentAmount, allocation.Inventory, allocation.Consumed, replacementFact);
        replacementAdjustment.ReversesAdjustment = reversedAdjustment;
        db.PurchaseCostAdjustments.AddRange(reversedAdjustment, replacementAdjustment);
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt, LatestEffectiveAt = purchasePermit.LatestEffectiveAt,
            PlanWatermark = purchasePermit.Watermark, PlanHash = purchasePermit.PlanHash,
            AffectedHistoryCount = purchasePermit.AffectedHistories, AffectedItemCount = 1,
            AffectedProductionMovementCount = 0, CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.AddRange(
            Effect(context, run, generation.Original.StockPostingId, StockReplayEffectTypes.PurchaseCostAdjustment,
                reversedAdjustment.Id, generation.Active.Id),
            Effect(context, run, generation.Original.StockPostingId, StockReplayEffectTypes.PurchaseCostAdjustment,
                replacementAdjustment.Id, generation.Active.Id),
            Effect(context, run, generation.Original.StockPostingId, StockReplayEffectTypes.StockValuationFact,
                reverseFact.Id, oldFact.Id),
            Effect(context, run, generation.Original.StockPostingId, StockReplayEffectTypes.StockValuationFact,
                replacementFact.Id, oldFact.Id));
        await db.SaveChangesAsync(ct);
    }

    private static StockReplayEffect Effect(StockPostingContext context, StockReplayRun run,
        long owner, string type, long id, long prior) => new()
    {
        CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, ReplayRunId = run.Id,
        RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = owner,
        EffectKind = StockReplayPurchaseVarianceGenerationReader.ReplacementEffect,
        AffectedEntityType = type, AffectedEntityId = id, OriginalEntityId = prior,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static PurchaseCostAdjustment CopyAdjustment(PurchaseCostAdjustment source,
        StockPostingContext context, decimal total, decimal inventory, decimal consumed, StockValuationFact fact) => new()
    {
        CompanyCode = source.CompanyCode, BranchCode = source.BranchCode, AdjustmentType = source.AdjustmentType,
        SourceDocumentType = source.SourceDocumentType, SourceDocumentNo = source.SourceDocumentNo,
        SourceDocumentLine = source.SourceDocumentLine, SourceCostingRevision = source.SourceCostingRevision,
        PoNo = source.PoNo, PoRelNo = source.PoRelNo, PoLineNo = source.PoLineNo,
        ItemCode = source.ItemCode, CostMethod = source.CostMethod, BaseQty = source.BaseQty,
        ActualBaseAmount = source.ActualBaseAmount, ReferenceBaseAmount = source.ReferenceBaseAmount,
        CommercialReferenceAmount = source.CommercialReferenceAmount, TotalAdjustmentAmount = total,
        InventoryAdjustmentAmount = inventory, ConsumedVarianceAmount = consumed,
        StockPostingId = context.Posting.Id, InventoryAdjustmentFact = fact,
        CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
    };

    private static StockValuationFact CopyFact(StockValuationFact source, StockPostingContext context,
        int line, int direction, decimal amount, string valuationSource, string status,
        int version, long originalId, long? reversesId) => new()
    {
        CompanyCode = source.CompanyCode, BranchCode = source.BranchCode, LedgerEpochId = source.LedgerEpochId,
        StockPostingId = context.Posting.Id, PostingLineNo = line, SplitOrdinal = 0,
        SourceLineId = source.SourceLineId, SourceDocumentType = source.SourceDocumentType,
        SourceDocumentId = source.SourceDocumentId, SourceDocumentNo = source.SourceDocumentNo,
        SourceDocumentLine = source.SourceDocumentLine, EffectiveAt = source.EffectiveAt,
        BusinessDate = source.BusinessDate, PeriodKey = source.PeriodKey, ItemCode = source.ItemCode,
        WarehouseCode = source.WarehouseCode, LocationCode = source.LocationCode,
        LotId = source.LotId, LotNo = source.LotNo, ItemStatus = source.ItemStatus,
        BaseUom = source.BaseUom, MovementCode = source.MovementCode, Direction = direction,
        BaseQty = 0m, CostMethod = source.CostMethod, UnitCost = 0m, CostAmount = amount,
        TransactionCurrency = source.TransactionCurrency, TransactionCostAmount = source.TransactionCostAmount,
        ExchangeRate = source.ExchangeRate, BaseCurrency = source.BaseCurrency, BaseCostAmount = amount,
        ValuationSource = valuationSource, ValuationStatus = status, ValuationVersion = version,
        OriginalValuationFactId = originalId, ReversesValuationFactId = reversesId,
        CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
    };

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
