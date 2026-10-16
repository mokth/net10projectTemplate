using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacReceiptRestatementResult(
    StockValuationFact? Reversal,
    StockValuationFact? ActiveDelta);

/// <summary>
/// Appends a value-only delta after an unchanged WAC receipt. A later change
/// reverses the active delta and appends its complete replacement. Receipt
/// price authority and downstream recosting belong to the replay caller.
/// </summary>
public static class StockReplayWacReceiptRestatementWriter
{
    public static async Task<StockReplayWacReceiptRestatementResult?> AppendAsync(
        StockPostingContext context,
        int receiptHistoryId,
        long receiptFactId,
        decimal correctedReceiptAmount,
        string valuationSource,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || receiptHistoryId <= 0 || receiptFactId <= 0
            || correctedReceiptAmount < 0m
            || StockLedgerPrecision.Money(correctedReceiptAmount)
                != correctedReceiptAmount
            || string.IsNullOrWhiteSpace(valuationSource)
            || reversalPostingLineNo <= 0 || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo)
            throw Invalid("Receipt WAC restatement has invalid source or root identity.");
        var db = context.Db;
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == receiptHistoryId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (history is null || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.FrStdQty is > 0m
            || history.ToStdQty is not > 0m
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.ReversesHistoryId == receiptHistoryId, cancellationToken))
            throw Invalid($"History {receiptHistoryId} is not an unchanged active V2 receipt.");
        var activeReceiptFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[receiptHistoryId];
        var receipt = activeReceiptFacts.SingleOrDefault(f => f.Id == receiptFactId);
        if (receipt is null || receipt.Direction != 1
            || receipt.CostMethod != StockCostMethods.MovingAverage
            || receipt.ReversesValuationFactId is not null
            || receipt.BaseCostAmount != receipt.CostAmount)
            throw Invalid($"Receipt fact {receiptFactId} has no active WAC authority.");
        var receiptOwner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == receipt.StockPostingId,
                cancellationToken);
        if (receiptOwner is null || receiptOwner.SealedAtUtc is null
            || receiptOwner.CompanyCode != context.CompanyCode
            || receiptOwner.BranchCode != context.BranchCode
            || receiptOwner.LedgerEpochId != context.Epoch.Id)
            throw Invalid($"Receipt fact {receiptFactId} has no sealed owner.");

        var rows = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.OriginalValuationFactId == receiptFactId
                && f.InventoryHistoryId == null)
            .ToArrayAsync(cancellationToken);
        var byId = rows.ToDictionary(f => f.Id);
        var reversedIds = new HashSet<long>();
        if (rows.Any(f => f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id
                || f.ItemCode != receipt.ItemCode
                || f.SourceLineId != receipt.SourceLineId
                || f.SourceDocumentType != receipt.SourceDocumentType
                || f.SourceDocumentId != receipt.SourceDocumentId
                || f.SourceDocumentNo != receipt.SourceDocumentNo
                || f.SourceDocumentLine != receipt.SourceDocumentLine
                || f.MovementCode != receipt.MovementCode
                || f.BaseCurrency != receipt.BaseCurrency
                || f.EffectiveAt != receipt.EffectiveAt
                || f.BusinessDate != receipt.BusinessDate
                || f.PeriodKey != receipt.PeriodKey
                || f.BaseQty != 0m
                || f.CostMethod != StockCostMethods.MovingAverage
                || f.Direction is not (-1 or 1)
                || f.CostAmount < 0m
                || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount)
            || rows.Where(f => f.ReversesValuationFactId.HasValue)
                .Any(r => r.ReversesValuationFactId is not long priorId
                    || !byId.TryGetValue(priorId, out var prior)
                    || prior.ReversesValuationFactId is not null
                    || !reversedIds.Add(priorId)
                    || r.ValuationStatus != StockValuationStatuses.Reversed
                    || r.Direction != -prior.Direction
                    || r.CostAmount != prior.CostAmount))
            throw Invalid($"Receipt fact {receiptFactId} has invalid value-delta lineage.");
        var active = rows.Where(f => f.ReversesValuationFactId is null
            && !reversedIds.Contains(f.Id)).ToArray();
        if (active.Length > 1 || active.Any(f =>
                f.ValuationStatus != StockValuationStatuses.Valued
                || f.TransactionCurrency is not null
                || f.TransactionCostAmount is not null
                || f.ExchangeRate is not null
                || f.BaseCostAmount != f.CostAmount))
            throw Invalid($"Receipt fact {receiptFactId} has ambiguous active value delta.");
        var priorActive = active.SingleOrDefault();
        if (priorActive is not null)
        {
            var priorOwner = await db.StockPostings.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == priorActive.StockPostingId,
                    cancellationToken);
            if (priorOwner is null || priorOwner.SealedAtUtc is null)
                throw Invalid($"Receipt fact {receiptFactId} has an unsealed active delta.");
            StockReplayWacValueEventFactory.CreateReceiptRestatement(
                priorActive, receipt, history, receiptOwner, priorOwner);
        }
        var desiredDelta = StockLedgerPrecision.Money(
            correctedReceiptAmount - receipt.CostAmount);
        if (desiredDelta == (priorActive?.SignedValue ?? 0m))
            return null;

        var periodRefusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            context.CompanyCode, context.BranchCode,
            receipt.EffectiveAt, cancellationToken);
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
            throw Invalid("A receipt WAC replay root line is already used.");

        var now = DateTime.UtcNow;
        StockValuationFact NewValue(int postingLineNo) => new()
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = 0,
            SourceLineId = receipt.SourceLineId,
            SourceDocumentType = receipt.SourceDocumentType,
            SourceDocumentId = receipt.SourceDocumentId,
            SourceDocumentNo = receipt.SourceDocumentNo,
            SourceDocumentLine = receipt.SourceDocumentLine,
            EffectiveAt = receipt.EffectiveAt,
            BusinessDate = receipt.BusinessDate,
            PeriodKey = receipt.PeriodKey,
            ItemCode = receipt.ItemCode,
            WarehouseCode = receipt.WarehouseCode,
            LocationCode = receipt.LocationCode,
            LotId = receipt.LotId,
            LotNo = receipt.LotNo,
            ItemStatus = receipt.ItemStatus,
            BaseUom = receipt.BaseUom,
            MovementCode = receipt.MovementCode,
            Direction = 1,
            BaseQty = 0m,
            CostMethod = StockCostMethods.MovingAverage,
            BaseCurrency = receipt.BaseCurrency,
            FromBalLocId = receipt.FromBalLocId,
            ToBalLocId = receipt.ToBalLocId,
            OriginalValuationFactId = receipt.Id,
            CreatedAtUtc = now,
            CreatedBy = context.UserId
        };
        StockValuationFact? reversal = null;
        if (priorActive is not null)
        {
            reversal = NewValue(reversalPostingLineNo);
            reversal.Direction = -priorActive.Direction;
            reversal.CostAmount = priorActive.CostAmount;
            reversal.BaseCostAmount = priorActive.BaseCostAmount;
            reversal.ValuationSource = StockValuationSources.OriginalReversal;
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
            reversal.ValuationVersion = priorActive.ValuationVersion;
            reversal.ReversesValuationFactId = priorActive.Id;
            db.StockValuationFacts.Add(reversal);
        }
        StockValuationFact? deltaFact = null;
        if (desiredDelta != 0m)
        {
            deltaFact = NewValue(replacementPostingLineNo);
            deltaFact.Direction = Math.Sign(desiredDelta);
            deltaFact.CostAmount = Math.Abs(desiredDelta);
            deltaFact.BaseCostAmount = deltaFact.CostAmount;
            deltaFact.UnitCost = StockLedgerPrecision.Money(
                deltaFact.CostAmount / receipt.BaseQty);
            deltaFact.ValuationSource = valuationSource;
            deltaFact.ValuationStatus = StockValuationStatuses.Valued;
            deltaFact.ValuationVersion = checked((priorActive?.ValuationVersion
                ?? receipt.ValuationVersion) + 1);
            db.StockValuationFacts.Add(deltaFact);
        }
        return new(reversal, deltaFact);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
