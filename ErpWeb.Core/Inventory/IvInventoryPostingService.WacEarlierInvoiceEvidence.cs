using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // An earlier invoice settled against another GR does not need to be
    // reallocated when this later receipt is removed. Prove that exact
    // settlement and owner on both sides of the branch lock.
    private static async Task<string?> ProveEarlierInvoiceIndependentOfReceiptAsync(
        AppDbContext db, string company, string branch, PoInvoice invoice,
        int receiptHistoryId, long receiptOwnerId, CancellationToken ct)
    {
        if (invoice.Type != PoInvoiceTypes.Invoice || invoice.Status != PoInvoiceStatuses.Posted
            || invoice.DeletedAtUtc is not null || invoice.Details.Count != 1)
            return null;
        var line = invoice.Details.Single();
        if (string.IsNullOrWhiteSpace(line.PoNo) || line.PoRelNo is null || line.PoLineNo is null
            || line.Qty <= 0m || line.Qty != line.StdQty)
            return null;
        var candidate = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == receiptHistoryId && h.CompanyCode == company && h.BranchCode == branch, ct);
        var candidateOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
            p.Id == receiptOwnerId && p.CompanyCode == company && p.BranchCode == branch, ct);
        if (candidate is null || candidateOwner is null || candidate.StockPostingId != candidateOwner.Id
            || candidate.TrxType != IvTrxTypes.GoodsReceive || candidate.BatchStatus != IvBatchStatuses.Posted
            || candidate.EntryRole != "ORIGINAL" || candidate.ICode != line.ICode
            || candidate.PoNo != line.PoNo || candidate.PoRelNo != line.PoRelNo
            || candidate.PoLineNo != line.PoLineNo || candidateOwner.SealedAtUtc is null
            || invoice.DocDate >= candidate.TrxDtTime || candidateOwner.EffectiveAt != candidate.TrxDtTime)
            return null;
        var candidateFactIds = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.InventoryHistoryId == candidate.Id
            && f.StockPostingId == candidateOwner.Id && f.ReversesValuationFactId == null)
            .Select(f => f.Id).ToArrayAsync(ct);
        if (candidateFactIds.Length != 1 || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s =>
                s.CompanyCode == company && s.BranchCode == branch
                && (s.ReceiptInventoryHistoryId == candidate.Id || s.ReceiptValuationFactId == candidateFactIds[0]), ct))
            return null;
        if (await db.PoInvoices.AsNoTracking().AnyAsync(p => p.CompanyCode == company && p.BranchCode == branch
                && p.Type == PoInvoiceTypes.CreditNote && p.InvNo == invoice.DocNo
                && (p.Status == PoInvoiceStatuses.New || p.Status == PoInvoiceStatuses.Posted), ct)
            || await db.PoCdns.AsNoTracking().AnyAsync(p => p.CompanyCode == company && p.BranchCode == branch
                && p.InvNo == invoice.DocNo && (p.Status == "NEW" || p.Status == "POSTED"), ct))
            return null;
        var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_INVOICE"
            && p.SourceDocumentId == invoice.DocNo && p.DocumentRevision == invoice.CostingRevision
            && p.PostingRole == "PRIMARY" && p.SealedAtUtc != null).ToArrayAsync(ct);
        if (roots.Length != 1 || roots[0].CommandType != "PROCUREMENT_PO_INVOICE_PRIMARY"
            || roots[0].EffectiveAt != invoice.DocDate || roots[0].LedgerEpochId != candidateOwner.LedgerEpochId
            || roots[0].ReversesPostingId is not null || await db.StockPostings.AsNoTracking().AnyAsync(p =>
                p.ReversesPostingId == roots[0].Id && p.SealedAtUtc != null, ct))
            return null;
        var snapshot = new
        {
            invoice.DocNo, invoice.Type, invoice.DocDate, invoice.Currency, invoice.CurrRate, invoice.CostingRevision,
            Lines = invoice.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReferencedInvoiceLineNo
            }).ToArray()
        };
        if (StockPostingFingerprint.Create(new { Kind = "INVOICE_ROLLBACK_SOURCE" }, snapshot).SourceSnapshotHash
                != roots[0].SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(roots[0].SourceSnapshotJson))
                != roots[0].SourceSnapshotHash)
            return null;
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.PiDocNo == invoice.DocNo
            && s.PiCostingRevision == invoice.CostingRevision).ToArrayAsync(ct);
        if (settlements.Length != 1 || settlements[0].ReversesSettlementId is not null
            || settlements[0].StockPostingId != roots[0].Id || settlements[0].PiLineNo != line.Line
            || settlements[0].SettledBaseQty != line.StdQty || settlements[0].ReceiptInventoryHistoryId is not int sourceHistoryId
            || sourceHistoryId == candidate.Id || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s =>
                s.ReversesSettlementId == settlements[0].Id, ct))
            return null;
        var source = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == sourceHistoryId
            && h.CompanyCode == company && h.BranchCode == branch, ct);
        var sourceFact = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f =>
            f.Id == settlements[0].ReceiptValuationFactId && f.CompanyCode == company && f.BranchCode == branch, ct);
        if (source is null || sourceFact is null || source.TrxType != IvTrxTypes.GoodsReceive
            || source.BatchStatus != IvBatchStatuses.Posted || source.EntryRole != "ORIGINAL"
            || source.ICode != line.ICode || source.PoNo != line.PoNo || source.PoRelNo != line.PoRelNo
            || source.PoLineNo != line.PoLineNo || source.TrxDtTime > invoice.DocDate
            || source.ToStdQty != line.StdQty || sourceFact.InventoryHistoryId != source.Id
            || sourceFact.StockPostingId != source.StockPostingId || sourceFact.EffectiveAt != source.TrxDtTime
            || sourceFact.LedgerEpochId != candidateOwner.LedgerEpochId)
            return null;
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a => a.CompanyCode == company
            && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE"
            && a.SourceDocumentNo == invoice.DocNo && a.SourceCostingRevision == invoice.CostingRevision)
            .OrderBy(a => a.Id).ToArrayAsync(ct);
        var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, adjustments, ct);
        if (generation is null || generation.Original.StockPostingId != roots[0].Id
            || generation.OriginalFact.OriginalValuationFactId != sourceFact.Id
            || generation.Original.PoNo != line.PoNo || generation.Original.PoRelNo != line.PoRelNo
            || generation.Original.PoLineNo != line.PoLineNo || generation.Original.BaseQty != line.StdQty)
            return null;
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo)
            .Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo && p.Line == line.PoLineNo)
            .Select(p => new { p.ICode, p.RecvQty, p.ReturnQty, p.InvoicedQty }).SingleOrDefaultAsync(ct);
        if (order is null || orderLine is null || orderLine.ICode != line.ICode || orderLine.InvoicedQty != line.Qty)
            return null;
        return StockPostingFingerprint.Create(new { Kind = "GR_EARLIER_INDEPENDENT_INVOICE" }, new
        {
            snapshot, invoice.RowVersion, Root = new { roots[0].Id, roots[0].SourceSnapshotHash },
            generation.Fingerprint, Settlement = new { settlements[0].Id, settlements[0].StockPostingId,
                settlements[0].ReceiptInventoryHistoryId, settlements[0].ReceiptValuationFactId, settlements[0].SettledBaseQty },
            Source = new { HistoryId = source.Id, source.TrxDtTime, FactId = sourceFact.Id, sourceFact.CostAmount },
            Candidate = new { HistoryId = candidate.Id, candidate.TrxDtTime, OwnerId = candidateOwner.Id, FactId = candidateFactIds[0] },
            order, orderLine
        }).SourceSnapshotHash;
    }
}
