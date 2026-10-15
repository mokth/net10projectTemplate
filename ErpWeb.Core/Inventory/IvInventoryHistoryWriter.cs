using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Core.Inventory;

public interface IIvInventoryHistoryWriter
{
    void StampGeneration(StockPostingContext context, IEnumerable<IvTrxHistory> rows, int documentRevision);
    void StampCountGeneration(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> rows,
        int documentRevision,
        DateTime countDate);
    IReadOnlyList<IvTrxHistory> AppendReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision);
    IReadOnlyList<IvTrxHistory> AppendCountReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision,
        DateTime countDate,
        int correctionBatchNo,
        int firstPostingLineNo);
}

/// <summary>Creates immutable V2 inventory generations; it never removes an accepted history row.</summary>
public sealed class IvInventoryHistoryWriter : IIvInventoryHistoryWriter
{
    public void StampGeneration(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> rows,
        int documentRevision)
        => StampCore(context, rows, documentRevision, null);

    public void StampCountGeneration(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> rows,
        int documentRevision,
        DateTime countDate)
        => StampCore(context, rows, documentRevision, countDate.Date);

    private static void StampCore(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> rows,
        int documentRevision,
        DateTime? countDate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        context.EnsureUnsealed();
        var line = 0;
        foreach (var row in rows.OrderBy(x => x.TrxLineNo))
        {
            if (row.LedgerVersion is not null)
                throw new InvalidOperationException("Inventory history was already assigned to a ledger generation.");
            if (countDate.HasValue && row.TrxType != IvTrxTypes.StockAdjustment)
                throw new InvalidOperationException("A count generation must use Stock Adjustment history.");
            row.LedgerVersion = 2;
            row.LedgerEpochId = context.Epoch.Id;
            row.StockPostingId = context.Posting.Id;
            row.PostingLineNo = ++line;
            row.DocumentRevision = documentRevision;
            row.EntryRole = "ORIGINAL";
            row.TrxDtTime = countDate ?? context.Posting.EffectiveAt;
        }
    }

    public IReadOnlyList<IvTrxHistory> AppendReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision)
        => AppendReversalCore(context, originals, documentRevision, null, null);

    public IReadOnlyList<IvTrxHistory> AppendCountReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision,
        DateTime countDate,
        int correctionBatchNo,
        int firstPostingLineNo)
        => AppendReversalCore(context, originals, documentRevision, countDate.Date,
            correctionBatchNo > 0 ? correctionBatchNo
                : throw new ArgumentOutOfRangeException(nameof(correctionBatchNo)),
            firstPostingLineNo > 0 ? firstPostingLineNo
                : throw new ArgumentOutOfRangeException(nameof(firstPostingLineNo)));

    private static IReadOnlyList<IvTrxHistory> AppendReversalCore(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision,
        DateTime? countDate,
        int? correctionBatchNo,
        int firstPostingLineNo = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(originals);
        context.EnsureUnsealed();
        var result = new List<IvTrxHistory>();
        var line = firstPostingLineNo - 1;
        short batchLine = 0;
        foreach (var source in originals.OrderBy(x => x.PostingLineNo).ThenBy(x => x.Id))
        {
            if (source.LedgerVersion != 2 || source.Id <= 0)
                throw new InvalidOperationException("Only persisted V2 history can be reversed append-only.");
            if (countDate.HasValue && (source.TrxType != IvTrxTypes.StockAdjustment
                || source.TrxDtTime.Date != countDate.Value))
                throw new InvalidOperationException("A count reversal must retain its count-date history stamp.");

            var reversal = new IvTrxHistory
            {
                CompanyCode = source.CompanyCode,
                BranchCode = source.BranchCode,
                BatchNo = correctionBatchNo ?? source.BatchNo,
                TrxLineNo = correctionBatchNo.HasValue
                    ? checked(++batchLine) : source.TrxLineNo,
                TrxDtTime = countDate ?? context.Posting.EffectiveAt,
                TrxType = source.TrxType,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = source.RefNo,
                ProdCode = source.ProdCode,
                ProdDesc = source.ProdDesc,
                ICode = source.ICode,
                IDesc = source.IDesc,
                FromBalLocId = source.ToBalLocId,
                ToBalLocId = source.FromBalLocId,
                FromLotId = source.ToLotId,
                ToLotId = source.FromLotId,
                FrWarehouse = source.ToWarehouse,
                FrLocation = source.ToLocation,
                FrLotNo = source.ToLotNo,
                FrStdQty = source.ToStdQty,
                FrStdUom = source.ToStdUom,
                FrPurQty = source.ToPurQty,
                FrPurUom = source.ToPurUom,
                ToWarehouse = source.FrWarehouse,
                ToLocation = source.FrLocation,
                ToLotNo = source.FrLotNo,
                ToStdQty = source.FrStdQty,
                ToStdUom = source.FrStdUom,
                ToPurQty = source.FrPurQty,
                ToPurUom = source.FrPurUom,
                IStatus = source.IStatus,
                DoNo = source.DoNo,
                InvNo = source.InvNo,
                SoNo = source.SoNo,
                PoNo = source.PoNo,
                PoRelNo = source.PoRelNo,
                SoLineNo = source.SoLineNo,
                PoLineNo = source.PoLineNo,
                Remarks = source.Remarks,
                ExactTransferredValue = source.ExactTransferredValue,
                ValuationStatus = source.ValuationStatus,
                EvidenceBaseQty = source.EvidenceBaseQty,
                EvidenceBaseUom = source.EvidenceBaseUom,
                PriceEvidence = source.PriceEvidence,
                CostEvidenceType = source.CostEvidenceType,
                CostOverrideReason = source.CostOverrideReason,
                CostApprovedBy = source.CostApprovedBy,
                CostApprovedAtUtc = source.CostApprovedAtUtc,
                StockCountCostBasisId = source.StockCountCostBasisId,
                Cost = source.Cost,
                CostPrice = source.CostPrice,
                AsNowCost = source.AsNowCost,
                UnitPrice = source.UnitPrice,
                BaseUnitPrices = source.BaseUnitPrices,
                LocationCode = source.LocationCode,
                CreatedDate = context.Posting.PostedAtUtc,
                CreatedBy = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId,
                LedgerVersion = 2,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = ++line,
                DocumentRevision = documentRevision,
                EntryRole = "REVERSAL",
                ReversesHistoryId = source.Id,
            };
            context.Db.IvTrxHistories.Add(reversal);
            result.Add(reversal);
        }
        return result;
    }
}
