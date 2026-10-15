using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record CapturedStockCountLedgerCost(decimal UnitCost, string Hash);

/// <summary>
/// Reconstructs the immutable WAC evidence available at an original count
/// watermark. Later corrections have later writer sequences and cannot change
/// this hash, even when they inherit an earlier business event key.
/// </summary>
public static class StockCountLedgerCostEvidence
{
    public static async Task<CapturedStockCountLedgerCost> CaptureWacAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int countLineId,
        string itemCode,
        DateTime countEffectiveAt,
        long originalWatermark,
        CancellationToken cancellationToken = default)
    {
        if (originalWatermark <= 0 || countLineId <= 0
            || countEffectiveAt != StockReplayEventKey.CountEndOfDay(countEffectiveAt))
            throw Corrupt("The original count cost watermark or event key is invalid.");

        var activeEpochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(e => e.CompanyCode == companyCode && e.BranchCode == branchCode
                && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2
                && e.EffectiveFrom <= countEffectiveAt)
            .Select(e => e.Id)
            .ToArrayAsync(cancellationToken);
        if (activeEpochs.Length != 1)
            throw Corrupt($"Count line {countLineId} has no unique active V2 cost epoch.");
        var epochId = activeEpochs[0];

        var rows = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode && f.BranchCode == branchCode
                && f.LedgerEpochId == epochId
                && f.ItemCode == itemCode && f.EffectiveAt <= countEffectiveAt
                && f.StockPosting != null
                && f.StockPosting.SealedAtUtc != null
                && f.StockPosting.PostingSequence <= originalWatermark)
            .Select(f => new
            {
                f.Id, f.StockPostingId, f.PostingLineNo, f.SplitOrdinal,
                f.InventoryHistoryId, f.OriginalValuationFactId,
                f.ReversesValuationFactId, f.EffectiveAt,
                f.CostMethod, f.Direction, f.BaseQty, f.CostAmount,
                f.ValuationStatus,
                PostingSequence = f.StockPosting!.PostingSequence
            })
            .OrderBy(f => f.Id)
            .ToArrayAsync(cancellationToken);
        if (rows.Length == 0
            || rows.Any(f => f.CostMethod != StockCostMethods.MovingAverage
                || f.Direction is not (-1 or 1)
                || f.BaseQty < 0m || f.CostAmount < 0m
                || f.ValuationStatus is not (StockValuationStatuses.Valued or StockValuationStatuses.Reversed)))
            throw Corrupt($"Count line {countLineId} has no consistent original WAC evidence.");

        var quantity = StockLedgerPrecision.Quantity(rows.Sum(f => f.Direction * f.BaseQty));
        var value = StockLedgerPrecision.Money(rows.Sum(f => f.Direction * f.CostAmount));
        if (quantity <= 0m || value < 0m)
            throw Corrupt($"Count line {countLineId} has no approved positive original WAC state.");

        var unitCost = StockLedgerPrecision.Money(value / quantity);
        var hash = StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, countLineId, itemCode,
            countEffectiveAt, originalWatermark, unitCost,
            facts = rows
        }, new { }).RequestFingerprint;
        return new(unitCost, hash);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.ValuationRequired, message));
}
