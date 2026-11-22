using ErpWeb.Core.Inventory;
using static ErpWeb.Core.Purchase.PurchaseReplayChronologyGuard;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    private sealed record CanceledSaleInvoiceReplayPlan(
        string Fingerprint, long EpochId, long Watermark, string ItemCode,
        string PoNo, short PoRelNo, short PoLineNo, string SaleInvoiceNo, int SaleBatchId, int SaleBatchNo, int SaleLineId,
        bool Posting, bool PrimaryCorrection, PurchaseCostAdjustmentRequest? InitialAdjustment,
        long PurchasePostingId, long PurchaseFactId, long ReceiptFactId, int BalanceId,
        int SaleHistoryId, int ReversalHistoryId, long SaleFactId, long ReversalFactId,
        long? SaleCorrectionId, long? ReversalCorrectionId, long[] SaleCycleFactIds,
        decimal SaleQuantity, decimal OriginalSaleCost, decimal CurrentSaleCost, decimal CorrectedSaleCost,
        decimal InventoryVariance, decimal CurrentQuantity, decimal CurrentValue, decimal FinalValue);

    // The initial supported shape is deliberately narrow: one untouched direct
    // sale and its exact cancellation, with no other later stock or value events.
    // The read is repeated under the branch lock before it can authorize a write.
    private async Task<CanceledSaleInvoiceReplayPlan?> PlanCanceledSaleInvoiceReplayAsync(
        AppDbContext db, PoInvoice invoice, CancellationToken ct, bool posting = false)
    {
        if (invoice.Type != PoInvoiceTypes.Invoice
            || invoice.Status != (posting ? PoInvoiceStatuses.New : PoInvoiceStatuses.Posted)
            || invoice.DeletedAtUtc is not null || invoice.Details.Count != 1)
            return null;
        var firstPost = posting && invoice.CostingRevision == 0;
        var sourceRevision = posting && !firstPost ? invoice.CostingRevision - 1 : invoice.CostingRevision;
        var line = invoice.Details.Single();
        if (string.IsNullOrWhiteSpace(line.ICode) || string.IsNullOrWhiteSpace(line.PoNo)
            || line.PoRelNo is null || line.PoLineNo is null)
            return null;
        var company = invoice.CompanyCode;
        var branch = invoice.BranchCode;
        var item = line.ICode.Trim();
        // Avoid running the settlement preview twice for ordinary invoices.
        // This route only accepts a sale strictly later than the invoice date;
        // the existing guard still handles every other canceled-sale shape.
        if (firstPost && !await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == company && h.BranchCode == branch && h.ICode == item
                && h.TrxType == IvTrxTypes.SalesOut && h.TrxDtTime > invoice.DocDate
                && db.IvTrxHistories.Any(r => r.CompanyCode == company && r.BranchCode == branch
                    && r.ReversesHistoryId == h.Id), ct))
            return null;
        var candidate = firstPost ? await PlanConsumedInvoiceAsync(db, invoice, ct, allowCanceledSaleOnly: true) : null;
        if (firstPost && (candidate is null || candidate.IssueReplay is not null
                || candidate.Adjustment.InventoryAdjustmentAmount != candidate.Adjustment.TotalAdjustmentAmount
                || candidate.Adjustment.ConsumedVarianceAmount != 0m))
            return null;
        var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SourceModule == "PROCUREMENT"
            && p.SourceDocumentType == "PO_INVOICE" && p.SourceDocumentId == invoice.DocNo
            && p.DocumentRevision == sourceRevision && p.PostingRole == "PRIMARY"
            && p.SealedAtUtc != null).ToArrayAsync(ct);
        if (firstPost ? roots.Length != 0 : roots.Length != 1
                || roots[0].CommandType != "PROCUREMENT_PO_INVOICE_PRIMARY"
                || roots[0].EffectiveAt != invoice.DocDate || roots[0].ReversesPostingId is not null)
            return null;
        var root = firstPost ? new StockPosting
        {
            LedgerEpochId = candidate!.EpochId, EffectiveAt = invoice.DocDate,
            PostingSequence = checked(candidate.Watermark + 1)
        } : roots[0];
        if (!await HasLaterCanceledSaleAsync(db, company, branch, root.LedgerEpochId,
                root.EffectiveAt, root.PostingSequence, [item], ct))
            return null;
        var rootReversals = firstPost ? Array.Empty<StockPosting>() : await db.StockPostings.AsNoTracking().Where(p => p.ReversesPostingId == root.Id).ToArrayAsync(ct);
        if (!firstPost && (posting ? rootReversals.Length != 1 || rootReversals[0].SealedAtUtc is null
                || rootReversals[0].CompanyCode != company || rootReversals[0].BranchCode != branch
                || rootReversals[0].LedgerEpochId != root.LedgerEpochId || rootReversals[0].PostingRole != "REVERSAL"
            : rootReversals.Length != 0))
            return null;
        var priorReversal = posting && !firstPost ? rootReversals[0] : null;
        var invoiceSnapshot = new
        {
            invoice.DocNo, invoice.Type, invoice.DocDate, invoice.Currency, invoice.CurrRate, CostingRevision = sourceRevision,
            Lines = invoice.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReferencedInvoiceLineNo
            }).ToArray()
        };
        if (!firstPost && (StockPostingFingerprint.Create(new { Kind = "INVOICE_ROLLBACK_SOURCE" }, invoiceSnapshot).SourceSnapshotHash
                != root.SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(root.SourceSnapshotJson)) != root.SourceSnapshotHash))
            return null;
        long purchaseFactId, receiptFactId, settlementId;
        decimal inventoryVariance;
        string purchaseFingerprint;
        if (firstPost)
        {
            if (await db.StockPostings.AsNoTracking().AnyAsync(p => p.CompanyCode == company && p.BranchCode == branch
                    && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_INVOICE"
                    && p.SourceDocumentId == invoice.DocNo, ct))
                return null;
            purchaseFactId = 0;
            receiptFactId = candidate!.ReceiptFactId;
            settlementId = 0;
            inventoryVariance = candidate.Adjustment.InventoryAdjustmentAmount;
            purchaseFingerprint = candidate.Fingerprint;
        }
        else
        {
            var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
                a.CompanyCode == company && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE"
                && a.SourceDocumentNo == invoice.DocNo && a.SourceCostingRevision == sourceRevision
                && a.StockPostingId == root.Id).ToArrayAsync(ct);
            var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
                s.CompanyCode == company && s.BranchCode == branch && s.PiDocNo == invoice.DocNo
                && s.PiCostingRevision == sourceRevision && s.StockPostingId == root.Id).ToArrayAsync(ct);
            if (adjustments.Length != 1 || settlements.Length != 1
                || adjustments[0].StockPostingId != root.Id || adjustments[0].ReversesAdjustmentId is not null
                || adjustments[0].InventoryAdjustmentAmount == 0m || adjustments[0].ConsumedVarianceAmount != 0m
                || adjustments[0].InventoryAdjustmentAmount != adjustments[0].TotalAdjustmentAmount
                || adjustments[0].InventoryAdjustmentFactId is not long existingPurchaseFactId
                || settlements[0].StockPostingId != root.Id || settlements[0].ReversesSettlementId is not null)
                return null;
            var adjustmentReversals = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
                a.ReversesAdjustmentId == adjustments[0].Id).ToArrayAsync(ct);
            var settlementReversals = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
                s.ReversesSettlementId == settlements[0].Id).ToArrayAsync(ct);
            if (posting ? adjustmentReversals.Length != 1 || settlementReversals.Length != 1
                    || adjustmentReversals[0].StockPostingId != priorReversal!.Id
                    || settlementReversals[0].StockPostingId != priorReversal.Id
                : adjustmentReversals.Length != 0 || settlementReversals.Length != 0)
                return null;
            if (await db.PurchaseCostAdjustments.AsNoTracking().CountAsync(a => a.CompanyCode == company
                    && a.BranchCode == branch && a.SourceDocumentType == "PO_INVOICE" && a.SourceDocumentNo == invoice.DocNo
                    && a.SourceCostingRevision == sourceRevision, ct) != (posting ? 2 : 1)
                || await db.PurchaseReceiptCostSettlements.AsNoTracking().CountAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.PiDocNo == invoice.DocNo && s.PiCostingRevision == sourceRevision, ct)
                    != (posting ? 2 : 1))
                return null;
            var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, adjustments, ct);
            if (generation is null || generation.OriginalFact.Id != existingPurchaseFactId || generation.ActiveFact.Id != existingPurchaseFactId
                || generation.Original.ItemCode != item || generation.Original.PoNo != line.PoNo
                || generation.Original.PoRelNo != line.PoRelNo || generation.Original.PoLineNo != line.PoLineNo
                || settlements[0].ReceiptValuationFactId != generation.OriginalFact.OriginalValuationFactId
                || settlements[0].SettledBaseQty != line.StdQty)
                return null;
            var purchaseReversals = await db.StockValuationFacts.AsNoTracking().Where(f =>
                f.ReversesValuationFactId == existingPurchaseFactId).ToArrayAsync(ct);
            if (posting ? purchaseReversals.Length != 1 || purchaseReversals[0].StockPostingId != priorReversal!.Id
                : purchaseReversals.Length != 0)
                return null;
            purchaseFactId = existingPurchaseFactId;
            receiptFactId = settlements[0].ReceiptValuationFactId;
            settlementId = settlements[0].Id;
            inventoryVariance = adjustments[0].InventoryAdjustmentAmount;
            purchaseFingerprint = generation.Fingerprint;
        }
        var epochs = await db.StockLedgerEpochs.AsNoTracking().Where(e => e.CompanyCode == company
            && e.BranchCode == branch && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2).ToArrayAsync(ct);
        if (epochs.Length != 1 || epochs[0].Id != root.LedgerEpochId
            || await _costMethods.ResolveAsync(db, company, branch, root.EffectiveAt, ct) != StockCostMethods.MovingAverage)
            return null;
        var watermark = await db.StockPostingBranchSequences.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch).Select(s => (long?)s.LastSequence).SingleOrDefaultAsync(ct) ?? 0;
        var latest = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SealedAtUtc != null).MaxAsync(p => (long?)p.PostingSequence, ct) ?? 0;
        if (watermark < latest || watermark == long.MaxValue || firstPost && watermark != candidate!.Watermark)
            return null;
        var states = await db.StockCostStates.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch && s.ItemCode == item).ToArrayAsync(ct);
        var balances = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == item).ToArrayAsync(ct);
        if (states.Length != 1 || states[0].CostMethod != StockCostMethods.MovingAverage
            || balances.Length != 1 || balances[0].StdQty != states[0].OnHandBaseQty)
            return null;

        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => h.CompanyCode == company
            && h.BranchCode == branch && h.ICode == item && h.TrxDtTime >= root.EffectiveAt).ToArrayAsync(ct);
        var ownerIds = histories.Where(h => h.StockPostingId.HasValue).Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        if (histories.Any(h => h.StockPostingId is not long id || !owners.ContainsKey(id)))
            return null;
        var later = histories.Where(h => h.TrxDtTime > root.EffectiveAt
            || h.TrxDtTime == root.EffectiveAt && owners[h.StockPostingId!.Value].PostingSequence > root.PostingSequence).ToArray();
        if (later.Length != 2)
            return null;
        var sale = later.SingleOrDefault(h => h.TrxType == IvTrxTypes.SalesOut && h.EntryRole == "ORIGINAL");
        var reverse = sale is null ? null : later.SingleOrDefault(h => h.ReversesHistoryId == sale.Id);
        if (sale is null || reverse is null || sale.StockPostingId is not long saleOwnerId
            || reverse.StockPostingId is not long reverseOwnerId || sale.LedgerVersion != 2
            || reverse.LedgerVersion != 2 || sale.LedgerEpochId != root.LedgerEpochId
            || reverse.LedgerEpochId != root.LedgerEpochId || sale.FromBalLocId != balances[0].Id
            || sale.ToBalLocId is not null || sale.FrStdQty is not > 0m
            || reverse.EntryRole != "REVERSAL" || reverse.ToBalLocId != sale.FromBalLocId
            || reverse.FromBalLocId is not null || reverse.ToStdQty != sale.FrStdQty
            || reverse.ToStdUom != sale.FrStdUom || reverse.InvNo != sale.InvNo || reverse.DoNo != sale.DoNo
            || reverse.IStatus != sale.IStatus || sale.FromLotId is not null || reverse.ToLotId is not null
            || !string.IsNullOrWhiteSpace(sale.FrLotNo) || !string.IsNullOrWhiteSpace(reverse.ToLotNo)
            || !string.IsNullOrWhiteSpace(sale.DoNo) || string.IsNullOrWhiteSpace(sale.InvNo)
            || sale.SoLineNo is not > 0 || sale.TrxDtTime.Year != root.EffectiveAt.Year
            || sale.TrxDtTime.Month != root.EffectiveAt.Month || sale.TrxDtTime <= root.EffectiveAt
            || reverse.TrxDtTime.Year != root.EffectiveAt.Year
            || reverse.TrxDtTime.Month != root.EffectiveAt.Month)
            return null;
        var saleOwner = owners[saleOwnerId];
        var reverseOwner = owners[reverseOwnerId];
        if (saleOwner.CompanyCode != company || saleOwner.BranchCode != branch || saleOwner.LedgerEpochId != root.LedgerEpochId
            || saleOwner.SealedAtUtc is null || saleOwner.SourceModule != "INVENTORY"
            || saleOwner.SourceDocumentType != IvTrxTypes.SalesOut || saleOwner.PostingRole != "PRIMARY"
            || saleOwner.CommandType != $"IV_POST_{IvTrxTypes.SalesOut}" || saleOwner.EffectiveAt != sale.TrxDtTime
            || reverseOwner.CompanyCode != company || reverseOwner.BranchCode != branch
            || reverseOwner.LedgerEpochId != root.LedgerEpochId || reverseOwner.SealedAtUtc is null
            || reverseOwner.SourceModule != "INVENTORY" || reverseOwner.SourceDocumentType != IvTrxTypes.SalesOut
            || reverseOwner.PostingRole != "REVERSAL" || reverseOwner.CommandType != $"IV_ROLLBACK_{IvTrxTypes.SalesOut}"
            || reverseOwner.ReversesPostingId != saleOwner.Id || reverseOwner.EffectiveAt != reverse.TrxDtTime
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(saleOwner.SourceSnapshotJson)) != saleOwner.SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(reverseOwner.SourceSnapshotJson)) != reverseOwner.SourceSnapshotHash)
            return null;
        var saleInvoice = await db.SaInvoices.AsNoTracking().Include(i => i.Details).SingleOrDefaultAsync(i =>
            i.CompanyCode == company && i.BranchCode == branch && i.InvNo == sale.InvNo, ct);
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyCode == company
            && b.BranchCode == branch && b.BatchNo == sale.BatchNo, ct);
        var details = batch is null ? [] : await db.IvTrxBatchDetails.AsNoTracking().Where(d =>
            d.BatchId == batch.Id).ToArrayAsync(ct);
        if (saleInvoice is null || saleInvoice.Status != "NEW" || saleInvoice.DeletedAtUtc is not null
            || saleInvoice.RollbackDate is null || saleInvoice.InvDate.Date != sale.TrxDtTime.Date
            || saleInvoice.Details.Count != 1 || batch is null || batch.TrxType != IvTrxTypes.SalesOut
            || batch.BatchStatus != IvBatchStatuses.New || batch.RefNo != sale.InvNo || details.Length != 1
            || sale.DocumentRevision != saleOwner.DocumentRevision
            || details[0].DocumentRevision != saleOwner.DocumentRevision + 1
            || saleOwner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || saleOwner.SourceDocumentNo != sale.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || await db.SaCdns.AsNoTracking().AnyAsync(n => n.CompanyCode == company && n.BranchCode == branch
                && n.InvNo == sale.InvNo, ct))
            return null;
        var saleLine = saleInvoice.Details.Single();
        if (!saleLine.StockControl || saleLine.LinkDo || saleLine.Line != sale.SoLineNo || saleLine.ICode != item
            || saleLine.StdQty != sale.FrStdQty || saleLine.StdUom != sale.FrStdUom
            || details[0].TrxLineNo != sale.TrxLineNo || details[0].InvNo != sale.InvNo
            || details[0].ICode != item || details[0].FromBalLocId != sale.FromBalLocId
            || details[0].FrStdQty != sale.FrStdQty)
            return null;
        var saleFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == sale.Id).ToArrayAsync(ct);
        var reverseFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == reverse.Id).ToArrayAsync(ct);
        if (saleFacts.Length != 1 || reverseFacts.Length != 1)
            return null;
        var saleFact = saleFacts[0];
        var reverseFact = reverseFacts[0];
        if (saleFact.CompanyCode != company || saleFact.BranchCode != branch || saleFact.LedgerEpochId != root.LedgerEpochId
            || saleFact.StockPostingId != saleOwner.Id || saleFact.ItemCode != item || saleFact.MovementCode != "SALE_OUT"
            || saleFact.SourceDocumentType != "SA_INVOICE" || saleFact.SourceDocumentId != sale.InvNo
            || saleFact.SourceDocumentNo != sale.InvNo || saleFact.CostMethod != StockCostMethods.MovingAverage
            || saleFact.SourceDocumentLine != sale.SoLineNo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || saleFact.EffectiveAt != saleOwner.EffectiveAt || saleFact.BusinessDate != saleOwner.BusinessDate
            || saleFact.PeriodKey != saleOwner.PeriodKey || saleFact.PostingLineNo != sale.PostingLineNo
            || saleFact.Direction != -1 || saleFact.BaseQty != sale.FrStdQty || saleFact.FromBalLocId != sale.FromBalLocId
            || saleFact.BaseUom != sale.FrStdUom || saleFact.ValuationStatus != StockValuationStatuses.Valued
            || saleFact.ValuationSource != StockValuationSources.MovingAverage || saleFact.CostAmount < 0m
            || saleFact.ItemStatus != sale.IStatus || saleFact.LotId != sale.FromLotId
            || (saleFact.LotNo ?? string.Empty) != (sale.FrLotNo ?? string.Empty)
            || saleFact.TransactionCurrency is not null || saleFact.TransactionCostAmount is not null || saleFact.ExchangeRate is not null
            || saleFact.BaseCostAmount != saleFact.CostAmount || saleFact.OriginalValuationFactId is not null
            || saleFact.ReversesValuationFactId is not null || reverseFact.CompanyCode != company
            || reverseFact.BranchCode != branch || reverseFact.LedgerEpochId != root.LedgerEpochId
            || reverseFact.StockPostingId != reverseOwner.Id || reverseFact.ItemCode != item
            || reverseFact.MovementCode != "SALE_OUT_REVERSAL" || reverseFact.Direction != 1
            || reverseFact.SourceDocumentType != saleFact.SourceDocumentType || reverseFact.SourceDocumentId != saleFact.SourceDocumentId
            || reverseFact.SourceDocumentNo != saleFact.SourceDocumentNo || reverseFact.SourceDocumentLine != saleFact.SourceDocumentLine
            || reverseFact.EffectiveAt != reverseOwner.EffectiveAt || reverseFact.BusinessDate != reverseOwner.BusinessDate
            || reverseFact.PeriodKey != reverseOwner.PeriodKey || reverseFact.PostingLineNo != reverse.PostingLineNo
            || reverseFact.BaseQty != saleFact.BaseQty || reverseFact.CostAmount != saleFact.CostAmount
            || reverseFact.BaseCostAmount != saleFact.BaseCostAmount || reverseFact.UnitCost != saleFact.UnitCost
            || reverseFact.BaseUom != saleFact.BaseUom || reverseFact.BaseCurrency != saleFact.BaseCurrency
            || reverseFact.TransactionCurrency is not null || reverseFact.TransactionCostAmount is not null || reverseFact.ExchangeRate is not null
            || reverseFact.ToBalLocId != sale.FromBalLocId || reverseFact.CostMethod != saleFact.CostMethod
            || reverseFact.ValuationStatus != StockValuationStatuses.Reversed
            || reverseFact.ValuationSource != StockValuationSources.OriginalReversal
            || reverseFact.ReversesValuationFactId != saleFact.Id || reverseFact.OriginalValuationFactId != saleFact.Id
            || await db.StockValuationFacts.AsNoTracking().CountAsync(f => f.ReversesValuationFactId == saleFact.Id, ct) != 1
            || await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a => a.OriginalValuationFactId == saleFact.Id
                || a.CompanyCode == company && a.BranchCode == branch && a.OriginalOwnerType == "SA_INVOICE"
                    && a.OriginalOwnerDocumentNo == sale.InvNo, ct))
            return null;
        var sourceIds = new[] { saleFact.Id, reverseFact.Id };
        var related = await db.StockValuationFacts.AsNoTracking().Where(f => f.OriginalValuationFactId.HasValue
            && sourceIds.Contains(f.OriginalValuationFactId.Value)).ToArrayAsync(ct);
        var corrections = related.Where(f => f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue).ToArray();
        var correctionIds = corrections.Select(f => f.Id).ToArray();
        var undoFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.ReversesValuationFactId.HasValue
            && correctionIds.Contains(f.ReversesValuationFactId.Value)).ToArrayAsync(ct);
        if (related.Any(f => f.Id != reverseFact.Id
                && f.MovementCode is not (StockValuationMovementClassifier.ReversedSaleReplayValue
                    or StockValuationMovementClassifier.ReversedSaleReplayValueUndo))
            || related.Count(f => f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValueUndo) != undoFacts.Length
            || undoFacts.Any(f => f.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValueUndo)
            || corrections.Length % 2 != 0 || undoFacts.Length % 2 != 0)
            return null;
        var correctionRootIds = corrections.Select(f => f.StockPostingId).Distinct().ToArray();
        if (correctionRootIds.Length > 100)
            return null;
        var primaryCorrection = firstPost;
        if (correctionRootIds.Length != 0)
        {
            var correctionRoots = await db.StockPostings.AsNoTracking().Where(p => correctionRootIds.Contains(p.Id)).ToArrayAsync(ct);
            if (correctionRoots.Length != correctionRootIds.Length || correctionRoots.Any(p => p.SourceModule != "PROCUREMENT"
                    || p.SourceDocumentType != "PO_INVOICE" || p.SourceDocumentId != invoice.DocNo)
                || correctionRoots.Select(p => p.PostingRole).Distinct().Count() != 1
                || correctionRoots.Any(p => p.PostingRole is not ("PRIMARY" or "REVERSAL"))
                || !await StockValuationSnapshotBuilder.ValidateReversedSaleCorrectionsAsync(db,
                    company, branch, correctionRootIds, ct))
                return null;
            primaryCorrection = correctionRoots[0].PostingRole == "PRIMARY";
        }
        var reversedCorrectionIds = undoFacts.Select(f => f.ReversesValuationFactId!.Value).ToHashSet();
        var activeCorrections = corrections.Where(f => !reversedCorrectionIds.Contains(f.Id)).ToArray();
        var saleCorrection = activeCorrections.SingleOrDefault(f => f.OriginalValuationFactId == saleFact.Id);
        var reverseCorrection = activeCorrections.SingleOrDefault(f => f.OriginalValuationFactId == reverseFact.Id);
        if (primaryCorrection
            ? posting ? activeCorrections.Length != 0 || !firstPost && !correctionRootIds.Contains(root.Id)
                : activeCorrections.Length != 2 || saleCorrection is null || reverseCorrection is null
                    || saleCorrection.StockPostingId != root.Id || reverseCorrection.StockPostingId != root.Id
            : posting ? activeCorrections.Length != 2 || saleCorrection is null || reverseCorrection is null
                    || saleCorrection.StockPostingId != priorReversal!.Id || reverseCorrection.StockPostingId != priorReversal.Id
                : activeCorrections.Length != 0)
            return null;
        var currentSaleCost = saleFact.CostAmount - (saleCorrection?.SignedValue ?? 0m);
        if (currentSaleCost < 0m || reverseFact.CostAmount + (reverseCorrection?.SignedValue ?? 0m) != currentSaleCost)
            return null;
        var cycleFactIds = corrections.Select(f => f.Id).Concat(undoFacts.Select(f => f.Id)).Order().ToArray();
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, sale.TrxDtTime, ct) is not null
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, reverse.TrxDtTime, ct) is not null)
            return null;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= root.EffectiveAt.Date && (p.EffectiveTo == null || p.EffectiveTo > root.EffectiveAt.Date))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveFrom, p.EffectiveTo, p.Status }).ToArrayAsync(ct);
        if (policies.Length != 1 || policies[0].EffectiveTo is DateTime end && end.Date <= reverse.TrxDtTime.Date
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p => p.CompanyCode == company
                && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active && p.Id != policies[0].Id
                && p.EffectiveFrom <= reverse.TrxDtTime.Date
                && (p.EffectiveTo == null || p.EffectiveTo > root.EffectiveAt.Date), ct))
            return null;
        // Reversed value-only pairs can affect the sale between their two dates,
        // even though they do not appear in the ordinary active tail.
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.CompanyCode == company
            && f.BranchCode == branch && f.ItemCode == item && f.InventoryHistoryId == null && f.BaseQty == 0m
            && f.ReversesValuationFactId == null && !cycleFactIds.Contains(f.Id) && f.EffectiveAt <= reverse.TrxDtTime
            && db.StockValuationFacts.Any(r => r.ReversesValuationFactId == f.Id && r.EffectiveAt >= sale.TrxDtTime), ct))
            return null;
        var anchor = new StockReplayEventKey(root.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            root.PostingSequence, $"V{purchaseFactId:D20}", 0);
        var saleKey = new StockReplayEventKey(saleOwner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            saleOwner.PostingSequence, $"H{sale.Id:D10}", 0);
        var reverseKey = new StockReplayEventKey(reverseOwner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            reverseOwner.PostingSequence, $"H{reverse.Id:D10}", 0);
        if (saleKey.CompareTo(anchor) <= 0 || reverseKey.CompareTo(saleKey) <= 0)
            return null;
        var physical = await StockReplayPhysicalAsOfReader.ReadAtKeyAsync(db, company, branch,
            [balances[0].Id], saleKey, ct);
        if (physical[balances[0].Id].AsOfBaseQty != balances[0].StdQty - saleFact.BaseQty)
            return null;
        var tail = cycleFactIds.Length == 0
            ? await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch, item, anchor, ct)
            : await StockReplayWacFinancialTailReader.ReadLaterForReversedSaleAsync(db, company, branch, item, anchor, cycleFactIds, ct);
        if (tail.LaterEvents.Count != 0 || tail.CurrentQuantity != states[0].OnHandBaseQty
            || tail.CurrentValue != states[0].InventoryValue)
            return null;
        var removed = new StockReplayWacEvent(long.MaxValue - purchaseFactId, anchor, 0m,
            inventoryVariance, 0m, inventoryVariance);
        var pair = new[]
        {
            new StockReplayWacEvent(sale.Id, saleKey, -saleFact.BaseQty, -currentSaleCost, -saleFact.BaseQty),
            new StockReplayWacEvent(reverse.Id, reverseKey, saleFact.BaseQty, currentSaleCost,
                saleFact.BaseQty, currentSaleCost, ReversedProductionIssueEventId: sale.Id)
        };
        var old = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
            removed with { OldSignedValue = 0m, FixedSignedValue = 0m }, pair);
        if (old.FinalQuantity != tail.CurrentQuantity || old.FinalValue != tail.CurrentValue
            || old.LaterSteps.Single(s => s.EventId == sale.Id).SignedValue != -currentSaleCost
            || old.LaterSteps.Single(s => s.EventId == reverse.Id).SignedValue != currentSaleCost)
            return null;
        IReadOnlyList<StockReplayWacStep> projectedSteps;
        decimal finalQuantity, finalValue;
        if (posting)
        {
            var projected = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
                removed with { OldSignedValue = 0m }, pair);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        else
        {
            var projected = StockReplayWacSimulator.SimulateRemoval(tail.CurrentQuantity, tail.CurrentValue, removed, pair);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        var correctedCost = -projectedSteps.Single(s => s.EventId == sale.Id).SignedValue;
        if (finalQuantity != tail.CurrentQuantity
            || finalValue != StockLedgerPrecision.Money(tail.CurrentValue
                + (posting ? inventoryVariance : -inventoryVariance))
            || correctedCost < 0m || correctedCost == currentSaleCost
            || (posting != primaryCorrection) && correctedCost != saleFact.CostAmount
            || projectedSteps.Single(s => s.EventId == reverse.Id).SignedValue != correctedCost)
            return null;
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo).Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo && p.Line == line.PoLineNo)
            .Select(p => new { p.ICode, p.PurchaseUom, p.RecvQty, p.ReturnQty, p.InvoicedQty }).SingleOrDefaultAsync(ct);
        if (order is null || orderLine is null || orderLine.ICode != item)
            return null;
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "PI_CANCELED_SALE_REMOVAL" }, new
        {
            invoice.DocNo, invoice.DocDate, invoice.RowVersion, invoice.CostingRevision, invoiceSnapshot,
            Root = new { root.Id, root.PostingSequence, root.RequestFingerprint, root.SourceSnapshotHash, root.LedgerEpochId },
            purchaseFingerprint, SettlementId = settlementId, policies, order, orderLine, watermark,
            Balance = new { balances[0].Id, balances[0].StdQty, balances[0].RowVersion },
            State = new { states[0].Id, states[0].OnHandBaseQty, states[0].InventoryValue,
                states[0].LastValuationFactId, states[0].LastPostingSequence, states[0].RowVersion },
            Sale = new { sale.Id, sale.StockPostingId, sale.TrxDtTime, sale.FromBalLocId, sale.FrStdQty,
                sale.InvNo, sale.SoLineNo, sale.DocumentRevision, SaleFactId = saleFact.Id, saleFact.CostAmount },
            Reverse = new { reverse.Id, reverse.StockPostingId, reverse.TrxDtTime, reverse.ToBalLocId,
                reverse.ToStdQty, FactId = reverseFact.Id, reverseFact.CostAmount },
            SaleOwner = new { saleOwner.Id, saleOwner.PostingSequence, saleOwner.RequestFingerprint, saleOwner.SourceSnapshotHash },
            ReverseOwner = new { reverseOwner.Id, reverseOwner.PostingSequence, reverseOwner.RequestFingerprint, reverseOwner.SourceSnapshotHash },
            SaleDocument = new { saleInvoice.InvNo, saleInvoice.RowVersion, saleInvoice.InvDate,
                saleInvoice.Status, saleInvoice.RollbackDate, saleLine.Id, saleLine.Line, saleLine.ICode,
                saleLine.StdQty, saleLine.StdUom, saleLine.NetAmount },
            Batch = new { batch.Id, batch.BatchNo, batch.BatchStatus, batch.RefNo }, posting, primaryCorrection,
            Cycle = cycleFactIds, CurrentSaleCost = currentSaleCost, CorrectedSaleCost = correctedCost,
            projectedSteps, finalQuantity, finalValue
        }).SourceSnapshotHash;
        return new(fingerprint, root.LedgerEpochId, watermark, item, line.PoNo.Trim(), line.PoRelNo.Value,
            line.PoLineNo.Value, sale.InvNo, batch.Id, sale.BatchNo, saleLine.Id, posting, primaryCorrection,
            candidate?.Adjustment, root.Id, purchaseFactId, receiptFactId, balances[0].Id,
            sale.Id, reverse.Id, saleFact.Id, reverseFact.Id, saleCorrection?.Id, reverseCorrection?.Id,
            cycleFactIds, saleFact.BaseQty, saleFact.CostAmount, currentSaleCost, correctedCost,
            inventoryVariance, tail.CurrentQuantity, tail.CurrentValue, finalValue);
    }

    private static async Task<bool> LockCanceledSaleInvoiceAsync(
        AppDbContext db, string company, string branch, CanceledSaleInvoiceReplayPlan plan, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer() || db.Database.CurrentTransaction is null)
            return false;
        // Sales and Inventory drafts may lock their own rows before the branch.
        // A busy row must fail immediately rather than deadlocking the replay.
        var invoices = await db.SaInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {plan.SaleInvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var lines = await db.SaInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {plan.SaleInvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var notes = await db.SaCdns.FromSqlInterpolated($@"SELECT * FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {plan.SaleInvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var batches = await db.IvTrxBatches.FromSqlInterpolated($@"SELECT * FROM dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND BatchNo = {plan.SaleBatchNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var details = await db.IvTrxBatchDetails.FromSqlInterpolated($@"SELECT * FROM dbo.IvTrxBatchDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE BatchId = {plan.SaleBatchId}").AsNoTracking().ToArrayAsync(ct);
        return invoices.Length == 1 && lines.Length == 1 && lines[0].Id == plan.SaleLineId
            && notes.Length == 0 && batches.Length == 1 && batches[0].Id == plan.SaleBatchId
            && details.Length == 1;
    }

    private static async Task WriteCanceledSaleInvoiceCorrectionAsync(
        StockPostingContext context, CanceledSaleInvoiceReplayPlan plan,
        PurchaseCostAdjustmentRequest request, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var value = request.ValueAdjustment;
        if (plan.Posting != plan.PrimaryCorrection || context.Db.Database.CurrentTransaction is null || context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "PROCUREMENT" || context.Posting.SourceDocumentType != "PO_INVOICE"
            || context.Posting.PostingRole != (plan.Posting ? "PRIMARY" : "REVERSAL")
            || (plan.Posting ? context.Posting.ReversesPostingId is not null : context.Posting.ReversesPostingId != plan.PurchasePostingId)
            || context.Epoch.Id != plan.EpochId || context.Posting.PostingSequence != checked(plan.Watermark + 1)
            || plan.Fingerprint.Length != 64 || value is null || value.PostingLineNo != request.SourceDocumentLine
            || value.PostingLineNo > int.MaxValue - 2 || value.SplitOrdinal != 0 || value.ItemCode != plan.ItemCode
            || value.Amount != Math.Abs(plan.InventoryVariance)
            || value.Direction * value.Amount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || value.Direction is not (-1 or 1)
            || (plan.Posting ? value.OriginalValuationFactId != plan.ReceiptFactId || value.ReversesValuationFactId is not null
                    || request.ReversesAdjustmentId is not null
                : value.OriginalValuationFactId != plan.PurchaseFactId || value.ReversesValuationFactId != plan.PurchaseFactId
                    || request.ReversesAdjustmentId is not > 0)
            || request.CostMethod != StockCostMethods.MovingAverage
            || request.SourceDocumentType != "PO_INVOICE" || request.SourceDocumentNo != context.Posting.SourceDocumentNo
            || request.SourceCostingRevision != context.Posting.DocumentRevision || request.ItemCode != plan.ItemCode
            || request.InventoryAdjustmentAmount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || request.TotalAdjustmentAmount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || request.ConsumedVarianceAmount != 0m
            || request.PoNo != plan.PoNo || request.PoRelNo != plan.PoRelNo || request.PoLineNo != plan.PoLineNo)
            throw InvoiceReplayInvalid("The canceled-sale replay has no matching locked invoice authority.");

        var db = context.Db;
        var saleFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == plan.SaleFactId, ct);
        var reverseFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == plan.ReversalFactId, ct);
        if (saleFact.CompanyCode != context.CompanyCode || saleFact.BranchCode != context.BranchCode
            || reverseFact.CompanyCode != context.CompanyCode || reverseFact.BranchCode != context.BranchCode
            || saleFact.LedgerEpochId != context.Epoch.Id || reverseFact.LedgerEpochId != context.Epoch.Id
            || saleFact.InventoryHistoryId != plan.SaleHistoryId || reverseFact.InventoryHistoryId != plan.ReversalHistoryId
            || reverseFact.ReversesValuationFactId != saleFact.Id || saleFact.CostAmount != plan.OriginalSaleCost
            || reverseFact.CostAmount != plan.OriginalSaleCost || saleFact.BaseQty != plan.SaleQuantity
            || reverseFact.BaseQty != plan.SaleQuantity || saleFact.ItemCode != plan.ItemCode
            || reverseFact.ItemCode != plan.ItemCode
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw InvoiceReplayInvalid("The canceled sale changed after invoice rollback planning.");

        AppendInvoiceReplayAdjustment(context, request, value);
        var now = DateTime.UtcNow;
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = new[] { context.Posting.EffectiveAt, saleFact.EffectiveAt, reverseFact.EffectiveAt }.Max(),
            PlanWatermark = plan.Watermark, PlanHash = plan.Fingerprint, AffectedHistoryCount = 2,
            AffectedItemCount = 1, AffectedProductionMovementCount = 0,
            CreatedAtUtc = now, CreatedBy = context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var delta = StockLedgerPrecision.Money(plan.CorrectedSaleCost - plan.OriginalSaleCost);
        var corrections = new[] { NewCorrection(saleFact, value.PostingLineNo + 1),
            NewCorrection(reverseFact, value.PostingLineNo + 2) };
        if (delta == 0m || corrections.Sum(f => f.SignedValue) != 0m)
            throw InvoiceReplayInvalid("The canceled-sale corrections do not balance.");
        db.StockValuationFacts.AddRange(corrections);
        await db.SaveChangesAsync(ct);
        var sources = new[] { saleFact, reverseFact };
        db.StockReplayEffects.AddRange(corrections.Select((correction, i) => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, ReplayRunId = run.Id,
            RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = sources[i].StockPostingId,
            EffectKind = StockReplayEffectTypes.SalesReversalValue, AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
            AffectedEntityId = correction.Id, OriginalEntityId = sources[i].Id, CreatedAtUtc = now
        }));
        var permit = new StockReplayWacWritePermit(context, plan.ItemCode, plan.Fingerprint,
            plan.CurrentQuantity, plan.CurrentValue, plan.CurrentQuantity, plan.FinalValue,
            plan.BalanceId, plan.CurrentQuantity);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);

        StockValuationFact NewCorrection(StockValuationFact source, int line) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id, PostingLineNo = line, SplitOrdinal = 0,
            SourceLineId = source.SourceLineId, SourceDocumentType = source.SourceDocumentType,
            SourceDocumentId = source.SourceDocumentId, SourceDocumentNo = source.SourceDocumentNo,
            SourceDocumentLine = source.SourceDocumentLine, EffectiveAt = source.EffectiveAt,
            BusinessDate = source.BusinessDate, PeriodKey = source.PeriodKey, ItemCode = source.ItemCode,
            WarehouseCode = source.WarehouseCode, LocationCode = source.LocationCode, LotId = source.LotId,
            LotNo = source.LotNo, ItemStatus = source.ItemStatus, BaseUom = source.BaseUom,
            MovementCode = StockValuationMovementClassifier.ReversedSaleReplayValue,
            Direction = source.Direction * Math.Sign(delta), BaseQty = 0m, CostMethod = StockCostMethods.MovingAverage,
            CostAmount = Math.Abs(delta), UnitCost = StockLedgerPrecision.Money(Math.Abs(delta) / plan.SaleQuantity),
            BaseCurrency = source.BaseCurrency, BaseCostAmount = Math.Abs(delta),
            ValuationSource = StockValuationSources.MovingAverage, ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(source.ValuationVersion + 1), FromBalLocId = source.FromBalLocId,
            ToBalLocId = source.ToBalLocId, OriginalValuationFactId = source.Id, CreatedAtUtc = now, CreatedBy = context.UserId
        };
    }

    private static async Task WriteCanceledSaleInvoiceCorrectionUndoAsync(
        StockPostingContext context, CanceledSaleInvoiceReplayPlan plan,
        PurchaseCostAdjustmentRequest request, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var value = request.ValueAdjustment;
        if (plan.Posting == plan.PrimaryCorrection || context.Db.Database.CurrentTransaction is null || context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "PROCUREMENT" || context.Posting.SourceDocumentType != "PO_INVOICE"
            || context.Posting.PostingRole != (plan.Posting ? "PRIMARY" : "REVERSAL")
            || (plan.Posting ? context.Posting.ReversesPostingId is not null : context.Posting.ReversesPostingId != plan.PurchasePostingId)
            || context.Epoch.Id != plan.EpochId || context.Posting.PostingSequence != checked(plan.Watermark + 1)
            || plan.Fingerprint.Length != 64 || plan.SaleCorrectionId is not long saleCorrectionId
            || plan.ReversalCorrectionId is not long reverseCorrectionId || value is null
            || value.PostingLineNo != request.SourceDocumentLine || value.PostingLineNo > int.MaxValue - 2
            || value.SplitOrdinal != 0 || value.ItemCode != plan.ItemCode
            || value.Amount != Math.Abs(plan.InventoryVariance)
            || value.Direction * value.Amount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || value.Direction is not (-1 or 1)
            || (plan.Posting ? value.OriginalValuationFactId != plan.ReceiptFactId || value.ReversesValuationFactId is not null
                    || request.ReversesAdjustmentId is not null
                : value.OriginalValuationFactId != plan.PurchaseFactId || value.ReversesValuationFactId != plan.PurchaseFactId
                    || request.ReversesAdjustmentId is not > 0)
            || request.CostMethod != StockCostMethods.MovingAverage || request.SourceDocumentType != "PO_INVOICE"
            || request.SourceDocumentNo != context.Posting.SourceDocumentNo
            || request.SourceCostingRevision != context.Posting.DocumentRevision || request.ItemCode != plan.ItemCode
            || request.InventoryAdjustmentAmount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || request.TotalAdjustmentAmount != (plan.Posting ? plan.InventoryVariance : -plan.InventoryVariance)
            || request.ConsumedVarianceAmount != 0m
            || request.PoNo != plan.PoNo || request.PoRelNo != plan.PoRelNo || request.PoLineNo != plan.PoLineNo)
            throw InvoiceReplayInvalid("The canceled-sale repost has no matching locked invoice authority.");
        var db = context.Db;
        var ids = new[] { saleCorrectionId, reverseCorrectionId };
        var prior = await db.StockValuationFacts.AsNoTracking().Where(f => ids.Contains(f.Id)).ToArrayAsync(ct);
        var sources = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.Id == plan.SaleFactId || f.Id == plan.ReversalFactId).ToArrayAsync(ct);
        if (prior.Length != 2 || sources.Length != 2 || prior.Any(f => f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode || f.LedgerEpochId != context.Epoch.Id
                || f.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValue
                || f.OriginalValuationFactId is not long sourceId || !sources.Any(s => s.Id == sourceId))
            || prior.Select(f => f.StockPostingId).Distinct().Count() != 1
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId.HasValue
                && ids.Contains(f.ReversesValuationFactId.Value), ct)
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw InvoiceReplayInvalid("The canceled-sale correction changed after invoice repost planning.");

        AppendInvoiceReplayAdjustment(context, request, value);
        var now = DateTime.UtcNow;
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt, LatestEffectiveAt = sources.Max(s => s.EffectiveAt),
            PlanWatermark = plan.Watermark, PlanHash = plan.Fingerprint, AffectedHistoryCount = 2,
            AffectedItemCount = 1, AffectedProductionMovementCount = 0, CreatedAtUtc = now, CreatedBy = context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var undo = prior.OrderBy(f => f.Id).Select((parent, index) => new StockValuationFact
        {
            CompanyCode = parent.CompanyCode, BranchCode = parent.BranchCode, LedgerEpochId = parent.LedgerEpochId,
            StockPostingId = context.Posting.Id, PostingLineNo = value.PostingLineNo + index + 1, SplitOrdinal = 0,
            SourceLineId = parent.SourceLineId, SourceDocumentType = parent.SourceDocumentType,
            SourceDocumentId = parent.SourceDocumentId, SourceDocumentNo = parent.SourceDocumentNo,
            SourceDocumentLine = parent.SourceDocumentLine, EffectiveAt = parent.EffectiveAt,
            BusinessDate = parent.BusinessDate, PeriodKey = parent.PeriodKey, ItemCode = parent.ItemCode,
            WarehouseCode = parent.WarehouseCode, LocationCode = parent.LocationCode, LotId = parent.LotId,
            LotNo = parent.LotNo, ItemStatus = parent.ItemStatus, BaseUom = parent.BaseUom,
            MovementCode = StockValuationMovementClassifier.ReversedSaleReplayValueUndo,
            Direction = -parent.Direction, BaseQty = 0m, CostMethod = parent.CostMethod,
            UnitCost = parent.UnitCost, CostAmount = parent.CostAmount, BaseCurrency = parent.BaseCurrency,
            BaseCostAmount = parent.BaseCostAmount, ValuationSource = StockValuationSources.OriginalReversal,
            ValuationStatus = StockValuationStatuses.Reversed, ValuationVersion = parent.ValuationVersion,
            FromBalLocId = parent.FromBalLocId, ToBalLocId = parent.ToBalLocId,
            OriginalValuationFactId = parent.OriginalValuationFactId, ReversesValuationFactId = parent.Id,
            CreatedAtUtc = now, CreatedBy = context.UserId
        }).ToArray();
        if (undo.Sum(f => f.SignedValue) != 0m)
            throw InvoiceReplayInvalid("The canceled-sale repost corrections do not balance.");
        db.StockValuationFacts.AddRange(undo);
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.AddRange(undo.Select(correction => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, ReplayRunId = run.Id,
            RootStockPostingId = context.Posting.Id,
            BusinessOwnerPostingId = sources.Single(s => s.Id == correction.OriginalValuationFactId).StockPostingId,
            EffectKind = StockReplayEffectTypes.SalesReversalValueUndo, AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
            AffectedEntityId = correction.Id, OriginalEntityId = correction.OriginalValuationFactId, CreatedAtUtc = now
        }));
        var permit = new StockReplayWacWritePermit(context, plan.ItemCode, plan.Fingerprint,
            plan.CurrentQuantity, plan.CurrentValue, plan.CurrentQuantity, plan.FinalValue,
            plan.BalanceId, plan.CurrentQuantity);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
    }
}
