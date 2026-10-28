using ErpWeb.Core.Sales;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record StandardSalesReturnPermit(
        int SaleHistoryId, string NoteNo, long AllocationId);

    /// <summary>
    /// Authorizes one posted stock-return CN only when its Standard receipt,
    /// original sale allocation and variance are complete and unchanged.
    /// The Standard projection separately proves that replay changes none of them.
    /// </summary>
    private static async Task<(StandardSalesReturnPermit? Permit, string? Error)>
        CheckStandardSalesReturnAsync(
            AppDbContext db, string companyCode, string branchCode, string itemCode,
            IReadOnlyList<IvTrxHistory> histories,
            IReadOnlyList<StockReplayStandardEvent> events,
            CancellationToken cancellationToken)
    {
        const string unsupported = "Backdated Standard receipt replay requires one exact posted invoice and stock-return credit note.";
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
        var saleEvent = events.SingleOrDefault(e => e.EventId == sale.Id);
        var returnEvent = events.SingleOrDefault(e => e.EventId == returned.Id);
        if (saleEvent is null || returnEvent is null
            || saleEvent.Key.CompareTo(returnEvent.Key) >= 0)
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
        if (saleFact.StockPostingId != sale.StockPostingId
            || saleFact.OriginalValuationFactId is not null
            || saleFact.SourceDocumentType != ownerType
            || saleFact.SourceDocumentId != ownerNo || saleFact.SourceDocumentNo != ownerNo
            || saleFact.SourceDocumentLine != sale.SoLineNo.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || saleFact.MovementCode != "SALE_OUT" || saleFact.Direction != -1
            || saleFact.CostMethod != StockCostMethods.Standard
            || saleFact.ValuationSource != StockValuationSources.Standard
            || saleFact.BaseCostAmount != saleFact.CostAmount
            || returnFact.StockPostingId != returned.StockPostingId
            || returnFact.OriginalValuationFactId is not null
            || returnFact.SourceDocumentType != IvTrxTypes.CustomerReturn
            || returnFact.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.SourceDocumentNo != batch.BatchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.SourceDocumentLine != returned.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || returnFact.MovementCode != "SALE_RETURN_IN" || returnFact.Direction != 1
            || returnFact.CostMethod != StockCostMethods.Standard
            || returnFact.ValuationSource != StockValuationSources.Standard
            || returnFact.BaseCostAmount != returnFact.CostAmount
            || returnFact.ToBalLocId != returned.ToBalLocId
            || returnFact.LotId != returned.ToLotId)
            return (null, unsupported + " Valuation owner is inconsistent.");
        var allocations = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == companyCode && a.BranchCode == branchCode
                && (a.ReturnValuationFactId == returnFact.Id
                    || a.OriginalOwnerType == ownerType
                        && a.OriginalOwnerDocumentNo == ownerNo))
            .ToArrayAsync(cancellationToken);
        if (allocations.Length != 1)
            return (null, unsupported + " Return allocation is incomplete.");
        var allocation = allocations[0];
        var expectedReturnedCost = StockLedgerPrecision.Money(
            saleFact.CostAmount * allocation.ReturnedBaseQty / saleFact.BaseQty);
        if (allocation.ReversesAllocationId is not null
            || allocation.OriginalValuationFactId != saleFact.Id
            || allocation.OriginalOwnerType != ownerType
            || allocation.OriginalOwnerDocumentNo != ownerNo
            || allocation.OriginalOwnerDocumentLine != saleFact.SourceDocumentLine
            || allocation.ReturnValuationFactId != returnFact.Id
            || allocation.StockPostingId != returned.StockPostingId
            || allocation.ReturnDocumentType != "SA_CDN"
            || allocation.ReturnDocumentNo != noteNo
            || allocation.ReturnDocumentLine != returned.TrxLineNo
            || allocation.ReturnCostingRevision != note.CostingRevision
            || allocation.ReturnedBaseQty != returned.ToStdQty
            || allocation.ReturnedCostAmount != expectedReturnedCost
            || await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a =>
                a.ReversesAllocationId == allocation.Id, cancellationToken))
            return (null, unsupported + " Return allocation is inconsistent.");
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
        return (new StandardSalesReturnPermit(sale.Id, noteNo, allocation.Id), null);
    }
}
