using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Append-only value changes to an existing FIFO customer-return layer.</summary>
internal static class StockReplayFifoSalesReturnValueAdjustment
{
    internal const string Source = "FIFO_SALES_RETURN_REPLAY";
    internal const string Movement = "SALE_RETURN_VALUE";

    internal static StockValuationFact Create(StockPostingContext context,
        StockValuationFact origin, decimal signedValue, int line, int ordinal = 0)
    {
        context.EnsureUnsealed();
        if (signedValue == 0m || StockLedgerPrecision.Money(signedValue) != signedValue
            || origin.BaseQty <= 0m || origin.CostMethod != StockCostMethods.Fifo
            || origin.MovementCode != "SALE_RETURN_IN" || line <= 0 || ordinal < 0)
            throw Invalid("FIFO Sales return adjustment has invalid value or origin.");
        var amount = Math.Abs(signedValue);
        return new StockValuationFact
        {
            CompanyCode = origin.CompanyCode, BranchCode = origin.BranchCode,
            LedgerEpochId = origin.LedgerEpochId, StockPostingId = context.Posting.Id,
            PostingLineNo = line, SplitOrdinal = ordinal,
            SourceLineId = origin.SourceLineId, SourceDocumentType = origin.SourceDocumentType,
            SourceDocumentId = origin.SourceDocumentId, SourceDocumentNo = origin.SourceDocumentNo,
            SourceDocumentLine = origin.SourceDocumentLine, EffectiveAt = origin.EffectiveAt,
            BusinessDate = origin.BusinessDate, PeriodKey = origin.PeriodKey,
            ItemCode = origin.ItemCode, WarehouseCode = origin.WarehouseCode,
            LocationCode = origin.LocationCode, LotId = origin.LotId, LotNo = origin.LotNo,
            ItemStatus = origin.ItemStatus, BaseUom = origin.BaseUom,
            MovementCode = Movement, Direction = Math.Sign(signedValue), BaseQty = 0m,
            CostMethod = StockCostMethods.Fifo,
            UnitCost = StockLedgerPrecision.Money(amount / origin.BaseQty),
            CostAmount = amount, BaseCostAmount = amount, BaseCurrency = origin.BaseCurrency,
            ValuationSource = Source, ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(origin.ValuationVersion + 1),
            ToBalLocId = origin.ToBalLocId, FromBalLocId = origin.FromBalLocId,
            OriginalValuationFactId = origin.Id, CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
    }

    internal static bool Matches(StockValuationFact fact, StockValuationFact origin, decimal signedValue) =>
        signedValue != 0m && fact.CompanyCode == origin.CompanyCode
        && fact.BranchCode == origin.BranchCode && fact.LedgerEpochId == origin.LedgerEpochId
        && fact.OriginalValuationFactId == origin.Id && fact.ReversesValuationFactId is null
        && fact.InventoryHistoryId is null && fact.BaseQty == 0m
        && fact.PostingLineNo > 0 && fact.SplitOrdinal >= 0
        && fact.Direction == Math.Sign(signedValue) && fact.CostAmount == Math.Abs(signedValue)
        && fact.CostAmount > 0m && StockLedgerPrecision.Money(fact.CostAmount) == fact.CostAmount
        && fact.BaseCostAmount == fact.CostAmount
        && fact.UnitCost == StockLedgerPrecision.Money(fact.CostAmount / origin.BaseQty)
        && fact.CostMethod == StockCostMethods.Fifo && fact.MovementCode == Movement
        && fact.ValuationSource == Source && fact.ValuationStatus == StockValuationStatuses.Valued
        && (long)fact.ValuationVersion == (long)origin.ValuationVersion + 1
        && fact.SourceLineId == origin.SourceLineId && fact.SourceDocumentType == origin.SourceDocumentType
        && fact.SourceDocumentId == origin.SourceDocumentId && fact.SourceDocumentNo == origin.SourceDocumentNo
        && fact.SourceDocumentLine == origin.SourceDocumentLine && fact.EffectiveAt == origin.EffectiveAt
        && fact.BusinessDate == origin.BusinessDate && fact.PeriodKey == origin.PeriodKey
        && fact.ItemCode == origin.ItemCode && fact.WarehouseCode == origin.WarehouseCode
        && fact.LocationCode == origin.LocationCode && fact.LotId == origin.LotId
        && fact.LotNo == origin.LotNo && fact.ItemStatus == origin.ItemStatus
        && fact.BaseUom == origin.BaseUom && fact.BaseCurrency == origin.BaseCurrency
        && fact.FromBalLocId == origin.FromBalLocId && fact.ToBalLocId == origin.ToBalLocId
        && fact.TransactionCurrency is null && fact.TransactionCostAmount is null && fact.ExchangeRate is null;

    internal static async Task<IReadOnlyList<StockValuationFact>> ReadVerifiedAsync(
        AppDbContext db, StockValuationFact origin, CancellationToken cancellationToken)
    {
        if (origin.BaseQty <= 0m || origin.Direction != 1
            || origin.CostMethod != StockCostMethods.Fifo || origin.MovementCode != "SALE_RETURN_IN"
            || origin.ValuationSource != StockValuationSources.OriginalSaleReturn)
            throw Invalid("FIFO Sales return value evidence has an invalid origin.");
        var rows = await db.StockValuationFacts.AsNoTracking().Include(f => f.StockPosting)
            .Where(f => f.OriginalValuationFactId == origin.Id && f.InventoryHistoryId == null)
            .ToArrayAsync(cancellationToken);
        if (rows.GroupBy(f => f.StockPostingId).Any(g => g.Count() != 1)
            || rows.Any(f => !Matches(f, origin, f.SignedValue)
                || f.StockPosting is null || f.StockPosting.SealedAtUtc is null
                || f.StockPosting.CompanyCode != origin.CompanyCode
                || f.StockPosting.BranchCode != origin.BranchCode
                || f.StockPosting.LedgerEpochId != origin.LedgerEpochId
                || f.StockPosting.SourceModule != "INVENTORY"
                || !(f.StockPosting.SourceDocumentType is (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                        && f.StockPosting.PostingRole is ("PRIMARY" or "REVERSAL")
                    || f.StockPosting.SourceDocumentType == IvTrxTypes.CustomerReturn
                        && f.StockPosting.PostingRole == "REVERSAL"
                        && f.StockPosting.ReversesPostingId == origin.StockPostingId)))
            throw Invalid("FIFO Sales return value adjustment evidence is inconsistent.");
        return rows;
    }

    // A return can be reversed after later issues have themselves been reversed.
    // Historical consumption rows remain immutable, so verify their complete
    // sealed reversal pairs instead of requiring that no rows ever existed.
    internal static async Task<bool> HasOnlyReversedConsumptionsAsync(
        AppDbContext db, StockValuationFact origin, long layerId,
        CancellationToken cancellationToken)
    {
        var rows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => c.FifoLayerId == layerId).ToArrayAsync(cancellationToken);
        if (rows.Length == 0)
            return true;
        var roots = rows.Where(c => c.ReversesConsumptionId is null).ToArray();
        var children = rows.Where(c => c.ReversesConsumptionId is not null).ToArray();
        if (roots.Length == 0 || children.Length != roots.Length
            || roots.Any(r => children.Count(c => c.ReversesConsumptionId == r.Id) != 1)
            || children.Any(c => !roots.Any(r => r.Id == c.ReversesConsumptionId)))
            return false;
        var childIds = children.Select(c => c.Id).ToArray();
        if (await db.StockFifoLayerConsumptions.AsNoTracking().AnyAsync(c =>
                c.ReversesConsumptionId.HasValue && childIds.Contains(c.ReversesConsumptionId.Value),
                cancellationToken))
            return false;
        var factIds = rows.Select(c => c.IssueValuationFactId).Distinct().ToArray();
        var facts = await db.StockValuationFacts.AsNoTracking().Include(f => f.StockPosting)
            .Where(f => factIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        var rootFactIds = roots.Select(c => c.IssueValuationFactId).Distinct().ToArray();
        var factReversals = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.ReversesValuationFactId.HasValue && rootFactIds.Contains(f.ReversesValuationFactId.Value))
            .ToArrayAsync(cancellationToken);
        if (facts.Count != factIds.Length || rootFactIds.Length != roots.Length
            || factReversals.Length != roots.Length)
            return false;
        foreach (var root in roots)
        {
            var child = children.Single(c => c.ReversesConsumptionId == root.Id);
            var issue = facts[root.IssueValuationFactId];
            var reversal = facts[child.IssueValuationFactId];
            if (root.CompanyCode != origin.CompanyCode || root.BranchCode != origin.BranchCode
                || child.CompanyCode != root.CompanyCode || child.BranchCode != root.BranchCode
                || child.FifoLayerId != root.FifoLayerId || child.ConsumedQty != root.ConsumedQty
                || child.ConsumedValue != root.ConsumedValue || child.SplitOrdinal != root.SplitOrdinal
                || issue.CompanyCode != origin.CompanyCode || issue.BranchCode != origin.BranchCode
                || issue.LedgerEpochId != origin.LedgerEpochId || issue.ItemCode != origin.ItemCode
                || issue.CostMethod != StockCostMethods.Fifo || issue.Direction != -1
                || issue.ValuationSource != StockValuationSources.Fifo
                || issue.ValuationStatus != StockValuationStatuses.Valued
                || issue.BaseQty != root.ConsumedQty || issue.CostAmount != root.ConsumedValue
                || issue.BaseCostAmount != issue.CostAmount
                || issue.SplitOrdinal != root.SplitOrdinal
                || issue.StockPosting is null || issue.StockPosting.SealedAtUtc is null
                || issue.StockPosting.CompanyCode != origin.CompanyCode
                || issue.StockPosting.BranchCode != origin.BranchCode
                || issue.StockPosting.LedgerEpochId != origin.LedgerEpochId
                || issue.StockPosting.SourceModule != "INVENTORY"
                || reversal.ReversesValuationFactId != issue.Id || reversal.Direction != 1
                || reversal.CompanyCode != issue.CompanyCode || reversal.BranchCode != issue.BranchCode
                || reversal.LedgerEpochId != issue.LedgerEpochId || reversal.ItemCode != issue.ItemCode
                || reversal.CostMethod != issue.CostMethod || reversal.BaseQty != issue.BaseQty
                || reversal.CostAmount != issue.CostAmount || reversal.UnitCost != issue.UnitCost
                || reversal.BaseCostAmount != issue.BaseCostAmount || reversal.BaseCurrency != issue.BaseCurrency
                || reversal.ValuationSource != StockValuationSources.OriginalReversal
                || reversal.ValuationStatus != StockValuationStatuses.Reversed
                || reversal.SplitOrdinal != child.SplitOrdinal
                || reversal.StockPosting is null || reversal.StockPosting.SealedAtUtc is null
                || reversal.StockPosting.CompanyCode != origin.CompanyCode
                || reversal.StockPosting.BranchCode != origin.BranchCode
                || reversal.StockPosting.LedgerEpochId != origin.LedgerEpochId
                || reversal.StockPosting.SourceModule != "INVENTORY"
                || factReversals.Count(f => f.ReversesValuationFactId == issue.Id
                    && f.Id == reversal.Id) != 1)
                return false;
        }
        return true;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
