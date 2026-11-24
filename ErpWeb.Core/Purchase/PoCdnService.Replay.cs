using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoCdnService
{
    private sealed record CdnIssueReplayPlan(
        string Fingerprint, long EpochId, long Watermark, string InvoiceNo,
        string PoNo, short PoRelNo, short PoLineNo, long? OriginalPostingId, long? OriginalFactId,
        PurchaseCostAdjustmentRequest InitialAdjustment,
        PurchaseCostAdjustmentRequest EffectiveAdjustment,
        PoInvoiceService.InvoiceIssueReplay Replay,
        IReadOnlyList<string> LaterNoteDocNos);

    private sealed record LaterPurchaseNoteProof(
        string DocNo, int CostingRevision, byte[] RowVersion,
        long AdjustmentId, long FactId, decimal InventoryAdjustmentAmount);

    private async Task<CdnIssueReplayPlan?> PreviewCdnIssueReplayAsync(
        UserContext write, string docNo, bool posting, CancellationToken ct)
    {
        if (!_openPeriodBackdatedReplayEnabled || _branchStockLock is null || _cdnCostAdjustments is null
            || _stockCoordinator is null)
            return null;
        try
        {
            await using var preview = await _dbFactory.CreateDbContextAsync(ct);
            var cdn = await preview.PoCdns.AsNoTracking().Include(c => c.Details).SingleOrDefaultAsync(c =>
                c.CompanyCode == write.CompanyCode && c.BranchCode == write.BranchCode && c.DocNo == docNo, ct);
            if (cdn is null || string.IsNullOrWhiteSpace(cdn.InvNo))
                return null;
            var invoice = await preview.PoInvoices.AsNoTracking().Include(i => i.Details).SingleOrDefaultAsync(i =>
                i.CompanyCode == write.CompanyCode && i.BranchCode == write.BranchCode && i.DocNo == cdn.InvNo, ct);
            return invoice is null ? null : await PlanCdnIssueReplayAsync(preview, cdn, invoice, posting, ct);
        }
        catch (StockLedgerException) { return null; }
        catch (ArgumentException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<bool> LockCdnReplaySourcesAsync(
        AppDbContext db, UserContext write, string docNo, CdnIssueReplayPlan plan, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer() || db.Database.CurrentTransaction is null)
            return false;
        // Other purchase paths may hold a document lock before requesting the
        // branch lock. Do not wait on those locks after acquiring the branch.
        var invoices = await db.PoInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.POInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {plan.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var invoiceLines = await db.PoInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.POInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {plan.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var notes = await db.PoCdns.FromSqlInterpolated($@"SELECT * FROM dbo.PoCdn WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {docNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var lines = await db.PoCdnDetails.FromSqlInterpolated($@"SELECT * FROM dbo.PoCdnDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {docNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var orders = await db.PoOrders.FromSqlInterpolated($@"SELECT * FROM dbo.POOrder WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode}
                AND PONo = {plan.PoNo} AND PORelNo = {plan.PoRelNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var orderLines = await db.PoOrderDetails.FromSqlInterpolated($@"SELECT * FROM dbo.PODetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode}
                AND PONo = {plan.PoNo} AND PORelNo = {plan.PoRelNo} AND Line = {plan.PoLineNo}")
            .AsNoTracking().ToArrayAsync(ct);
        if (invoices.Length != 1 || invoiceLines.Length == 0 || notes.Length != 1
            || lines.Length != 1 || orders.Length != 1 || orderLines.Length != 1)
            return false;
        foreach (var laterDocNo in plan.LaterNoteDocNos.OrderBy(x => x, StringComparer.Ordinal))
        {
            var laterNotes = await db.PoCdns.FromSqlInterpolated($@"SELECT * FROM dbo.PoCdn WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {laterDocNo}")
                .AsNoTracking().ToArrayAsync(ct);
            if (laterNotes.Length != 1)
                return false;
        }
        return await PoInvoiceService.LockInvoiceLaterGoodsReceiptOrdersAsync(
                db, write.CompanyCode!, write.BranchCode!, plan.Replay.GoodsReceipts, ct)
            && await PoInvoiceService.LockInvoiceDirectSalesAsync(
                db, write.CompanyCode!, write.BranchCode!, plan.Replay.Sales, ct)
            && await PoInvoiceService.LockInvoiceDeliverySalesAsync(
                db, write.CompanyCode!, write.BranchCode!, plan.Replay.Deliveries, ct);
    }

    // Financial CN/DN replay supports ordinary issues, scrap, receipts, sales (direct invoices
    // and unbilled/partially/fully billed DOs), and unchanged later financial purchase notes on one stock pile.
    // Other dependencies continue through the existing safety guard.
    private async Task<CdnIssueReplayPlan?> PlanCdnIssueReplayAsync(
        AppDbContext db, PoCdn cdn, PoInvoice invoice, bool posting, CancellationToken ct)
    {
        if (_cdnCostAdjustments is null || cdn.ReturnStock || cdn.DeletedAtUtc is not null
            || cdn.Status != (posting ? PoCdnStatuses.New : PoCdnStatuses.Posted)
            || cdn.Details.Count != 1 || invoice.Status != PoInvoiceStatuses.Posted
            || invoice.Type != PoInvoiceTypes.Invoice || invoice.DeletedAtUtc is not null
            || invoice.DocNo != cdn.InvNo || invoice.DocDate > cdn.DocDate)
            return null;
        var line = cdn.Details.Single();
        if (string.IsNullOrWhiteSpace(line.ICode) || line.InvLineNo is not > 0
            || line.IsStockReturn || line.StdQty <= 0m || line.NetAmount <= 0m
            || invoice.Details.Count(d => d.Line == line.InvLineNo) != 1)
            return null;
        var company = cdn.CompanyCode;
        var branch = cdn.BranchCode;
        var item = line.ICode.Trim();
        if (posting && !await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company
                && h.BranchCode == branch && h.ICode == item
                && (h.TrxType == IvTrxTypes.MiscellaneousIssue
                    || h.TrxType == IvTrxTypes.Scrap
                    || h.TrxType == IvTrxTypes.SalesOut)
                && h.EntryRole == "ORIGINAL" && h.TrxDtTime > cdn.DocDate, ct))
            return null;
        var invoiceLine = invoice.Details.Single(d => d.Line == line.InvLineNo);
        if (invoiceLine.ICode != item || invoiceLine.PoNo is null || invoiceLine.PoRelNo is null
            || invoiceLine.PoLineNo is null)
            return null;
        var epochs = await db.StockLedgerEpochs.AsNoTracking().Where(e => e.CompanyCode == company
            && e.BranchCode == branch && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2).ToArrayAsync(ct);
        if (epochs.Length != 1 || epochs[0].EffectiveFrom.Date > cdn.DocDate.Date
            || await _costMethods.ResolveAsync(db, company, branch, invoice.DocDate, ct) != StockCostMethods.MovingAverage
            || await _costMethods.ResolveAsync(db, company, branch, cdn.DocDate, ct) != StockCostMethods.MovingAverage)
            return null;
        var watermark = await db.StockPostingBranchSequences.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch).Select(s => (long?)s.LastSequence).SingleOrDefaultAsync(ct) ?? 0;
        var latest = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SealedAtUtc != null).MaxAsync(p => (long?)p.PostingSequence, ct) ?? 0;
        if (watermark < latest || watermark == long.MaxValue)
            return null;
        var sourceSnapshot = new
        {
            cdn.DocNo, cdn.Type, cdn.DocDate, cdn.Currency, cdn.CurrRate, cdn.CostingRevision,
            cdn.InvNo, cdn.ReturnStock, cdn.ReasonCode,
            Lines = cdn.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.InvLineNo, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.IsStockReturn
            }).ToArray(),
            ReferencedInvoiceRevision = invoice.CostingRevision
        };
        StockPosting? original = null;
        PurchaseCostAdjustment? priorAdjustment = null;
        StockValuationFact? originalFact = null;
        StockValuationFact? priorFact = null;
        PurchaseReceiptSettlementResult result;
        if (posting)
        {
            result = await _cdnCostAdjustments.BuildAsync(db, cdn, invoice, StockCostMethods.MovingAverage, ct);
        }
        else
        {
            var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
                && p.BranchCode == branch && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_CDN"
                && p.SourceDocumentId == cdn.DocNo && p.DocumentRevision == cdn.CostingRevision
                && p.PostingRole == "PRIMARY" && p.SealedAtUtc != null).ToArrayAsync(ct);
            if (roots.Length != 1 || roots[0].CommandType != "PROCUREMENT_PO_CDN_PRIMARY"
                || roots[0].LedgerEpochId != epochs[0].Id || roots[0].EffectiveAt != cdn.DocDate
                || roots[0].ReversesPostingId is not null
                || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == roots[0].Id, ct)
                || StockPostingFingerprint.Create(new { Kind = "CDN_REPLAY_SOURCE" }, sourceSnapshot).SourceSnapshotHash
                    != roots[0].SourceSnapshotHash
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(roots[0].SourceSnapshotJson))
                    != roots[0].SourceSnapshotHash)
                return null;
            original = roots[0];
            var allAdjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
                a.CompanyCode == company && a.BranchCode == branch && a.SourceDocumentType == "PO_CDN"
                && a.SourceDocumentNo == cdn.DocNo && a.SourceCostingRevision == cdn.CostingRevision)
                .ToArrayAsync(ct);
            var rootAdjustments = allAdjustments.Where(a => a.StockPostingId == original.Id && a.ReversesAdjustmentId == null).ToArray();
            if (rootAdjustments.Length != 1)
                return null;
            var childGroups = allAdjustments.Where(a => a.ReversesAdjustmentId.HasValue)
                .GroupBy(a => a.ReversesAdjustmentId!.Value).ToArray();
            if (childGroups.Any(g => g.Count() != 1))
                return null;
            var childMap = childGroups.ToDictionary(g => g.Key, g => g.Single());
            var chain = new List<PurchaseCostAdjustment> { rootAdjustments[0] };
            while (childMap.TryGetValue(chain[^1].Id, out var child))
            {
                if (chain.Count >= 101 || chain.Any(a => a.Id == child.Id))
                    return null;
                chain.Add(child);
            }
            var chainIds = chain.Select(a => a.Id).ToArray();
            if (chain.Count != allAdjustments.Length || chain.Count % 2 == 0
                || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a =>
                    a.ReversesAdjustmentId.HasValue && chainIds.Contains(a.ReversesAdjustmentId.Value)
                    && !chainIds.Contains(a.Id), ct))
                return null;
            var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, chain, ct);
            if (generation is null || generation.Original.ItemCode != item
                || generation.Active.InventoryAdjustmentAmount == 0m
                || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == generation.ActiveFact.Id, ct))
                return null;
            priorAdjustment = generation.Active;
            originalFact = generation.OriginalFact;
            priorFact = generation.ActiveFact;
            result = await _cdnCostAdjustments.ReverseAsync(db, cdn, original.Id, "REPLAY_PLAN", ct);
        }
        if (!result.Succeeded || result.Adjustments.Count != 1)
            return null;
        var initialAdjustment = result.Adjustments.Single();
        var value = initialAdjustment.ValueAdjustment;
        if (initialAdjustment.SourceDocumentType != "PO_CDN" || initialAdjustment.SourceDocumentNo != cdn.DocNo
            || initialAdjustment.SourceCostingRevision != cdn.CostingRevision || initialAdjustment.SourceDocumentLine != line.Line
            || initialAdjustment.ItemCode != item || initialAdjustment.CostMethod != StockCostMethods.MovingAverage
            || initialAdjustment.BaseQty != line.StdQty || initialAdjustment.TotalAdjustmentAmount == 0m
            || StockLedgerPrecision.Money(initialAdjustment.InventoryAdjustmentAmount + initialAdjustment.ConsumedVarianceAmount)
                != initialAdjustment.TotalAdjustmentAmount
            || initialAdjustment.PoNo != invoiceLine.PoNo || initialAdjustment.PoRelNo != invoiceLine.PoRelNo
            || initialAdjustment.PoLineNo != invoiceLine.PoLineNo
            || (value is not null && (value.Amount != Math.Abs(initialAdjustment.InventoryAdjustmentAmount)
                || value.Direction * value.Amount != initialAdjustment.InventoryAdjustmentAmount
                || (posting ? value.OriginalValuationFactId is not > 0 || value.ReversesValuationFactId is not null
                    : value.OriginalValuationFactId != priorFact!.Id || value.ReversesValuationFactId != priorFact.Id)))
            || (value is null && initialAdjustment.InventoryAdjustmentAmount != 0m)
            || (posting ? initialAdjustment.ReversesAdjustmentId is not null
                : initialAdjustment.ReversesAdjustmentId != priorAdjustment!.Id))
            return null;
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.PiDocNo == invoice.DocNo
            && s.PiCostingRevision == invoice.CostingRevision && s.PiLineNo == line.InvLineNo
            && s.ReversesSettlementId == null).ToArrayAsync(ct);
        if (settlements.Length != 1 || settlements[0].ItemCode != item || settlements[0].SettledBaseQty < line.StdQty
            || settlements[0].SettledBaseQty != invoiceLine.StdQty
            || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.ReversesSettlementId == settlements[0].Id, ct))
            return null;
        var receiptId = settlements[0].ReceiptValuationFactId;
        if (posting && value is not null && value.OriginalValuationFactId != receiptId
            || !posting && originalFact!.OriginalValuationFactId != receiptId)
            return null;
        var receipt = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == receiptId, ct);
        if (receipt is null || receipt.CompanyCode != company || receipt.BranchCode != branch
            || receipt.LedgerEpochId != epochs[0].Id || receipt.ItemCode != item || receipt.Direction != 1
            || receipt.InventoryHistoryId is null || receipt.CostMethod != StockCostMethods.MovingAverage
            || receipt.ValuationStatus != StockValuationStatuses.Valued || receipt.ValuationSource != StockValuationSources.ReceiptActual
            || receipt.EffectiveAt > cdn.DocDate || receipt.BaseQty != settlements[0].SettledBaseQty
            || receipt.ReversesValuationFactId is not null || receipt.OriginalValuationFactId is not null
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == receipt.Id, ct)
            || !await db.StockPostings.AsNoTracking().AnyAsync(p => p.Id == receipt.StockPostingId
                && p.CompanyCode == company && p.BranchCode == branch && p.LedgerEpochId == epochs[0].Id
                && p.SourceModule == "INVENTORY" && p.SourceDocumentType == IvTrxTypes.GoodsReceive
                && p.CommandType == "IV_POST_GR" && p.PostingRole == "PRIMARY" && p.ReversesPostingId == null
                && p.EffectiveAt == receipt.EffectiveAt && p.SealedAtUtc != null, ct)
            || !await db.StockPostings.AsNoTracking().AnyAsync(p => p.Id == settlements[0].StockPostingId
                && p.CompanyCode == company && p.BranchCode == branch && p.LedgerEpochId == epochs[0].Id
                && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_INVOICE"
                && p.SourceDocumentId == invoice.DocNo && p.DocumentRevision == invoice.CostingRevision
                && p.CommandType == "PROCUREMENT_PO_INVOICE_PRIMARY" && p.PostingRole == "PRIMARY"
                && p.ReversesPostingId == null && p.EffectiveAt == invoice.DocDate && p.SealedAtUtc != null, ct)
            || !await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.Id == receipt.InventoryHistoryId
                && h.CompanyCode == company && h.BranchCode == branch && h.StockPostingId == receipt.StockPostingId
                && h.LedgerVersion == 2 && h.LedgerEpochId == epochs[0].Id && h.ICode == item && h.TrxType == IvTrxTypes.GoodsReceive
                && h.EntryRole == "ORIGINAL" && h.BatchStatus == IvBatchStatuses.Posted
                && h.TrxDtTime == receipt.EffectiveAt && h.ToStdQty == receipt.BaseQty && h.ToStdUom == receipt.BaseUom
                && h.PoNo == invoiceLine.PoNo && h.PoRelNo == invoiceLine.PoRelNo && h.PoLineNo == invoiceLine.PoLineNo, ct))
            return null;
        var receiptOwner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == receipt.StockPostingId, ct);
        var invoiceOwner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == settlements[0].StockPostingId, ct);
        var receiptHistory = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == receipt.InventoryHistoryId, ct);
        var invoiceSnapshot = new
        {
            invoice.DocNo, invoice.Type, invoice.DocDate, invoice.Currency, invoice.CurrRate, invoice.CostingRevision,
            Lines = invoice.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReferencedInvoiceLineNo
            }).ToArray()
        };
        if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(receiptOwner.SourceSnapshotJson)) != receiptOwner.SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(invoiceOwner.SourceSnapshotJson)) != invoiceOwner.SourceSnapshotHash
            || StockPostingFingerprint.Create(new { Kind = "CDN_INVOICE_SOURCE" }, invoiceSnapshot).SourceSnapshotHash != invoiceOwner.SourceSnapshotHash
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == invoiceOwner.Id, ct)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId == receiptHistory.Id, ct)
            || receiptHistory.BaseUnitPrices is not decimal commercialUnit || commercialUnit < 0m
            || settlements[0].ReceiptInventoryHistoryId != receiptHistory.Id || settlements[0].ReceiptBatchNo != receiptHistory.BatchNo
            || settlements[0].ReceiptCommercialUnitCost != StockLedgerPrecision.Money(commercialUnit)
            || settlements[0].ReceiptCommercialBaseAmount != StockLedgerPrecision.Money(receipt.BaseQty * commercialUnit)
            || settlements[0].ReceiptValuationUnitCost != receipt.UnitCost || settlements[0].ReceiptValuationBaseAmount != receipt.CostAmount
            || settlements[0].AllocatedActualBaseAmount != StockLedgerPrecision.Money(invoiceLine.NetAmount * invoice.CurrRate)
            || settlements[0].CommercialVarianceAmount != StockLedgerPrecision.Money(
                settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptCommercialBaseAmount)
            || settlements[0].ValuationVarianceAmount != StockLedgerPrecision.Money(
                settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptValuationBaseAmount))
            return null;
        var states = await db.StockCostStates.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch && s.ItemCode == item).ToArrayAsync(ct);
        var balances = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == item).ToArrayAsync(ct);
        if (states.Length != 1 || states[0].CostMethod != StockCostMethods.MovingAverage || balances.Length != 1
            || balances[0].StdQty != states[0].OnHandBaseQty)
            return null;
        var key = new StockReplayEventKey(cdn.DocDate, StockReplayEventKey.OrdinaryClass,
            posting ? checked(watermark + 1) : original!.PostingSequence,
            posting ? "PURCHASE_CDN" : $"V{originalFact!.Id:D20}", 0);
        if (await PurchaseReplayChronologyGuard.HasLaterCanceledSaleAsync(db, company, branch, epochs[0].Id,
                key.EffectiveAt, key.OriginalPostingSequence, [item], ct))
            return null;
        var tail = posting
            ? await StockReplayWacFinancialTailReader.ReadLaterForCdnAsync(db, company, branch, item, key, ct)
            : await StockReplayWacFinancialTailReader.ReadLaterForCdnRemovalAsync(db, company, branch, item, key, originalFact!.Id, ct);
        if (tail.LaterEvents.Count is 0 or > StockReplayInvoiceIssueAudit.MaxIssueCount
            || tail.CurrentQuantity != states[0].OnHandBaseQty || tail.CurrentValue != states[0].InventoryValue
            || tail.LaterEvents.Any(e => e.Key.EffectiveAt.Year != cdn.DocDate.Year
                || e.Key.EffectiveAt.Month != cdn.DocDate.Month))
            return null;
        var physicalEvents = tail.LaterEvents.Where(e => e.EventId is > 0 and <= int.MaxValue).ToArray();
        var laterValueEvents = tail.LaterEvents.Where(e => e.EventId > int.MaxValue && e.EventId < long.MaxValue).ToArray();
        if (physicalEvents.Length == 0
            || physicalEvents.Length + laterValueEvents.Length != tail.LaterEvents.Count
            || laterValueEvents.Any(e => e.OldSignedQuantity != 0m || e.FixedSignedValue != e.OldSignedValue
                || e.OldSignedValue == 0m))
            return null;
        var historyIds = physicalEvents.Select(e => (int)e.EventId).ToArray();
        if (await db.IvTrxHistories.AsNoTracking().CountAsync(h => historyIds.Contains(h.Id)
                && h.CompanyCode == company && h.BranchCode == branch && h.ICode == item
                && (h.TrxType == IvTrxTypes.MiscellaneousIssue
                    || h.TrxType == IvTrxTypes.Scrap
                    || h.TrxType == IvTrxTypes.MiscellaneousReceipt
                    || h.TrxType == IvTrxTypes.GoodsReceive
                    || h.TrxType == IvTrxTypes.SalesOut), ct) != historyIds.Length)
            return null;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= cdn.DocDate.Date && (p.EffectiveTo == null || p.EffectiveTo > cdn.DocDate.Date))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveFrom, p.EffectiveTo, p.Status }).ToArrayAsync(ct);
        var lastDate = tail.LaterEvents.Max(e => e.Key.EffectiveAt).Date;
        if (policies.Length != 1 || policies[0].EffectiveTo is DateTime end && end.Date <= lastDate
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p => p.CompanyCode == company
                && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active && p.Id != policies[0].Id
                && p.EffectiveFrom <= lastDate && (p.EffectiveTo == null || p.EffectiveTo > cdn.DocDate.Date), ct))
            return null;
        var candidate = new StockReplayWacEvent(posting ? long.MaxValue : long.MaxValue - originalFact!.Id,
            key, 0m, posting ? 0m : priorFact!.SignedValue, 0m, posting ? 0m : priorFact!.SignedValue);
        var old = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
            candidate with { OldSignedValue = 0m, FixedSignedValue = 0m }, tail.LaterEvents);
        if (old.FinalQuantity != tail.CurrentQuantity || old.FinalValue != tail.CurrentValue
            || old.HistoricalQuantityBeforeCandidate <= 0m
            || old.LaterSteps.Any(step => !tail.LaterEvents.Any(e => e.EventId == step.EventId
                && e.OldSignedQuantity == step.SignedQuantity && e.OldSignedValue == step.SignedValue)))
            return null;
        PurchaseCostAdjustmentRequest effectiveAdjustment;
        IReadOnlyList<StockReplayWacStep> projectedSteps;
        decimal finalQuantity, finalValue;
        if (posting)
        {
            var split = StockPurchaseVarianceAllocator.Allocate(
                initialAdjustment.TotalAdjustmentAmount,
                initialAdjustment.BaseQty,
                Math.Min(old.HistoricalQuantityBeforeCandidate, initialAdjustment.BaseQty),
                old.HistoricalValueBeforeCandidate,
                StockCostMethods.MovingAverage);
            if (split.Inventory == 0m)
                return null;
            var positive = split.Inventory > 0m;
            var effectiveValue = new StockValueAdjustmentRequest(
                initialAdjustment.SourceDocumentLine,
                0,
                item,
                receipt.BaseUom,
                Math.Abs(split.Inventory),
                positive ? 1 : -1,
                positive ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT",
                StockValuationSources.PurchasePriceVariance,
                SourceDocumentLine: initialAdjustment.SourceDocumentLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OriginalValuationFactId: receipt.Id);
            effectiveAdjustment = initialAdjustment with
            {
                InventoryAdjustmentAmount = split.Inventory,
                ConsumedVarianceAmount = split.Consumed,
                ValueAdjustment = effectiveValue
            };
            var projected = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
                candidate with { FixedSignedValue = split.Inventory }, tail.LaterEvents);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        else
        {
            var reversalInventory = StockLedgerPrecision.Money(-priorAdjustment!.InventoryAdjustmentAmount);
            var reversalConsumed = StockLedgerPrecision.Money(-priorAdjustment.ConsumedVarianceAmount);
            var reversalTotal = StockLedgerPrecision.Money(-priorAdjustment.TotalAdjustmentAmount);
            var positive = reversalInventory > 0m;
            var effectiveValue = new StockValueAdjustmentRequest(
                priorAdjustment.SourceDocumentLine,
                0,
                item,
                priorFact!.BaseUom,
                Math.Abs(reversalInventory),
                positive ? 1 : -1,
                positive ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT",
                StockValuationSources.PurchasePriceVariance,
                SourceDocumentLine: priorAdjustment.SourceDocumentLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OriginalValuationFactId: priorFact.Id,
                ReversesValuationFactId: priorFact.Id);
            effectiveAdjustment = initialAdjustment with
            {
                TotalAdjustmentAmount = reversalTotal,
                InventoryAdjustmentAmount = reversalInventory,
                ConsumedVarianceAmount = reversalConsumed,
                ValueAdjustment = effectiveValue,
                ReversesAdjustmentId = priorAdjustment.Id
            };
            var projected = StockReplayWacSimulator.SimulateRemoval(tail.CurrentQuantity, tail.CurrentValue, candidate, tail.LaterEvents);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        var projectedStepsById = projectedSteps.ToDictionary(s => s.EventId);
        var laterNotes = await ProveLaterUnchangedPurchaseNotesAsync(db, company, branch, item,
            epochs[0].Id, cdn.DocNo, laterValueEvents, projectedStepsById, ct);
        if (laterNotes is null)
            return null;
        var physicalSteps = physicalEvents.Select(e => projectedStepsById[e.EventId]).ToArray();
        var proof = await PoInvoiceService.PlanInvoiceIssueChangesAsync(db, company, branch, item,
            epochs[0].Id, balances[0].Id, invoice.DocNo, invoiceLine.PoNo, invoiceLine.PoRelNo.Value,
            invoiceLine.PoLineNo.Value, invoiceLine.Qty, invoicePosted: true,
            physicalEvents, physicalSteps, ct);
        if (proof is null)
            return null;
        var replay = new PoInvoiceService.InvoiceIssueReplay(proof.Changes, proof.EvidenceHash,
            proof.GoodsReceipts, proof.Sales, proof.Deliveries, effectiveAdjustment.InventoryAdjustmentAmount,
            effectiveAdjustment.ConsumedVarianceAmount, balances[0].Id, balances[0].StdQty,
            tail.CurrentQuantity, tail.CurrentValue, finalQuantity, finalValue);
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == invoiceLine.PoNo && p.PoRelNo == invoiceLine.PoRelNo)
            .Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == invoiceLine.PoNo && p.PoRelNo == invoiceLine.PoRelNo && p.Line == invoiceLine.PoLineNo)
            .Select(p => new { p.ICode, p.PurchaseUom, p.RecvQty, p.ReturnQty, p.InvoicedQty }).SingleOrDefaultAsync(ct);
        if (order is null || orderLine is null || orderLine.ICode != item)
            return null;
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "PO_CDN_WAC_ISSUE_REPLAY" }, new
        {
            posting, sourceSnapshot, cdn.RowVersion, Invoice = new { invoice.DocNo, invoice.DocDate, invoice.Status,
                invoice.CostingRevision, invoice.RowVersion, invoiceLine.Line, invoiceLine.ICode, invoiceLine.StdQty,
                invoiceLine.NetAmount, invoiceLine.PoNo, invoiceLine.PoRelNo, invoiceLine.PoLineNo },
            Epoch = epochs[0].Id, watermark, Settlement = new { settlements[0].Id, settlements[0].StockPostingId,
                settlements[0].ReceiptValuationFactId, settlements[0].SettledBaseQty,
                settlements[0].ReceiptCommercialBaseAmount, settlements[0].AllocatedActualBaseAmount },
            Receipt = new { receipt.Id, receipt.StockPostingId, receipt.CostAmount, receipt.UnitCost, receipt.EffectiveAt },
            ReceiptOwner = new { receiptOwner.Id, receiptOwner.SourceSnapshotHash, receiptOwner.RequestFingerprint },
            InvoiceOwner = new { invoiceOwner.Id, invoiceOwner.SourceSnapshotHash, invoiceOwner.RequestFingerprint },
            Original = original is null ? null : new { original.Id, original.PostingSequence,
                original.SourceSnapshotHash, original.RequestFingerprint },
            OriginalFact = originalFact?.Id, PriorAdjustment = priorAdjustment?.Id, PriorFact = priorFact?.Id, policies, order, orderLine,
            Balance = new { balances[0].Id, balances[0].StdQty, balances[0].RowVersion },
            State = new { states[0].Id, states[0].OnHandBaseQty, states[0].InventoryValue,
                states[0].LastValuationFactId, states[0].LastPostingSequence, states[0].RowVersion },
            LaterNotes = laterNotes,
            initialAdjustment, effectiveAdjustment, tail.LaterEvents, projectedSteps, finalQuantity, finalValue, replay
        }).SourceSnapshotHash;
        return new(fingerprint, epochs[0].Id, watermark, invoice.DocNo, invoiceLine.PoNo,
            invoiceLine.PoRelNo.Value, invoiceLine.PoLineNo.Value, original?.Id, priorFact?.Id,
            initialAdjustment, effectiveAdjustment, replay,
            laterNotes.Select(n => n.DocNo).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static async Task<IReadOnlyList<LaterPurchaseNoteProof>?> ProveLaterUnchangedPurchaseNotesAsync(
        AppDbContext db, string company, string branch, string item, long epochId,
        string currentCdnDocNo, IReadOnlyList<StockReplayWacEvent> laterValueEvents,
        IReadOnlyDictionary<long, StockReplayWacStep> projectedStepsById, CancellationToken ct)
    {
        if (laterValueEvents.Count == 0)
            return [];
        var proofs = new List<LaterPurchaseNoteProof>(laterValueEvents.Count);
        foreach (var noteEvent in laterValueEvents.OrderBy(e => e.Key))
        {
            var originalFactId = long.MaxValue - noteEvent.EventId;
            if (!projectedStepsById.TryGetValue(noteEvent.EventId, out var step)
                || step.SignedQuantity != 0m || step.SignedValue != noteEvent.OldSignedValue)
                return null;
            var origFact = await db.StockValuationFacts.AsNoTracking()
                .SingleOrDefaultAsync(f => f.Id == originalFactId && f.CompanyCode == company
                    && f.BranchCode == branch && f.LedgerEpochId == epochId && f.ItemCode == item
                    && f.SourceDocumentType == "PO_CDN" && f.BaseQty == 0m
                    && f.CostMethod == StockCostMethods.MovingAverage
                    && f.ValuationSource == StockValuationSources.PurchasePriceVariance, ct);
            if (origFact is null || origFact.SourceDocumentNo == currentCdnDocNo)
                return null;
            var noteHeader = await db.PoCdns.AsNoTracking()
                .Where(c => c.CompanyCode == company && c.BranchCode == branch
                    && c.DocNo == origFact.SourceDocumentNo && c.Status == PoCdnStatuses.Posted
                    && !c.ReturnStock && c.DeletedAtUtc == null)
                .Select(c => new { c.DocNo, c.CostingRevision, c.RowVersion })
                .SingleOrDefaultAsync(ct);
            if (noteHeader is null)
                return null;
            var allAdjustments = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(a => a.CompanyCode == company && a.BranchCode == branch
                    && a.SourceDocumentType == "PO_CDN"
                    && a.SourceDocumentNo == noteHeader.DocNo
                    && a.SourceCostingRevision == noteHeader.CostingRevision)
                .ToArrayAsync(ct);
            var rootAdjustments = allAdjustments.Where(a => a.ReversesAdjustmentId == null).ToArray();
            if (rootAdjustments.Length != 1)
                return null;
            var childGroups = allAdjustments.Where(a => a.ReversesAdjustmentId.HasValue)
                .GroupBy(a => a.ReversesAdjustmentId!.Value).ToArray();
            if (childGroups.Any(g => g.Count() != 1))
                return null;
            var childMap = childGroups.ToDictionary(g => g.Key, g => g.Single());
            var chain = new List<PurchaseCostAdjustment> { rootAdjustments[0] };
            while (childMap.TryGetValue(chain[^1].Id, out var child))
            {
                if (chain.Count >= 101 || chain.Any(a => a.Id == child.Id))
                    return null;
                chain.Add(child);
            }
            if (chain.Count != allAdjustments.Length || chain.Count % 2 == 0)
                return null;
            var generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, chain, ct);
            if (generation is null || generation.OriginalFact.Id != originalFactId
                || generation.Active.ItemCode != item
                || generation.Active.InventoryAdjustmentAmount != noteEvent.OldSignedValue
                || generation.ActiveFact.SignedValue != noteEvent.OldSignedValue)
                return null;
            // Prove that the later note's historical inventory-vs-consumed split does NOT change
            // (in particular, does not clip at zero under the replayed pool value before that note).
            var replayedSplit = StockPurchaseVarianceAllocator.Allocate(
                generation.Active.TotalAdjustmentAmount,
                generation.Active.BaseQty,
                Math.Min(step.QuantityBefore, generation.Active.BaseQty),
                step.ValueBefore,
                StockCostMethods.MovingAverage);
            if (replayedSplit.Inventory != noteEvent.OldSignedValue)
                return null;
            proofs.Add(new LaterPurchaseNoteProof(
                noteHeader.DocNo, noteHeader.CostingRevision, noteHeader.RowVersion,
                generation.Active.Id, generation.ActiveFact.Id, generation.Active.InventoryAdjustmentAmount));
        }
        return proofs;
    }
}
