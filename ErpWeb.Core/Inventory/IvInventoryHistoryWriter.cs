using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Core.Inventory;

public interface IIvInventoryHistoryWriter
{
    void StampGeneration(StockPostingContext context, IEnumerable<IvTrxHistory> rows, int documentRevision);
    IReadOnlyList<IvTrxHistory> AppendReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision);
}

/// <summary>Creates immutable V2 inventory generations; it never removes an accepted history row.</summary>
public sealed class IvInventoryHistoryWriter : IIvInventoryHistoryWriter
{
    public void StampGeneration(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> rows,
        int documentRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        context.EnsureUnsealed();
        var line = 0;
        foreach (var row in rows.OrderBy(x => x.TrxLineNo))
        {
            if (row.LedgerVersion is not null)
                throw new InvalidOperationException("Inventory history was already assigned to a ledger generation.");
            row.LedgerVersion = 2;
            row.LedgerEpochId = context.Epoch.Id;
            row.StockPostingId = context.Posting.Id;
            row.PostingLineNo = ++line;
            row.DocumentRevision = documentRevision;
            row.EntryRole = "ORIGINAL";
            row.TrxDtTime = context.Posting.EffectiveAt;
        }
    }

    public IReadOnlyList<IvTrxHistory> AppendReversal(
        StockPostingContext context,
        IEnumerable<IvTrxHistory> originals,
        int documentRevision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(originals);
        context.EnsureUnsealed();
        var result = new List<IvTrxHistory>();
        var line = 0;
        foreach (var source in originals.OrderBy(x => x.PostingLineNo).ThenBy(x => x.Id))
        {
            if (source.LedgerVersion != 2 || source.Id <= 0)
                throw new InvalidOperationException("Only persisted V2 history can be reversed append-only.");

            var reversal = new IvTrxHistory
            {
                CompanyCode = source.CompanyCode,
                BranchCode = source.BranchCode,
                BatchNo = source.BatchNo,
                TrxLineNo = source.TrxLineNo,
                TrxDtTime = context.Posting.EffectiveAt,
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
