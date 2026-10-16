using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacValueRepricingResult(
    StockValuationFact Reversal,
    StockValuationFact Replacement);

/// <summary>
/// Replaces one original value-only WAC adjustment at its original business
/// key while the unsealed replay root owns the new audit facts. The caller
/// proves the corrected settlement authority and applies the final cost state.
/// </summary>
public static class StockReplayWacValueRepricingWriter
{
    public static async Task<StockReplayWacValueRepricingResult?> AppendAsync(
        StockPostingContext context,
        long originalFactId,
        decimal oldSignedValue,
        decimal correctedSignedValue,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || originalFactId <= 0 || reversalPostingLineNo <= 0
            || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo
            || StockLedgerPrecision.Money(oldSignedValue) != oldSignedValue
            || StockLedgerPrecision.Money(correctedSignedValue)
                != correctedSignedValue)
            throw Invalid("Value-only WAC repricing has invalid root or amount evidence.");

        var db = context.Db;
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.Id == originalFactId
                || f.OriginalValuationFactId == originalFactId)
            .ToArrayAsync(cancellationToken);
        var original = facts.SingleOrDefault(f => f.Id == originalFactId);
        if (original is null || original.OriginalValuationFactId is not null
            || original.ReversesValuationFactId is not null
            || original.ValuationStatus != StockValuationStatuses.Valued
            || original.InventoryHistoryId is not null
            || original.BaseQty != 0m
            || original.CostMethod != StockCostMethods.MovingAverage
            || original.CompanyCode != context.CompanyCode
            || original.BranchCode != context.BranchCode
            || original.LedgerEpochId != context.Epoch.Id)
            throw Invalid($"Value fact {originalFactId} is not an original V2 WAC adjustment.");
        var byId = facts.ToDictionary(f => f.Id);
        if (facts.Length != byId.Count
            || facts.Any(f => f.InventoryHistoryId is not null
                || f.BaseQty != 0m || f.CostMethod != StockCostMethods.MovingAverage
                || f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id
                || f.EffectiveAt != original.EffectiveAt
                || f.BusinessDate != original.BusinessDate
                || f.PeriodKey != original.PeriodKey
                || f.ItemCode != original.ItemCode
                || f.SourceDocumentType != original.SourceDocumentType
                || f.SourceDocumentId != original.SourceDocumentId
                || f.SourceDocumentNo != original.SourceDocumentNo
                || f.SourceDocumentLine != original.SourceDocumentLine
                || f.SourceLineId != original.SourceLineId
                || f.MovementCode != original.MovementCode
                || f.BaseCurrency != original.BaseCurrency
                || f.Direction is not (-1 or 1)
                || f.CostAmount < 0m
                || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount
                || f.Id != originalFactId
                    && f.OriginalValuationFactId != originalFactId))
            throw Invalid($"Value fact {originalFactId} has unsupported restatement lineage.");
        var reversals = facts.Where(f => f.ReversesValuationFactId.HasValue)
            .ToArray();
        var reversedIds = new HashSet<long>();
        if (reversals.Any(r => r.ReversesValuationFactId is not long priorId
                || !byId.TryGetValue(priorId, out var prior)
                || prior.ReversesValuationFactId is not null
                || !reversedIds.Add(priorId)
                || r.ValuationStatus != StockValuationStatuses.Reversed
                || r.Direction != -prior.Direction
                || r.CostAmount != prior.CostAmount))
            throw Invalid($"Value fact {originalFactId} has an inexact reversal chain.");
        var active = facts.Where(f => f.ReversesValuationFactId is null
            && !reversedIds.Contains(f.Id)).ToArray();
        if (active.Length != 1
            || active[0].ValuationStatus != StockValuationStatuses.Valued
            || active[0].SignedValue != oldSignedValue
            || active[0].TransactionCurrency is not null
            || active[0].TransactionCostAmount is not null
            || active[0].ExchangeRate is not null
            || active[0].BaseCostAmount != active[0].CostAmount
            || original.TransactionCurrency is not null
            || original.TransactionCostAmount is not null
            || original.ExchangeRate is not null)
            throw Invalid($"Value fact {originalFactId} has no unique active WAC amount.");
        var priorActive = active[0];

        var postingIds = facts.Select(f => f.StockPostingId).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (postingIds.Any(id => !owners.TryGetValue(id, out var owner)
                || owner.SealedAtUtc is null
                || owner.CompanyCode != context.CompanyCode
                || owner.BranchCode != context.BranchCode
                || owner.LedgerEpochId != context.Epoch.Id))
            throw Invalid($"Value fact {originalFactId} has an unsealed owner.");
        if (priorActive.Id == original.Id)
            StockReplayWacValueEventFactory.Create(original,
                owners[original.StockPostingId]);
        else
            StockReplayWacValueEventFactory.CreateCorrected(
                priorActive, original, owners[original.StockPostingId],
                owners[priorActive.StockPostingId]);
        if (correctedSignedValue == oldSignedValue)
            return null;

        var periodRefusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            context.CompanyCode, context.BranchCode,
            original.EffectiveAt, cancellationToken);
        if (periodRefusal is not null)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod,
                periodRefusal));
        var occupied = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == context.Posting.Id
                && (f.PostingLineNo == reversalPostingLineNo
                    || f.PostingLineNo == replacementPostingLineNo),
                cancellationToken);
        if (occupied || db.ChangeTracker.Entries<StockValuationFact>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalPostingLineNo
                    || e.Entity.PostingLineNo == replacementPostingLineNo)))
            throw Invalid("A value-only WAC replay root line is already used.");

        var now = DateTime.UtcNow;
        StockValuationFact Copy(int postingLineNo) => new()
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = 0,
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
            BaseQty = 0m,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = original.UnitCost,
            BaseCurrency = original.BaseCurrency,
            FromBalLocId = original.FromBalLocId,
            ToBalLocId = original.ToBalLocId,
            ProductionMovementId = original.ProductionMovementId,
            WorkOrderId = original.WorkOrderId,
            WorkOrderOperationId = original.WorkOrderOperationId,
            ProductionPostingLinkId = original.ProductionPostingLinkId,
            ValuationVersion = checked(priorActive.ValuationVersion + 1),
            OriginalValuationFactId = original.Id,
            CreatedAtUtc = now,
            CreatedBy = context.UserId
        };
        var reversal = Copy(reversalPostingLineNo);
        reversal.Direction = -priorActive.Direction;
        reversal.CostAmount = priorActive.CostAmount;
        reversal.BaseCostAmount = priorActive.BaseCostAmount;
        reversal.ValuationSource = StockValuationSources.OriginalReversal;
        reversal.ValuationStatus = StockValuationStatuses.Reversed;
        reversal.ValuationVersion = priorActive.ValuationVersion;
        reversal.ReversesValuationFactId = priorActive.Id;
        var replacement = Copy(replacementPostingLineNo);
        replacement.Direction = correctedSignedValue == 0m
            ? priorActive.Direction : Math.Sign(correctedSignedValue);
        replacement.CostAmount = Math.Abs(correctedSignedValue);
        replacement.BaseCostAmount = replacement.CostAmount;
        replacement.ValuationSource = priorActive.ValuationSource;
        replacement.ValuationStatus = StockValuationStatuses.Valued;
        db.StockValuationFacts.AddRange(reversal, replacement);
        return new(reversal, replacement);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
