using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    private sealed record InvoiceIssueRemovalPlan(
        string Fingerprint, long EpochId, long Watermark, string PoNo, short PoRelNo, short PoLineNo,
        long OriginalPostingId, long OriginalFactId, InvoiceIssueReplay IssueReplay);

    // Prove the original invoice and its later issues and receipts before
    // removing the invoice value at its own business position.
    private async Task<InvoiceIssueRemovalPlan?> PlanInvoiceIssueRemovalAsync(
        AppDbContext db, PoInvoice invoice, CancellationToken ct)
    {
        if (invoice.Type != PoInvoiceTypes.Invoice || invoice.Status != PoInvoiceStatuses.Posted
            || invoice.DeletedAtUtc is not null || invoice.Details.Count != 1)
            return null;
        var line = invoice.Details.Single();
        if (string.IsNullOrWhiteSpace(line.ICode) || string.IsNullOrWhiteSpace(line.PoNo)
            || line.PoRelNo is null || line.PoLineNo is null)
            return null;
        var item = line.ICode.Trim();
        var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.SourceModule == "PROCUREMENT"
            && p.SourceDocumentType == "PO_INVOICE" && p.SourceDocumentId == invoice.DocNo
            && p.DocumentRevision == invoice.CostingRevision && p.PostingRole == "PRIMARY"
            && p.SealedAtUtc != null).ToArrayAsync(ct);
        if (roots.Length != 1 || roots[0].CommandType != "PROCUREMENT_PO_INVOICE_PRIMARY"
            || roots[0].EffectiveAt != invoice.DocDate || roots[0].ReversesPostingId is not null
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == roots[0].Id
                && p.SealedAtUtc != null, ct))
            return null;
        var root = roots[0];
        var snapshot = new
        {
            invoice.DocNo, invoice.Type, invoice.DocDate, invoice.Currency, invoice.CurrRate, invoice.CostingRevision,
            Lines = invoice.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReferencedInvoiceLineNo
            }).ToArray()
        };
        if (StockPostingFingerprint.Create(new { Kind = "INVOICE_ROLLBACK_SOURCE" }, snapshot).SourceSnapshotHash
                != root.SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(root.SourceSnapshotJson)) != root.SourceSnapshotHash)
            return null;
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
            a.CompanyCode == invoice.CompanyCode && a.BranchCode == invoice.BranchCode
            && a.SourceDocumentType == "PO_INVOICE" && a.SourceDocumentNo == invoice.DocNo
            && a.SourceCostingRevision == invoice.CostingRevision).ToArrayAsync(ct);
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            s.CompanyCode == invoice.CompanyCode && s.BranchCode == invoice.BranchCode
            && s.PiDocNo == invoice.DocNo && s.PiCostingRevision == invoice.CostingRevision).ToArrayAsync(ct);
        if (adjustments.Length != 1 || settlements.Length != 1
            || adjustments[0].ReversesAdjustmentId is not null || adjustments[0].StockPostingId != root.Id
            || adjustments[0].InventoryAdjustmentAmount == 0m || adjustments[0].InventoryAdjustmentFactId is not long factId
            || settlements[0].ReversesSettlementId is not null || settlements[0].StockPostingId != root.Id
            || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a => a.ReversesAdjustmentId == adjustments[0].Id, ct)
            || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.ReversesSettlementId == settlements[0].Id, ct))
            return null;
        var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, adjustments, ct);
        if (generation is null || generation.OriginalFact.Id != factId || generation.ActiveFact.Id != factId
            || generation.Original.ItemCode != item || generation.Original.PoNo != line.PoNo
            || generation.Original.PoRelNo != line.PoRelNo || generation.Original.PoLineNo != line.PoLineNo
            || settlements[0].ReceiptValuationFactId != generation.OriginalFact.OriginalValuationFactId
            || settlements[0].SettledBaseQty != line.StdQty
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == factId, ct))
            return null;
        var epochs = await db.StockLedgerEpochs.AsNoTracking().Where(e => e.CompanyCode == invoice.CompanyCode
            && e.BranchCode == invoice.BranchCode && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2)
            .ToArrayAsync(ct);
        if (epochs.Length != 1 || epochs[0].Id != root.LedgerEpochId
            || await _costMethods.ResolveAsync(db, invoice.CompanyCode, invoice.BranchCode,
                root.EffectiveAt, ct) != StockCostMethods.MovingAverage)
            return null;
        var watermark = await db.StockPostingBranchSequences.AsNoTracking().Where(s =>
            s.CompanyCode == invoice.CompanyCode && s.BranchCode == invoice.BranchCode)
            .Select(s => (long?)s.LastSequence).SingleOrDefaultAsync(ct) ?? 0;
        var latest = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.SealedAtUtc != null)
            .MaxAsync(p => (long?)p.PostingSequence, ct) ?? 0;
        if (watermark < latest || watermark == long.MaxValue)
            return null;
        var state = await db.StockCostStates.AsNoTracking().Where(s => s.CompanyCode == invoice.CompanyCode
            && s.BranchCode == invoice.BranchCode && s.ItemCode == item).ToArrayAsync(ct);
        if (state.Length != 1 || state[0].CostMethod != StockCostMethods.MovingAverage)
            return null;
        var balances = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == invoice.CompanyCode
            && b.BranchCode == invoice.BranchCode && b.ICode == item)
            .Select(b => new { b.Id, b.StdQty, b.RowVersion }).ToArrayAsync(ct);
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo)
            .Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo
            && p.Line == line.PoLineNo).Select(p => new { p.ICode, p.PurchaseUom, p.RecvQty, p.ReturnQty, p.InvoicedQty })
            .SingleOrDefaultAsync(ct);
        if (balances.Length != 1 || order is null || orderLine is null)
            return null;
        var key = new StockReplayEventKey(root.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            root.PostingSequence, $"V{factId:D20}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db,
            invoice.CompanyCode, invoice.BranchCode, item, key, ct);
        if (tail.LaterEvents.Count is 0 or > StockReplayInvoiceIssueAudit.MaxIssueCount || tail.LaterEvents.Any(e =>
                e.Key.EffectiveAt.Year != root.EffectiveAt.Year || e.Key.EffectiveAt.Month != root.EffectiveAt.Month))
            return null;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= root.EffectiveAt.Date && (p.EffectiveTo == null || p.EffectiveTo > root.EffectiveAt.Date))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveFrom, p.EffectiveTo, p.Status }).ToArrayAsync(ct);
        var lastDate = tail.LaterEvents.Max(e => e.Key.EffectiveAt.Date);
        if (policies.Length != 1 || policies[0].EffectiveTo is DateTime end && end.Date <= lastDate
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p => p.CompanyCode == invoice.CompanyCode
                && p.BranchCode == invoice.BranchCode && p.Status == StockCostPolicyStatuses.Active
                && p.Id != policies[0].Id && p.EffectiveFrom <= lastDate
                && (p.EffectiveTo == null || p.EffectiveTo > root.EffectiveAt.Date), ct))
            return null;
        var removed = new StockReplayWacEvent(long.MaxValue - factId, key, 0m,
            generation.ActiveFact.SignedValue, 0m, generation.ActiveFact.SignedValue);
        var projection = StockReplayWacSimulator.SimulateRemoval(tail.CurrentQuantity, tail.CurrentValue, removed, tail.LaterEvents);
        // Recompute the complete old tail before authorizing any replacement.
        var oldTail = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
            removed with { OldSignedValue = 0m, FixedSignedValue = 0m }, tail.LaterEvents);
        if (oldTail.FinalQuantity != tail.CurrentQuantity || oldTail.FinalValue != tail.CurrentValue
            || oldTail.LaterSteps.Any(step => !tail.LaterEvents.Any(e => e.EventId == step.EventId
                && e.OldSignedQuantity == step.SignedQuantity && e.OldSignedValue == step.SignedValue)))
            return null;
        var proof = await PlanInvoiceIssueChangesAsync(db, invoice.CompanyCode, invoice.BranchCode,
            item, root.LedgerEpochId, balances[0].Id, invoice.DocNo, line.PoNo, line.PoRelNo.Value,
            line.PoLineNo.Value, PoOrderCalc.RoundQty(line.Qty), invoicePosted: true,
            tail.LaterEvents, projection.LaterSteps, ct);
        if (proof is null)
            return null;
        var replay = new InvoiceIssueReplay(proof.Changes, proof.EvidenceHash, proof.GoodsReceipts, proof.Sales,
            -generation.Active.InventoryAdjustmentAmount, -generation.Active.ConsumedVarianceAmount,
            balances[0].Id, balances[0].StdQty, tail.CurrentQuantity, tail.CurrentValue,
            projection.FinalQuantity, projection.FinalValue);
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "PI_ISSUE_REMOVAL" }, new
        {
            invoice.DocNo, invoice.DocDate, invoice.RowVersion, invoice.CostingRevision, snapshot,
            Root = new { root.Id, root.PostingSequence, root.RequestFingerprint, root.SourceSnapshotHash, root.LedgerEpochId },
            generation.Fingerprint, settlement = settlements[0].Id, policies, order, orderLine, balances, watermark,
            State = new { state[0].Id, state[0].OnHandBaseQty, state[0].InventoryValue,
                state[0].LastValuationFactId, state[0].LastPostingSequence, state[0].RowVersion },
            tail.LaterEvents, projection, replay
        }).SourceSnapshotHash;
        return new(fingerprint, root.LedgerEpochId, watermark, line.PoNo.Trim(), line.PoRelNo.Value,
            line.PoLineNo.Value, root.Id, factId, replay);
    }
}
