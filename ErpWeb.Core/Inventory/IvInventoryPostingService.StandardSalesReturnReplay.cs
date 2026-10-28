using ErpWeb.Core.Sales;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record SalesReturnPermit(
        int SaleHistoryId, string NoteNo, long? AllocationId,
        string CostMethod = StockCostMethods.Standard,
        bool ReversedWacAllocation = false);

    // A rolled-back note remains as a draft with immutable stock and cost reversal
    // evidence. Permit only the one draft that still describes that exact return;
    // the shared sales guard verifies the full reversal pair separately.
    private static async Task<SalesReturnPermit?>
        CheckRolledBackSalesReturnAsync(
            AppDbContext db, IvTrxHistory sale, string costMethod,
            CancellationToken cancellationToken)
    {
        var doNo = sale.DoNo?.Trim();
        var ownerType = string.IsNullOrWhiteSpace(doNo) ? "SA_INVOICE" : "SA_DO";
        var ownerNo = ownerType == "SA_INVOICE" ? sale.InvNo?.Trim() : doNo;
        if (string.IsNullOrWhiteSpace(ownerNo) || sale.SoLineNo is not > 0)
            return null;
        var invoiceNos = ownerType == "SA_INVOICE" ? [ownerNo] : await db.SaInvoiceDetails
            .AsNoTracking().Where(d => d.CompanyCode == sale.CompanyCode
                && d.BranchCode == sale.BranchCode && d.DoNo == doNo
                && d.DoLine == sale.SoLineNo)
            .Select(d => d.InvNo).Distinct().ToArrayAsync(cancellationToken);
        if (invoiceNos.Length != 1 || string.IsNullOrWhiteSpace(invoiceNos[0]))
            return null;
        var invoiceNo = invoiceNos[0];
        var invoice = await db.SaInvoices.AsNoTracking().Include(i => i.Details)
            .SingleOrDefaultAsync(i => i.CompanyCode == sale.CompanyCode
                && i.BranchCode == sale.BranchCode && i.InvNo == invoiceNo,
                cancellationToken);
        var notes = await db.SaCdns.AsNoTracking().Include(c => c.Details)
            .Where(c => c.CompanyCode == sale.CompanyCode
                && c.BranchCode == sale.BranchCode && c.InvNo == invoiceNo)
            .ToArrayAsync(cancellationToken);
        if (invoice is null || invoice.Details.Count != 1 || notes.Length != 1)
            return null;
        var note = notes[0];
        var invoiceLine = invoice.Details.Single();
        if (note.Status != SaCdnStatuses.New || note.Type != SaCdnTypes.CreditNote
            || !note.ReturnStock || note.CustCode != invoice.CustCode
            || note.Currency != invoice.Currency || note.Details.Count != 1)
            return null;
        var roots = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == sale.CompanyCode
                && a.BranchCode == sale.BranchCode && a.OriginalOwnerType == ownerType
                && a.OriginalOwnerDocumentNo == ownerNo
                && a.ReversesAllocationId == null).ToArrayAsync(cancellationToken);
        if (roots.Length == 0 || roots.Length > 100
            || costMethod is StockCostMethods.Standard or StockCostMethods.Fifo
                && roots.Length != 1)
            return null;
        var lastRoot = roots.OrderByDescending(a => a.Id).First();
        if (lastRoot.ReturnValuationFactId is not long returnFactId
            || lastRoot.ReturnDocumentType != "SA_CDN"
            || lastRoot.ReturnDocumentNo != note.DocNo
            || lastRoot.ReturnCostingRevision + 1 != note.CostingRevision)
            return null;
        var returnFact = await db.StockValuationFacts.AsNoTracking()
            .Include(f => f.InventoryHistory)
            .SingleOrDefaultAsync(f => f.Id == returnFactId, cancellationToken);
        var returned = returnFact?.InventoryHistory;
        var noteLine = note.Details.Single();
        if (returned is null || returned.InvNo != invoiceNo
            || returned.RefNo != SaCdnSpRefs.ToRefNo(note.DocNo)
            || returned.SoLineNo != invoiceLine.Line
            || returned.TrxDtTime.Date != note.DocDate.Date
            || returned.TrxDtTime.Date < sale.TrxDtTime.Date
            || returned.TrxDtTime.Year != sale.TrxDtTime.Year
            || returned.TrxDtTime.Month != sale.TrxDtTime.Month
            || noteLine.Line != returned.TrxLineNo
            || noteLine.SourceInvLine != invoiceLine.Line || !noteLine.StockControl
            || noteLine.ICode != sale.ICode || noteLine.StdQty != returned.ToStdQty
            || noteLine.StdUom != returned.ToStdUom
            || noteLine.FrWarehouse != returned.ToWarehouse
            || (noteLine.LocCode ?? string.Empty) != (returned.ToLocation ?? string.Empty)
            || noteLine.IStatus != returned.IStatus
            || (noteLine.LotNo ?? string.Empty) != (returned.ToLotNo ?? string.Empty))
            return null;
        if (costMethod == StockCostMethods.MovingAverage)
        {
            var activeSale = (await StockReplayValuationGenerationResolver.ResolveAsync(
                db, sale.CompanyCode, sale.BranchCode, [sale], cancellationToken))[sale.Id];
            var allocations = await db.SalesReturnCostAllocations.AsNoTracking()
                .Where(a => a.CompanyCode == sale.CompanyCode && a.BranchCode == sale.BranchCode
                    && a.OriginalOwnerType == ownerType && a.OriginalOwnerDocumentNo == ownerNo)
                .ToArrayAsync(cancellationToken);
            if (activeSale.Count != 1 || returnFact is null
                || !MatchesWacReturnDocumentCost(returnFact, returned, noteLine, note.Currency)
                || !await HasExactWacSalesReturnLineageAsync(db, allocations,
                    lastRoot, activeSale[0], returnFact, cancellationToken,
                    requireAllReversed: true))
                return null;
            return new SalesReturnPermit(sale.Id, note.DocNo, null,
                StockCostMethods.MovingAverage, ReversedWacAllocation: true);
        }
        return costMethod is StockCostMethods.Standard or StockCostMethods.Fifo
            ? new SalesReturnPermit(sale.Id, note.DocNo, null, costMethod) : null;
    }

    private static async Task<bool> HasExactStandardReturnVarianceReversalAsync(
        AppDbContext db, ErpWeb.Model.Entities.Sales.SalesReturnCostAllocation root,
        ErpWeb.Model.Entities.Sales.SalesReturnCostAllocation reversal,
        StockValuationFact returnFact, StockValuationFact reversalFact,
        CancellationToken cancellationToken)
    {
        var variances = await db.SalesReturnStandardCostVariances.AsNoTracking()
            .Where(v => v.ReturnValuationFactId == returnFact.Id
                || v.ReturnValuationFactId == reversalFact.Id)
            .ToArrayAsync(cancellationToken);
        if (variances.Length != 2)
            return false;
        var originals = variances.Where(v => v.ReturnValuationFactId == returnFact.Id
            && v.ReversesVarianceId == null).ToArray();
        if (originals.Length != 1)
            return false;
        var original = originals[0];
        var reversals = variances.Where(v => v.ReturnValuationFactId == reversalFact.Id
            && v.ReversesVarianceId == original.Id).ToArray();
        if (reversals.Length != 1)
            return false;
        var undone = reversals[0];
        if (original.CompanyCode != root.CompanyCode || original.BranchCode != root.BranchCode
            || original.StockPostingId != root.StockPostingId
            || original.ReturnDocumentType != root.ReturnDocumentType
            || original.ReturnDocumentNo != root.ReturnDocumentNo
            || original.ReturnDocumentLine != root.ReturnDocumentLine
            || original.ReturnCostingRevision != root.ReturnCostingRevision
            || original.ItemCode != returnFact.ItemCode || original.BaseQty != root.ReturnedBaseQty
            || original.CurrentStandardReceiptValue != returnFact.CostAmount
            || original.OriginalCogsReversalValue != root.ReturnedCostAmount
            || original.VarianceAmount != StockLedgerPrecision.Money(
                returnFact.CostAmount - root.ReturnedCostAmount)
            || undone.CompanyCode != original.CompanyCode || undone.BranchCode != original.BranchCode
            || undone.StockPostingId != reversal.StockPostingId
            || undone.ReturnDocumentType != original.ReturnDocumentType
            || undone.ReturnDocumentNo != original.ReturnDocumentNo
            || undone.ReturnDocumentLine != original.ReturnDocumentLine
            || undone.ReturnCostingRevision != reversal.ReturnCostingRevision
            || undone.ItemCode != original.ItemCode || undone.BaseQty != original.BaseQty
            || undone.CurrentStandardReceiptValue != -original.CurrentStandardReceiptValue
            || undone.OriginalCogsReversalValue != -original.OriginalCogsReversalValue
            || undone.VarianceAmount != -original.VarianceAmount
            || await db.SalesReturnStandardCostVariances.AsNoTracking().AnyAsync(v =>
                v.ReversesVarianceId == undone.Id, cancellationToken))
            return false;
        return true;
    }

    private static async Task<bool> HasExactWacSalesReturnLineageAsync(
        AppDbContext db, IReadOnlyList<SalesReturnCostAllocation> allocations,
        SalesReturnCostAllocation activeAllocation,
        StockValuationFact activeSale, StockValuationFact activeReturn,
        CancellationToken cancellationToken, bool requireAllReversed = false)
    {
        var roots = allocations.Where(a => a.ReversesAllocationId is null).ToArray();
        if (roots.Length == 0 || roots.Length > 100
            || roots.Any(a => a.ReturnValuationFactId is null))
            return false;
        var rootIds = roots.Select(a => a.Id).ToArray();
        var reversals = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.ReversesAllocationId != null
                && rootIds.Contains(a.ReversesAllocationId.Value))
            .ToArrayAsync(cancellationToken);
        if (reversals.Any(a => a.ReturnValuationFactId is null)
            || reversals.GroupBy(a => a.ReversesAllocationId).Any(g => g.Count() != 1)
            || allocations.Count != roots.Length + reversals.Length
            || reversals.Any(a => !allocations.Any(x => x.Id == a.Id)))
            return false;
        var reversalIds = reversals.Select(a => a.Id).ToArray();
        if (await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a =>
                a.ReversesAllocationId != null
                && reversalIds.Contains(a.ReversesAllocationId.Value), cancellationToken))
            return false;
        var factIds = roots.Select(a => a.OriginalValuationFactId)
            .Concat(roots.Select(a => a.ReturnValuationFactId!.Value))
            .Concat(reversals.Select(a => a.ReturnValuationFactId!.Value))
            .Distinct().ToArray();
        var facts = await db.StockValuationFacts.AsNoTracking().Include(f => f.StockPosting)
            .Include(f => f.InventoryHistory)
            .Where(f => factIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        if (facts.Count != factIds.Length)
            return false;
        var saleOrigin = activeSale.OriginalValuationFactId ?? activeSale.Id;
        var returnOrigin = activeReturn.OriginalValuationFactId ?? activeReturn.Id;
        var byRoot = reversals.ToDictionary(a => a.ReversesAllocationId!.Value);
        foreach (var root in roots)
        {
            var sale = facts[root.OriginalValuationFactId];
            var returned = facts[root.ReturnValuationFactId!.Value];
            if (root.CompanyCode != activeSale.CompanyCode || root.BranchCode != activeSale.BranchCode
                || root.OriginalOwnerType != activeSale.SourceDocumentType
                || root.OriginalOwnerDocumentNo != activeSale.SourceDocumentNo
                || root.OriginalOwnerDocumentLine != activeSale.SourceDocumentLine
                || root.ReturnDocumentType != "SA_CDN"
                || root.ReturnDocumentNo != activeAllocation.ReturnDocumentNo
                || root.ReturnDocumentLine != activeAllocation.ReturnDocumentLine
                || root.ReturnCostingRevision != activeAllocation.ReturnCostingRevision
                || root.ReturnedBaseQty <= 0m || root.ReturnedBaseQty > sale.BaseQty
                || sale.BaseQty <= 0m || sale.Direction != -1
                || sale.CompanyCode != activeSale.CompanyCode
                || sale.BranchCode != activeSale.BranchCode
                || sale.ItemCode != activeSale.ItemCode
                || sale.LedgerEpochId != activeSale.LedgerEpochId
                || sale.MovementCode != "SALE_OUT"
                || sale.CostMethod != StockCostMethods.MovingAverage
                || sale.ValuationStatus != StockValuationStatuses.Valued
                || sale.BaseCostAmount != sale.CostAmount
                || (sale.OriginalValuationFactId ?? sale.Id) != saleOrigin
                || sale.InventoryHistoryId != activeSale.InventoryHistoryId
                || sale.SourceDocumentType != activeSale.SourceDocumentType
                || sale.SourceDocumentId != activeSale.SourceDocumentId
                || sale.SourceDocumentNo != activeSale.SourceDocumentNo
                || sale.SourceDocumentLine != activeSale.SourceDocumentLine
                || sale.StockPosting?.SealedAtUtc is null
                || sale.StockPosting.CompanyCode != sale.CompanyCode
                || sale.StockPosting.BranchCode != sale.BranchCode
                || sale.StockPosting.LedgerEpochId != sale.LedgerEpochId
                || root.ReturnedCostAmount != StockLedgerPrecision.Money(
                    sale.CostAmount * root.ReturnedBaseQty / sale.BaseQty)
                || returned.CompanyCode != activeReturn.CompanyCode
                || returned.BranchCode != activeReturn.BranchCode
                || returned.ItemCode != activeReturn.ItemCode
                || returned.LedgerEpochId != activeReturn.LedgerEpochId
                || returned.Direction != 1 || returned.MovementCode != "SALE_RETURN_IN"
                || returned.CostMethod != StockCostMethods.MovingAverage
                || returned.ValuationSource != StockValuationSources.OriginalSaleReturn
                || returned.ValuationStatus != StockValuationStatuses.Valued
                || returned.BaseCostAmount != returned.CostAmount
                || !StockReplayWacSalesReturnRepricingWriter.HasSupportedCostEvidence(returned)
                || returned.BaseCurrency != activeReturn.BaseCurrency
                || returned.TransactionCostAmount != activeReturn.TransactionCostAmount
                || (returned.OriginalValuationFactId ?? returned.Id) != returnOrigin
                || returned.InventoryHistoryId != activeReturn.InventoryHistoryId
                || returned.SourceDocumentType != activeReturn.SourceDocumentType
                || returned.SourceDocumentId != activeReturn.SourceDocumentId
                || returned.SourceDocumentNo != activeReturn.SourceDocumentNo
                || returned.SourceDocumentLine != activeReturn.SourceDocumentLine
                || returned.BaseQty != root.ReturnedBaseQty
                || returned.CostAmount != root.ReturnedCostAmount
                || returned.StockPostingId != root.StockPostingId
                || returned.StockPosting?.SealedAtUtc is null
                || returned.StockPosting.CompanyCode != returned.CompanyCode
                || returned.StockPosting.BranchCode != returned.BranchCode
                || returned.StockPosting.LedgerEpochId != returned.LedgerEpochId)
                return false;
            if (!byRoot.TryGetValue(root.Id, out var reversal))
            {
                if (requireAllReversed)
                    return false;
                continue;
            }
            var reversedFact = facts[reversal.ReturnValuationFactId!.Value];
            var returnRollback = reversedFact.InventoryHistory?.EntryRole == "REVERSAL";
            if (returnRollback)
            {
                var originalHistory = returned.InventoryHistory;
                var reversalPosting = reversedFact.StockPosting;
                if (!requireAllReversed || root.Id != activeAllocation.Id
                    || originalHistory is null || originalHistory.StockPostingId is not > 0
                    || reversedFact.InventoryHistory?.ReversesHistoryId != originalHistory.Id
                    || reversedFact.MovementCode != "SALE_RETURN_IN_REVERSAL"
                    || reversalPosting?.PostingRole != "REVERSAL"
                    || reversalPosting.ReversesPostingId != originalHistory.StockPostingId
                    || reversalPosting.SourceModule != "INVENTORY"
                    || reversalPosting.SourceDocumentType != IvTrxTypes.CustomerReturn)
                    return false;
            }
            else if (reversedFact.InventoryHistoryId != returned.InventoryHistoryId
                || reversedFact.MovementCode != "SALE_RETURN_IN"
                || reversedFact.StockPosting?.SourceModule != "INVENTORY"
                || reversedFact.StockPosting.SourceDocumentType is not
                    (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive))
                return false;
            if (reversal.CompanyCode != root.CompanyCode || reversal.BranchCode != root.BranchCode
                || reversal.ReturnDocumentType != root.ReturnDocumentType
                || reversal.ReturnDocumentNo != root.ReturnDocumentNo
                || reversal.ReturnDocumentLine != root.ReturnDocumentLine
                || reversal.ReturnCostingRevision != root.ReturnCostingRevision
                || reversal.OriginalValuationFactId != root.OriginalValuationFactId
                || reversal.OriginalOwnerType != root.OriginalOwnerType
                || reversal.OriginalOwnerDocumentNo != root.OriginalOwnerDocumentNo
                || reversal.OriginalOwnerDocumentLine != root.OriginalOwnerDocumentLine
                || reversal.ReturnedBaseQty != root.ReturnedBaseQty
                || reversal.ReturnedCostAmount != root.ReturnedCostAmount
                || reversedFact.CompanyCode != returned.CompanyCode
                || reversedFact.BranchCode != returned.BranchCode
                || reversedFact.ItemCode != returned.ItemCode
                || reversedFact.LedgerEpochId != returned.LedgerEpochId
                || reversedFact.CostMethod != returned.CostMethod
                || reversedFact.Direction != -1 || reversedFact.BaseQty != returned.BaseQty
                || reversedFact.CostAmount != returned.CostAmount
                || reversedFact.BaseCostAmount != returned.BaseCostAmount
                || reversedFact.BaseCurrency != returned.BaseCurrency
                || reversedFact.TransactionCurrency != returned.TransactionCurrency
                || reversedFact.TransactionCostAmount != returned.TransactionCostAmount
                || reversedFact.ExchangeRate != returned.ExchangeRate
                || reversedFact.ReversesValuationFactId != returned.Id
                || reversedFact.ValuationStatus != StockValuationStatuses.Reversed
                || reversedFact.StockPostingId != reversal.StockPostingId
                || reversedFact.StockPosting?.SealedAtUtc is null
                || reversedFact.StockPosting.CompanyCode != returned.CompanyCode
                || reversedFact.StockPosting.BranchCode != returned.BranchCode
                || reversedFact.StockPosting.LedgerEpochId != returned.LedgerEpochId)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Authorizes one posted stock-return CN only when its invoice, stock
    /// movement, active allocation and method-specific cost evidence match.
    /// </summary>
    private static async Task<(SalesReturnPermit? Permit, string? Error)>
        CheckPostedSalesReturnAsync(
            AppDbContext db, string companyCode, string branchCode, string itemCode,
            IReadOnlyList<IvTrxHistory> histories,
            IReadOnlyDictionary<long, StockReplayEventKey> eventKeys,
            string costMethod,
            CancellationToken cancellationToken)
    {
        var unsupported = "Backdated receipt replay requires one exact posted invoice and stock-return credit note.";
        if (costMethod is not (StockCostMethods.Standard or StockCostMethods.MovingAverage
            or StockCostMethods.Fifo))
            return (null, unsupported);
        var sales = histories.Where(h => h.TrxType == IvTrxTypes.SalesOut).ToArray();
        var returns = histories.Where(h => h.TrxType == IvTrxTypes.CustomerReturn).ToArray();
        if (sales.Length != 1 || returns.Length != 1)
            return (null, unsupported);
        var sale = sales[0];
        var returned = returns[0];
        var invoiceNo = returned.InvNo?.Trim();
        var doNo = sale.DoNo?.Trim();
        var ownerType = string.IsNullOrWhiteSpace(doNo) ? "SA_INVOICE" : "SA_DO";
        var ownerNo = string.IsNullOrWhiteSpace(doNo) ? invoiceNo : doNo;
        var reference = returned.RefNo?.Trim();
        if (string.IsNullOrWhiteSpace(invoiceNo)
            || string.IsNullOrWhiteSpace(ownerNo)
            || ownerType == "SA_INVOICE" && sale.InvNo != invoiceNo
            || ownerType == "SA_DO" && sale.InvNo != doNo
            || sale.SoLineNo is not > 0
            || returned.SoLineNo is not > 0
            || returned.FromBalLocId is not null || returned.ToBalLocId is not > 0
            || returned.ToStdQty is not > 0m || returned.FrStdQty is > 0m
            || returned.ToStdQty > sale.FrStdQty
            || string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith(SaCdnSpRefs.Prefix, StringComparison.Ordinal)
            || reference.Length <= SaCdnSpRefs.Prefix.Length)
            return (null, unsupported + " Return identity is inconsistent.");
        var noteNo = reference[SaCdnSpRefs.Prefix.Length..];
        if (!eventKeys.TryGetValue(sale.Id, out var saleKey)
            || !eventKeys.TryGetValue(returned.Id, out var returnKey)
            || saleKey.CompareTo(returnKey) >= 0)
            return (null, unsupported + " Return chronology is inconsistent.");

        var invoice = await db.SaInvoices.AsNoTracking().Include(i => i.Details)
            .SingleOrDefaultAsync(i => i.CompanyCode == companyCode
                && i.BranchCode == branchCode && i.InvNo == invoiceNo,
                cancellationToken);
        var note = await db.SaCdns.AsNoTracking().Include(c => c.Details)
            .SingleOrDefaultAsync(c => c.CompanyCode == companyCode
                && c.BranchCode == branchCode && c.DocNo == noteNo,
                cancellationToken);
        if (invoice is null || invoice.Status != SaInvoiceStatuses.Posted
            || invoice.DeletedAtUtc is not null || invoice.Details.Count != 1
            || note is null || note.Status != SaCdnStatuses.Posted
            || note.DeletedAtUtc is not null || note.Type != SaCdnTypes.CreditNote
            || !note.ReturnStock || note.InvNo != invoiceNo
            || note.CustCode != invoice.CustCode || note.Currency != invoice.Currency
            || note.DocDate.Date != returned.TrxDtTime.Date
            || note.DocDate.Date < invoice.InvDate.Date
            || note.CostingRevision != returned.DocumentRevision
            || note.Details.Count != 1
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, companyCode,
                branchCode, note.DocDate, cancellationToken) is not null)
            return (null, unsupported + " Invoice or credit-note header is inconsistent.");
        var invoiceLine = invoice.Details.Single();
        var noteLine = note.Details.Single();
        if (!invoiceLine.StockControl || invoiceLine.ICode != itemCode
            || ownerType == "SA_INVOICE"
                && (invoiceLine.LinkDo || invoiceLine.Line != sale.SoLineNo)
            || ownerType == "SA_DO"
                && (!invoiceLine.LinkDo || invoiceLine.DoNo != doNo
                    || invoiceLine.DoLine != sale.SoLineNo)
            || returned.SoLineNo != invoiceLine.Line
            || !noteLine.StockControl || noteLine.Line != returned.TrxLineNo
            || noteLine.SourceInvLine != invoiceLine.Line || noteLine.ICode != itemCode
            || noteLine.StdQty != returned.ToStdQty || noteLine.StdUom != returned.ToStdUom
            || noteLine.FrWarehouse != returned.ToWarehouse
            || (noteLine.LocCode ?? string.Empty) != (returned.ToLocation ?? string.Empty)
            || noteLine.IStatus != returned.IStatus
            || (noteLine.LotNo ?? string.Empty) != (returned.ToLotNo ?? string.Empty))
            return (null, unsupported + " Credit-note line is inconsistent.");

        var batch = await db.IvTrxBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.BatchNo == returned.BatchNo,
                cancellationToken);
        var details = batch is null ? [] : await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id
                && d.DocumentRevision == returned.DocumentRevision)
            .ToArrayAsync(cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.CustomerReturn
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || batch.RefNo != SaCdnSpRefs.ToRefNo(noteNo)
            || batch.TrxDtTime != returned.TrxDtTime
            || !SaCdnCalc.IsValidFingerprint(batch.SourceFingerprint)
            || batch.SourceFingerprint != SaCdnCalc.ComputeSourceFingerprint(note.Details)
            || details.Length != 1 || details[0].TrxLineNo != returned.TrxLineNo
            || details[0].ICode != itemCode || details[0].InvNo != invoiceNo
            || details[0].SoLineNo != invoiceLine.Line
            || details[0].ToBalLocId != returned.ToBalLocId
            || details[0].ToStdQty != returned.ToStdQty
            || details[0].ToStdUom != returned.ToStdUom
            || details[0].ToLotId != returned.ToLotId)
            return (null, unsupported + " Return batch is inconsistent.");

        var facts = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode, [sale, returned], cancellationToken);
        var saleFacts = facts[sale.Id];
        var returnFacts = facts[returned.Id];
        if (saleFacts.Count != 1 || returnFacts.Count != 1)
            return (null, unsupported + " Valuation generation is inconsistent.");
        var saleFact = saleFacts[0];
        var returnFact = returnFacts[0];
        var valuationSource = costMethod switch
        {
            StockCostMethods.Standard => StockValuationSources.Standard,
            StockCostMethods.Fifo => StockValuationSources.Fifo,
            _ => StockValuationSources.MovingAverage
        };
        var returnValuationSource = costMethod == StockCostMethods.Standard
            ? StockValuationSources.Standard : StockValuationSources.OriginalSaleReturn;
        if (costMethod != StockCostMethods.MovingAverage
                && (saleFact.StockPostingId != sale.StockPostingId
                    || saleFact.OriginalValuationFactId is not null)
            || saleFact.SourceDocumentType != ownerType
            || saleFact.SourceDocumentId != ownerNo || saleFact.SourceDocumentNo != ownerNo
            || saleFact.SourceDocumentLine != sale.SoLineNo.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || saleFact.MovementCode != "SALE_OUT" || saleFact.Direction != -1
            || saleFact.CostMethod != costMethod
            || saleFact.ValuationSource != valuationSource
            || saleFact.BaseCostAmount != saleFact.CostAmount
            || costMethod != StockCostMethods.MovingAverage
                && (returnFact.StockPostingId != returned.StockPostingId
                    || returnFact.OriginalValuationFactId is not null)
            || returnFact.SourceDocumentType != IvTrxTypes.CustomerReturn
            || returnFact.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.SourceDocumentNo != batch.BatchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.SourceDocumentLine != returned.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.MovementCode != "SALE_RETURN_IN" || returnFact.Direction != 1
            || returnFact.CostMethod != costMethod
            || returnFact.ValuationSource != returnValuationSource
            || returnFact.BaseCostAmount != returnFact.CostAmount
            || costMethod == StockCostMethods.MovingAverage
                && !MatchesWacReturnDocumentCost(returnFact, returned, noteLine, note.Currency, details[0])
            || returnFact.ToBalLocId != returned.ToBalLocId
            || returnFact.LotId != returned.ToLotId)
            return (null, unsupported + " Valuation owner is inconsistent.");
        var allocations = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == companyCode && a.BranchCode == branchCode
                && (a.ReturnValuationFactId == returnFact.Id
                    || a.OriginalOwnerType == ownerType
                        && a.OriginalOwnerDocumentNo == ownerNo))
            .ToArrayAsync(cancellationToken);
        var roots = allocations.Where(a => a.ReversesAllocationId is null).ToArray();
        var live = roots.Where(root => !allocations.Any(a =>
            a.ReversesAllocationId == root.Id)).ToArray();
        if (live.Length != 1 || costMethod != StockCostMethods.MovingAverage
                && allocations.Length != 1)
            return (null, unsupported + " Return allocation is incomplete.");
        var allocation = live[0];
        var expectedReturnedCost = StockLedgerPrecision.Money(
            saleFact.CostAmount * allocation.ReturnedBaseQty / saleFact.BaseQty);
        if (allocation.ReversesAllocationId is not null
            || allocation.OriginalValuationFactId != saleFact.Id
            || allocation.OriginalOwnerType != ownerType
            || allocation.OriginalOwnerDocumentNo != ownerNo
            || allocation.OriginalOwnerDocumentLine != saleFact.SourceDocumentLine
            || allocation.ReturnValuationFactId != returnFact.Id
            || allocation.StockPostingId != returnFact.StockPostingId
            || allocation.ReturnDocumentType != "SA_CDN"
            || allocation.ReturnDocumentNo != noteNo
            || allocation.ReturnDocumentLine != returned.TrxLineNo
            || allocation.ReturnCostingRevision != note.CostingRevision
            || allocation.ReturnedBaseQty != returned.ToStdQty
            || allocation.ReturnedCostAmount != expectedReturnedCost
            || await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a =>
                a.ReversesAllocationId == allocation.Id, cancellationToken))
            return (null, unsupported + " Return allocation is inconsistent.");
        if (costMethod == StockCostMethods.MovingAverage)
        {
            if (returnFact.CostAmount != allocation.ReturnedCostAmount
                || !await HasExactWacSalesReturnLineageAsync(db, allocations,
                    allocation, saleFact, returnFact, cancellationToken))
                return (null, unsupported + " Return cost lineage is inconsistent.");
            return (new SalesReturnPermit(sale.Id, noteNo, allocation.Id,
                StockCostMethods.MovingAverage), null);
        }
        if (costMethod == StockCostMethods.Fifo)
            return returnFact.CostAmount == allocation.ReturnedCostAmount
                ? (new SalesReturnPermit(sale.Id, noteNo, allocation.Id,
                    StockCostMethods.Fifo), null)
                : (null, unsupported + " Return cost is inconsistent.");
        var variances = await db.SalesReturnStandardCostVariances.AsNoTracking()
            .Where(v => v.CompanyCode == companyCode && v.BranchCode == branchCode
                && v.ReturnValuationFactId == returnFact.Id)
            .ToArrayAsync(cancellationToken);
        if (variances.Length != 1)
            return (null, unsupported + " Return variance is incomplete.");
        var variance = variances[0];
        if (variance.ReversesVarianceId is not null
            || variance.StockPostingId != returned.StockPostingId
            || variance.ReturnDocumentType != "SA_CDN"
            || variance.ReturnDocumentNo != noteNo
            || variance.ReturnDocumentLine != returned.TrxLineNo
            || variance.ReturnCostingRevision != note.CostingRevision
            || variance.ItemCode != itemCode || variance.BaseQty != returned.ToStdQty
            || variance.CurrentStandardReceiptValue != returnFact.CostAmount
            || variance.OriginalCogsReversalValue != allocation.ReturnedCostAmount
            || variance.VarianceAmount != StockLedgerPrecision.Money(
                returnFact.CostAmount - allocation.ReturnedCostAmount)
            || await db.SalesReturnStandardCostVariances.AsNoTracking().AnyAsync(v =>
                v.ReversesVarianceId == variance.Id, cancellationToken))
            return (null, unsupported + " Return variance is inconsistent.");
        return (new SalesReturnPermit(sale.Id, noteNo, allocation.Id), null);
    }

    private static bool MatchesWacReturnDocumentCost(
        StockValuationFact fact, IvTrxHistory returned, SaCdnDetail noteLine, string? documentCurrency,
        IvTrxBatchDetail? detail = null)
    {
        // A stock-return batch is created in base quantity with the note's
        // cost price as its unit-price evidence. Check it explicitly: the
        // stock-slice fingerprint does not contain the cost price.
        if (noteLine.CostPrice < 0m || returned.ToStdQty is not > 0m
            || noteLine.CostPrice > 0m
                && (string.IsNullOrWhiteSpace(fact.BaseCurrency)
                    || !string.Equals(documentCurrency?.Trim(), fact.BaseCurrency.Trim(),
                        StringComparison.OrdinalIgnoreCase))
            || returned.ToPurQty is > 0m || returned.CostPrice is not null
            || returned.UnitPrice != noteLine.CostPrice
            || !StockReplayWacSalesReturnRepricingWriter.HasSupportedCostEvidence(fact)
            || detail is not null && (detail.Currency is not null
                || detail.ToPurQty is > 0m || detail.CostPrice is not null
                || detail.UnitPrice != noteLine.CostPrice))
            return false;
        var expected = StockLedgerPrecision.Money(returned.ToStdQty.Value * noteLine.CostPrice);
        return expected == 0m
            ? fact.TransactionCostAmount is null or 0m
            : fact.TransactionCostAmount == expected;
    }
}
