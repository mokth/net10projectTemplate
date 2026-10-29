using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    /// <summary>INV-03: locked BalLoc must have TransDate on or before the document date.</summary>
    private string? ValidateBalanceStockDate(
        int balLocId,
        DateTime? stockDate,
        DateTime documentDate,
        string? lotNo)
    {
        if (!stockDate.HasValue)
        {
            // The database id stays in the log; the operator only needs to know the balance is unusable.
            _logger.LogWarning(
                "Stock balance {BalLocId} has no stock date; posting refused until it is repaired.",
                balLocId);
            return IvStockDateRules.NullStockDateMessage();
        }

        if (!IvStockDateRules.IsAvailableOn(stockDate, documentDate))
            return IvStockDateRules.FutureStockMessage(lotNo, stockDate.Value, documentDate);

        return null;
    }

    /// <summary>INV-04: reject when posted history exists on a later calendar day.</summary>
    private async Task<string?> ValidateNoLaterDayMovementsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balLocIds,
        DateTime documentDate,
        int? excludeBatchNo,
        CancellationToken cancellationToken,
        int? repostedCustomerReturnBatchNo = null)
    {
        if (balLocIds.Count == 0)
            return null;

        var hits = await _posting.FindLaterDayMovementsAsync(
            db, companyCode, branchCode, balLocIds, documentDate, excludeBatchNo, cancellationToken);
        if (hits.Count == 0)
            return null;

        IvLaterMovementHit? hit = null;
        foreach (var candidate in hits)
        {
            if (repostedCustomerReturnBatchNo == candidate.BatchNo
                && await IsExactReversedFifoCustomerReturnAsync(db, companyCode,
                    branchCode, candidate, cancellationToken))
                continue;
            hit = candidate;
            break;
        }
        if (hit is null)
            return null;
        return IvStockMovementRules.LaterDayMovementMessage(
            hit.ICode, hit.Warehouse, hit.LotNo, hit.TrxDtTime, documentDate);
    }

    private static async Task<bool> IsExactReversedFifoCustomerReturnAsync(
        AppDbContext db, string companyCode, string branchCode,
        IvLaterMovementHit hit, CancellationToken cancellationToken)
    {
        var reversal = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == hit.HistoryId
                && h.CompanyCode == companyCode && h.BranchCode == branchCode,
                cancellationToken);
        if (reversal is null || reversal.LedgerVersion != 2
            || reversal.EntryRole != "REVERSAL" || reversal.BatchStatus != IvBatchStatuses.Posted
            || reversal.TrxType != IvTrxTypes.CustomerReturn
            || reversal.BatchNo != hit.BatchNo || reversal.ReversesHistoryId is not int originalId
            || reversal.StockPostingId is not long reversalPostingId)
            return false;
        var original = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == originalId
                && h.CompanyCode == companyCode && h.BranchCode == branchCode,
                cancellationToken);
        if (original is null || original.LedgerVersion != 2 || original.EntryRole != "ORIGINAL"
            || original.BatchStatus != IvBatchStatuses.Posted
            || original.TrxType != IvTrxTypes.CustomerReturn || original.BatchNo != hit.BatchNo
            || original.StockPostingId is not long originalPostingId
            || original.LedgerEpochId != reversal.LedgerEpochId
            || original.DocumentRevision != reversal.DocumentRevision
            || original.TrxLineNo != reversal.TrxLineNo || original.ICode != reversal.ICode
            || original.FromBalLocId != reversal.ToBalLocId
            || original.ToBalLocId != reversal.FromBalLocId
            || original.FromLotId != reversal.ToLotId || original.ToLotId != reversal.FromLotId
            || original.FrStdQty != reversal.ToStdQty || original.ToStdQty != reversal.FrStdQty
            || original.FrStdUom != reversal.ToStdUom || original.ToStdUom != reversal.FrStdUom
            || original.ToWarehouse != reversal.FrWarehouse
            || original.ToLocation != reversal.FrLocation || original.ToLotNo != reversal.FrLotNo
            || original.IStatus != reversal.IStatus
            || original.FromBalLocId is not null || original.ToBalLocId != hit.BalLocId
            || original.ToStdQty is not > 0m
            || await db.IvTrxHistories.AsNoTracking().CountAsync(h =>
                h.ReversesHistoryId == original.Id, cancellationToken) != 1)
            return false;
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => p.Id == originalPostingId || p.Id == reversalPostingId)
            .ToArrayAsync(cancellationToken);
        var originalPosting = postings.SingleOrDefault(p => p.Id == originalPostingId);
        var reversedPosting = postings.SingleOrDefault(p => p.Id == reversalPostingId);
        if (originalPosting is null || reversedPosting is null
            || originalPosting.CompanyCode != companyCode || originalPosting.BranchCode != branchCode
            || originalPosting.LedgerEpochId != original.LedgerEpochId
            || originalPosting.SourceModule != "INVENTORY"
            || originalPosting.SourceDocumentType != IvTrxTypes.CustomerReturn
            || originalPosting.SourceDocumentNo != original.BatchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || originalPosting.DocumentRevision != original.DocumentRevision
            || originalPosting.EffectiveAt != original.TrxDtTime
            || originalPosting.PostingRole != "PRIMARY" || originalPosting.SealedAtUtc is null
            || reversedPosting.CompanyCode != companyCode || reversedPosting.BranchCode != branchCode
            || reversedPosting.LedgerEpochId != originalPosting.LedgerEpochId
            || reversedPosting.SourceModule != originalPosting.SourceModule
            || reversedPosting.SourceDocumentType != originalPosting.SourceDocumentType
            || reversedPosting.SourceDocumentId != originalPosting.SourceDocumentId
            || reversedPosting.SourceDocumentNo != originalPosting.SourceDocumentNo
            || reversedPosting.DocumentRevision != originalPosting.DocumentRevision
            || reversedPosting.EffectiveAt != reversal.TrxDtTime
            || reversedPosting.PostingRole != "REVERSAL"
            || reversedPosting.ReversesPostingId != originalPosting.Id
            || reversedPosting.SealedAtUtc is null)
            return false;
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.InventoryHistoryId == original.Id || f.InventoryHistoryId == reversal.Id)
            .ToArrayAsync(cancellationToken);
        var returnFacts = facts.Where(f => f.InventoryHistoryId == original.Id
            && f.Direction == 1 && f.ReversesValuationFactId is null).ToArray();
        var reversalFacts = facts.Where(f => f.InventoryHistoryId == reversal.Id).ToArray();
        if (facts.Length != 2 || returnFacts.Length != 1 || reversalFacts.Length != 1
            || returnFacts[0].StockPostingId != originalPostingId
            || returnFacts[0].CompanyCode != companyCode || returnFacts[0].BranchCode != branchCode
            || returnFacts[0].LedgerEpochId != original.LedgerEpochId
            || returnFacts[0].ItemCode != original.ICode
            || returnFacts[0].CostMethod != StockCostMethods.Fifo
            || returnFacts[0].ValuationSource != StockValuationSources.OriginalSaleReturn
            || returnFacts[0].ValuationStatus != StockValuationStatuses.Valued
            || returnFacts[0].MovementCode != "SALE_RETURN_IN"
            || returnFacts[0].BaseQty != original.ToStdQty
            || returnFacts[0].BaseCostAmount != returnFacts[0].CostAmount
            || reversalFacts[0].StockPostingId != reversalPostingId
            || reversalFacts[0].ReversesValuationFactId != returnFacts[0].Id
            || reversalFacts[0].Direction != -1
            || reversalFacts[0].MovementCode != "SALE_RETURN_IN_REVERSAL"
            || reversalFacts[0].ValuationStatus != StockValuationStatuses.Reversed
            || reversalFacts[0].ValuationSource != StockValuationSources.OriginalReversal
            || reversalFacts[0].CompanyCode != returnFacts[0].CompanyCode
            || reversalFacts[0].BranchCode != returnFacts[0].BranchCode
            || reversalFacts[0].LedgerEpochId != returnFacts[0].LedgerEpochId
            || reversalFacts[0].ItemCode != returnFacts[0].ItemCode
            || reversalFacts[0].CostMethod != returnFacts[0].CostMethod
            || reversalFacts[0].BaseQty != returnFacts[0].BaseQty
            || reversalFacts[0].CostAmount != returnFacts[0].CostAmount
            || reversalFacts[0].UnitCost != returnFacts[0].UnitCost
            || reversalFacts[0].BaseCostAmount != returnFacts[0].BaseCostAmount
            || reversalFacts[0].BaseCurrency != returnFacts[0].BaseCurrency
            || reversalFacts[0].TransactionCurrency != returnFacts[0].TransactionCurrency
            || reversalFacts[0].TransactionCostAmount != returnFacts[0].TransactionCostAmount
            || reversalFacts[0].ExchangeRate != returnFacts[0].ExchangeRate
            || await db.StockValuationFacts.AsNoTracking().CountAsync(f =>
                f.ReversesValuationFactId == returnFacts[0].Id, cancellationToken) != 1)
            return false;
        var layers = await db.StockFifoLayers.AsNoTracking().Where(l =>
            l.OriginValuationFactId == returnFacts[0].Id).ToArrayAsync(cancellationToken);
        if (layers.Length != 1 || layers[0].CompanyCode != companyCode
            || layers[0].BranchCode != branchCode || layers[0].ItemCode != original.ICode
            || layers[0].OriginStockPostingId != originalPostingId
            || layers[0].OriginalQty != returnFacts[0].BaseQty
            || layers[0].OriginalValue != returnFacts[0].CostAmount
            || layers[0].AccumulatedAdjustment != 0m || layers[0].RemainingQty != 0m
            || layers[0].RemainingValue != 0m || layers[0].CurrentUnitCost != 0m
            || layers[0].Status != StockFifoLayerStatuses.Closed
            || !await StockReplayFifoSalesReturnValueAdjustment.HasOnlyReversedConsumptionsAsync(
                db, returnFacts[0], layers[0].Id, cancellationToken))
            return false;
        var roots = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == companyCode && a.BranchCode == branchCode
                && a.ReturnValuationFactId == returnFacts[0].Id && a.ReversesAllocationId == null)
            .ToArrayAsync(cancellationToken);
        var saleFactIds = roots.Select(a => a.OriginalValuationFactId).Distinct().ToArray();
        var saleFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => saleFactIds.Contains(f.Id)).ToArrayAsync(cancellationToken);
        if (roots.Length == 0 || saleFacts.Length != saleFactIds.Length
            || saleFacts.Any(f => f.InventoryHistoryId is null
                || f.SourceDocumentType != roots[0].OriginalOwnerType
                || f.SourceDocumentNo != roots[0].OriginalOwnerDocumentNo)
            || saleFacts.Select(f => f.InventoryHistoryId).Distinct().Count() != 1)
            return false;
        var sale = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == saleFacts[0].InventoryHistoryId && h.CompanyCode == companyCode
            && h.BranchCode == branchCode && h.TrxType == IvTrxTypes.SalesOut,
            cancellationToken);
        return sale is not null && sale.SoLineNo is > 0 && sale.ICode == original.ICode
            && await HasUnreturnedSaleFactsAsync(db, sale, roots[0].OriginalOwnerType,
                roots[0].OriginalOwnerDocumentNo, StockCostMethods.Fifo, cancellationToken);
    }

    // Value-only corrections keep the returned layer's original business date.
    // They cease to be a Sales rollback dependency only after that same sale's
    // return, allocations and every value correction have all been reversed.
    internal static async Task<bool> IsNeutralizedFifoReturnForSalesRollbackAsync(
        AppDbContext db, string companyCode, string branchCode, long returnFactId,
        IReadOnlyCollection<int> saleHistoryIds, CancellationToken cancellationToken)
    {
        if (saleHistoryIds.Count == 0)
            return false;
        var returned = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
            f.Id == returnFactId && f.CompanyCode == companyCode && f.BranchCode == branchCode,
            cancellationToken);
        if (returned?.InventoryHistoryId is not int returnHistoryId)
            return false;
        var original = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == returnHistoryId && h.CompanyCode == companyCode && h.BranchCode == branchCode,
            cancellationToken);
        if (original?.ToBalLocId is not int balLocId)
            return false;
        var reversals = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.ReversesHistoryId == original.Id && h.CompanyCode == companyCode
            && h.BranchCode == branchCode).ToArrayAsync(cancellationToken);
        if (reversals.Length != 1)
            return false;
        var saleFactIds = await db.SalesReturnCostAllocations.AsNoTracking().Where(a =>
                a.CompanyCode == companyCode && a.BranchCode == branchCode
                && a.ReturnValuationFactId == returned.Id && a.ReversesAllocationId == null)
            .Select(a => a.OriginalValuationFactId).Distinct().ToArrayAsync(cancellationToken);
        var sales = await db.StockValuationFacts.AsNoTracking().Where(f => saleFactIds.Contains(f.Id))
            .Select(f => f.InventoryHistoryId).Distinct().ToArrayAsync(cancellationToken);
        if (saleFactIds.Length == 0 || sales.Length != 1 || sales[0] is not int saleId
            || !saleHistoryIds.Contains(saleId))
            return false;
        var reversal = reversals[0];
        var hit = new IvLaterMovementHit(balLocId, reversal.Id, reversal.BatchNo,
            reversal.TrxDtTime, reversal.ICode, reversal.FrWarehouse, reversal.FrLotNo);
        return await IsExactReversedFifoCustomerReturnAsync(db, companyCode, branchCode,
            hit, cancellationToken);
    }

    // A replay correction belongs at the return's original business position,
    // even when its writer has a sequence later than the issue being rolled back.
    internal static async Task<bool> IsVerifiedFifoReturnBeforeRollbackAsync(
        AppDbContext db, string companyCode, string branchCode, long returnFactId,
        DateTime targetEffectiveAt, long targetPostingSequence, CancellationToken cancellationToken)
    {
        var returned = await db.StockValuationFacts.AsNoTracking().Include(f => f.StockPosting)
            .SingleOrDefaultAsync(f => f.Id == returnFactId && f.CompanyCode == companyCode
                && f.BranchCode == branchCode, cancellationToken);
        if (returned?.StockPosting is not { } owner || owner.SealedAtUtc is null
            || owner.CompanyCode != companyCode || owner.BranchCode != branchCode
            || owner.LedgerEpochId != returned.LedgerEpochId
            || owner.EffectiveAt != returned.EffectiveAt || returned.EffectiveAt != targetEffectiveAt
            || owner.PostingSequence >= targetPostingSequence)
            return false;
        var roots = await db.SalesReturnCostAllocations.AsNoTracking().Where(a =>
            a.CompanyCode == companyCode && a.BranchCode == branchCode
            && a.ReturnValuationFactId == returned.Id && a.ReversesAllocationId == null)
            .OrderBy(a => a.Id).ToArrayAsync(cancellationToken);
        if (roots.Length == 0 || roots.Length > 100)
            return false;
        var rootIds = roots.Select(a => a.Id).ToArray();
        var children = await db.SalesReturnCostAllocations.AsNoTracking().Where(a =>
            a.ReversesAllocationId.HasValue && rootIds.Contains(a.ReversesAllocationId.Value))
            .ToArrayAsync(cancellationToken);
        var active = roots.Where(r => !children.Any(c => c.ReversesAllocationId == r.Id)).ToArray();
        var firstSale = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
            f.Id == roots[0].OriginalValuationFactId, cancellationToken);
        if (firstSale?.InventoryHistoryId is not int saleId)
            return false;
        var sale = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == saleId && h.CompanyCode == companyCode && h.BranchCode == branchCode,
            cancellationToken);
        var layer = await db.StockFifoLayers.AsNoTracking().SingleOrDefaultAsync(l =>
            l.OriginValuationFactId == returned.Id, cancellationToken);
        if (sale is null || layer is null || layer.CompanyCode != companyCode
            || layer.BranchCode != branchCode || layer.ItemCode != returned.ItemCode
            || layer.OriginalQty != returned.BaseQty || layer.OriginalValue != returned.CostAmount)
            return false;
        IReadOnlyList<StockValuationFact> adjustments;
        try
        {
            adjustments = await StockReplayFifoSalesReturnValueAdjustment.ReadVerifiedAsync(
                db, returned, cancellationToken);
        }
        catch (StockLedgerException)
        {
            return false;
        }
        if (layer.AccumulatedAdjustment != StockLedgerPrecision.Money(adjustments.Sum(f => f.SignedValue))
            || !await HasExactFifoSalesReturnLineageAsync(db, roots.Concat(children).ToArray(),
                sale, returned, active.FirstOrDefault()?.Id, active.Length == 0, cancellationToken))
            return false;
        try
        {
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(db,
                companyCode, branchCode, returned.ItemCode, cancellationToken);
            var liveLayer = snapshot.Layers.SingleOrDefault(l => l.OriginFactId == returned.Id);
            return active.Length == 0 ? liveLayer is null
                : liveLayer?.SalesReturn is { } binding && active.Any(a => a.Id == binding.AllocationId)
                    && binding.ReturnedQuantity == StockLedgerPrecision.Quantity(active.Sum(a => a.ReturnedBaseQty))
                    && binding.ReturnedValue == StockLedgerPrecision.Money(active.Sum(a => a.ReturnedCostAmount));
        }
        catch (StockLedgerException)
        {
            return false;
        }
    }

    /// <summary>INV-05: reject rollback when a later persisted movement remains on any BalLoc.</summary>
    private async Task<string?> ValidateRollbackChronologyAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        IReadOnlyList<IvTrxHistory> history,
        CancellationToken cancellationToken)
    {
        var targets = history
            .SelectMany(h => new[]
            {
                h.FromBalLocId is int fromId ? (BalLocId: fromId, HistoryId: h.Id, Date: h.TrxDtTime) : default,
                h.ToBalLocId is int toId ? (BalLocId: toId, HistoryId: h.Id, Date: h.TrxDtTime) : default
            })
            .Where(x => x.BalLocId > 0)
            .GroupBy(x => x.BalLocId)
            .Select(g => (
                BalLocId: g.Key,
                TargetDate: g.Max(x => x.Date),
                TargetMaxHistoryId: g.Max(x => x.HistoryId)))
            .ToList();

        if (targets.Count == 0)
            return null;

        var hits = await _posting.FindLaterRollbackMovementsAsync(
            db, companyCode, branchCode, targets, batchNo, cancellationToken);
        if (hits.Count == 0)
            return null;

        var hit = hits[0];
        // The balance id stays in the log; the operator sees "this stock balance" instead.
        _logger.LogDebug(
            "Rollback of batch {BatchNo} blocked by later movement on stock balance {BalLocId} dated {LaterDate:O}.",
            batchNo, hit.BalLocId, hit.TrxDtTime);
        return IvStockMovementRules.RollbackLaterMovementMessage(batchNo, hit.TrxDtTime);
    }

    /// <summary>
    /// After inverse qty, repair TransDate from remaining history (including zero-net BalLocs).
    /// Opening piles with leftover quantity keep their current stock date when no other history remains.
    /// </summary>
    private async Task RepairTransDatesAfterRollbackAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int excludeBatchNo,
        IEnumerable<int> balLocIds,
        CancellationToken cancellationToken)
    {
        foreach (var balLocId in balLocIds.Distinct().OrderBy(x => x))
        {
            var current = await db.IvBalLocs
                .AsNoTracking()
                .Where(x => x.Id == balLocId)
                .Select(x => new { x.TransDate, x.StdQty })
                .FirstOrDefaultAsync(cancellationToken);
            var repaired = await ResolveRollbackTransDateAsync(
                db,
                companyCode,
                branchCode,
                balLocId,
                excludeBatchNo,
                current?.TransDate,
                current?.StdQty ?? 0m,
                cancellationToken);
            await _posting.SetBalLocTransDateAsync(
                db, balLocId, companyCode, branchCode, repaired, cancellationToken);
        }
    }

    /// <summary>
    /// Restore TransDate from remaining movements. If this batch was the only history and
    /// quantity remains (opening stock), keep the pile's current date instead of nulling it.
    /// </summary>
    private async Task<DateTime?> ResolveRollbackTransDateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        int excludeBatchNo,
        DateTime? currentTransDate,
        decimal quantityAfterRollback,
        CancellationToken cancellationToken)
    {
        var latest = _openPeriodBackdatedReplayEnabled
            ? await db.IvTrxHistories.AsNoTracking()
                .Where(h => h.CompanyCode == companyCode && h.BranchCode == branchCode
                    && h.BatchNo != excludeBatchNo
                    && (h.FromBalLocId == balLocId || h.ToBalLocId == balLocId)
                    && (h.LedgerVersion != 2
                        || h.EntryRole == "ORIGINAL" && h.StockPostingId != null
                            && db.StockPostings.Any(p => p.Id == h.StockPostingId
                                && p.CompanyCode == companyCode && p.BranchCode == branchCode
                                && p.SealedAtUtc != null)
                            && !db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id
                                && r.EntryRole == "REVERSAL" && r.StockPostingId != null
                                && db.StockPostings.Any(p => p.Id == r.StockPostingId
                                    && p.CompanyCode == companyCode && p.BranchCode == branchCode
                                    && p.SealedAtUtc != null))))
                .OrderByDescending(h => h.TrxDtTime).ThenByDescending(h => h.Id)
                .Select(h => (DateTime?)h.TrxDtTime).FirstOrDefaultAsync(cancellationToken)
            : await _posting.GetLatestRemainingMovementAsync(
                db, companyCode, branchCode, balLocId, excludeBatchNo, cancellationToken);
        if (latest.HasValue)
            return latest;
        return quantityAfterRollback > 0m ? currentTransDate : null;
    }
}
