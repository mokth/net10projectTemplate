using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record SameItemWacRemovalPlan(
        StockReplayWacRemovalPlan Anchor,
        StockReplayWacRemovalPlan Combined,
        IReadOnlyDictionary<int, StockReplayWacEvent> SourceEvents);

    private sealed record NeutralReversedSaleCandidate(
        string ItemCode, int BalLocId, long EpochId, bool Removal,
        decimal PhysicalCurrent, StockReplayPhysicalEvent PhysicalAnchor,
        IReadOnlyList<StockReplayPhysicalEvent> PhysicalLater, decimal PhysicalFinal,
        decimal WacCurrentQty, decimal WacCurrentValue, StockReplayWacEvent WacAnchor,
        IReadOnlyList<StockReplayWacEvent> WacLater, IReadOnlyList<StockReplayWacStep> WacSteps,
        decimal WacFinalQty, decimal WacFinalValue);

    private sealed record ReversedSaleProof(
        string Fingerprint, int SaleHistoryId, int ReversalHistoryId,
        long SaleFactId, long ReversalFactId, decimal Quantity,
        decimal OriginalCost, decimal ProjectedCost);

    internal sealed record ReversedSaleCycleState(
        string Fingerprint, long SaleFactId, long ReversalFactId,
        long? SaleDeltaId, long? ReversalDeltaId, long? ActiveRootId,
        int ReceiptBalanceId, decimal ReceiptQuantity, decimal ReceiptUnitCost, decimal ReceiptValue,
        decimal EffectiveSaleCost, long[] ValueIds);

    private static async Task<ReversedSaleCycleState?> ReadReversedSaleCycleAsync(
        AppDbContext db, string company, string branch, IvTrxBatch receipt, string item, CancellationToken ct)
    {
        var values = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.ItemCode == item
            && f.EffectiveAt >= receipt.TrxDtTime && f.StockPosting!.SealedAtUtc != null
            && f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue).ToArrayAsync(ct);
        if (values.Length == 0)
            return null;
        var rootIds = values.Select(v => v.StockPostingId).Distinct().ToArray();
        if (!await StockValuationSnapshotBuilder.ValidateReversedSaleCorrectionsAsync(db, company, branch, rootIds, ct))
            return null;
        var roots = await db.StockPostings.AsNoTracking().Where(p => rootIds.Contains(p.Id)).ToArrayAsync(ct);
        var documentId = receipt.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (roots.Length != rootIds.Length || roots.Any(r => r.SourceDocumentId != documentId
            || r.SourceDocumentNo != receipt.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || r.EffectiveAt != receipt.TrxDtTime))
            return null;
        // The replay tail omits retired receipts. Every retired generation of
        // this batch must belong to the validated correction cycle before it
        // can be ignored while simulating the canceled sale.
        var receiptHistories = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && h.BatchNo == receipt.BatchNo
            && h.TrxType == IvTrxTypes.MiscellaneousReceipt).ToArrayAsync(ct);
        var receiptOriginals = receiptHistories.Where(h => h.EntryRole == "ORIGINAL").ToArray();
        var receiptReversals = receiptHistories.Where(h => h.EntryRole == "REVERSAL").ToArray();
        if (receiptOriginals.Length + receiptReversals.Length != receiptHistories.Length
            || receiptReversals.Length != roots.Length
            || receiptOriginals.Count(h => !receiptReversals.Any(r => r.ReversesHistoryId == h.Id))
                != (receipt.BatchStatus == IvBatchStatuses.Posted ? 1 : 0))
            return null;
        foreach (var reversal in receiptReversals)
        {
            var original = receiptOriginals.SingleOrDefault(h => h.Id == reversal.ReversesHistoryId);
            var reversalRoot = roots.SingleOrDefault(r => r.Id == reversal.StockPostingId);
            if (original is null || reversalRoot is null || reversalRoot.ReversesPostingId != original.StockPostingId
                || receiptReversals.Count(r => r.ReversesHistoryId == original.Id) != 1
                || original.ICode != item || reversal.ICode != item
                || reversal.FromBalLocId != original.ToBalLocId || reversal.FrStdQty != original.ToStdQty
                || reversal.FrStdUom != original.ToStdUom || reversal.FromLotId != original.ToLotId
                || reversal.FrLotNo != original.ToLotNo || reversal.IStatus != original.IStatus)
                return null;
        }
        var sourceIds = values.Select(v => v.OriginalValuationFactId!.Value).Distinct().ToArray();
        var sources = await db.StockValuationFacts.AsNoTracking().Where(f => sourceIds.Contains(f.Id)).ToArrayAsync(ct);
        var sales = sources.Where(f => f.MovementCode == "SALE_OUT").ToArray();
        var reversals = sources.Where(f => f.MovementCode == "SALE_OUT_REVERSAL").ToArray();
        if (sources.Length != 2 || sales.Length != 1 || reversals.Length != 1)
            return null;
        var ids = values.Select(v => v.Id).ToArray();
        var children = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.ReversesValuationFactId.HasValue && ids.Contains(f.ReversesValuationFactId.Value)).ToArrayAsync(ct);
        var related = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.OriginalValuationFactId.HasValue && sourceIds.Contains(f.OriginalValuationFactId.Value)
            && f.Id != reversals[0].Id).Select(f => f.Id).ToArrayAsync(ct);
        if (!related.Order().SequenceEqual(values.Select(v => v.Id).Concat(children.Select(v => v.Id)).Order()))
            return null;
        var sourceEffects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
            && (sourceIds.Contains(e.AffectedEntityId)
                || e.OriginalEntityId.HasValue && sourceIds.Contains(e.OriginalEntityId.Value))).ToArrayAsync(ct);
        var allowedIds = values.Select(v => v.Id).Concat(children.Select(v => v.Id)).ToHashSet();
        if (sourceEffects.Any(e => !allowedIds.Contains(e.AffectedEntityId)
            || e.EffectKind is not (StockReplayEffectTypes.SalesReversalValue or StockReplayEffectTypes.SalesReversalValueUndo)))
            return null;
        var reversed = children.Select(c => c.ReversesValuationFactId!.Value).ToHashSet();
        var active = values.Where(v => !reversed.Contains(v.Id)).ToArray();
        if (active.Length is not (0 or 2) || active.Length == 2 && active[0].StockPostingId != active[1].StockPostingId)
            return null;
        var saleDelta = active.SingleOrDefault(v => v.OriginalValuationFactId == sales[0].Id);
        var reverseDelta = active.SingleOrDefault(v => v.OriginalValuationFactId == reversals[0].Id);
        if (active.Length == 2 && (saleDelta is null || reverseDelta is null))
            return null;
        var latestRoot = roots.MaxBy(r => r.PostingSequence)!;
        var receiptHistory = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.StockPostingId == latestRoot.ReversesPostingId && h.EntryRole == "ORIGINAL", ct);
        if (receiptHistory is null || receiptHistory.ToBalLocId is not int balanceId
            || receiptHistory.ToStdQty is not > 0m)
            return null;
        var receiptFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.InventoryHistoryId == receiptHistory.Id).ToArrayAsync(ct);
        if (receiptFacts.Length != 1 || receiptFacts[0].SignedQty != receiptHistory.ToStdQty
            || receiptFacts[0].ItemCode != item || receiptFacts[0].ToBalLocId != balanceId)
            return null;
        var cost = sales[0].CostAmount - (saleDelta?.SignedValue ?? 0m);
        if (cost < 0m || reversals[0].CostAmount + (reverseDelta?.SignedValue ?? 0m) != cost)
            return null;
        var fingerprint = StockPostingFingerprint.Create(new { receipt.Id, item }, new
        {
            roots = roots.OrderBy(r => r.PostingSequence).Select(r => new { r.Id, r.PostingSequence }).ToArray(),
            values = values.OrderBy(v => v.Id).Select(v => new { v.Id, v.StockPostingId, v.OriginalValuationFactId, v.Direction, v.CostAmount }).ToArray(),
            children = children.OrderBy(v => v.Id).Select(v => new { v.Id, v.StockPostingId, v.ReversesValuationFactId }).ToArray(),
            receiptHistory.Id, balanceId, receiptHistory.ToStdQty, receiptFacts[0].UnitCost, receiptFacts[0].CostAmount, cost
        }).SourceSnapshotHash;
        return new(fingerprint, sales[0].Id, reversals[0].Id, saleDelta?.Id, reverseDelta?.Id,
            saleDelta?.StockPostingId, balanceId, receiptHistory.ToStdQty.Value, receiptFacts[0].UnitCost, receiptFacts[0].CostAmount, cost,
            values.Select(v => v.Id).Concat(children.Select(v => v.Id)).Order().ToArray());
    }

    private static Task<bool> HasReversedSalesAfterAsync(
        AppDbContext db, string company, string branch, string[] items, DateTime receiptAt, CancellationToken ct) =>
        db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company && h.BranchCode == branch
            && h.TrxType == IvTrxTypes.SalesOut && h.ICode != null && items.Contains(h.ICode)
            && h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL"
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id
                && (h.TrxDtTime >= receiptAt || r.TrxDtTime >= receiptAt)), ct);

    private static Task<bool> HasUnresolvedReversedSaleValueAsync(
        AppDbContext db, string company, string branch, string[] items, DateTime receiptAt, CancellationToken ct,
        bool rollback = false) =>
        InventoryValuationService.HasBlockingReversedSaleCorrectionsAsync(db, company, branch, items,
            receiptAt, rollback ? 0 : long.MaxValue, ct);

    // The ordinary active tail omits an original/reversal pair. Admit an
    // isolated reversed direct sale only when replaying both physical legs
    // proves availability and the projected values reconcile.
    private static async Task<ReversedSaleProof?> ProveNeutralReversedSaleAsync(
        AppDbContext db, string company, string branch, IvTrxBatch receipt,
        NeutralReversedSaleCandidate plan, CancellationToken ct, bool lockSalesEvidence = false,
        bool allowCostCorrection = false, ReversedSaleCycleState? cycle = null)
    {
        var hasVerifiedCycle = cycle is not null;
        var issues = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && h.ICode == plan.ItemCode
            && h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL"
            && (!hasVerifiedCycle || h.BatchNo != receipt.BatchNo || h.TrxType != receipt.TrxType)
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id
                && (h.TrxDtTime >= receipt.TrxDtTime || r.TrxDtTime >= receipt.TrxDtTime))).ToArrayAsync(ct);
        if (issues.Length != 1)
            return null;
        var sale = issues[0];
        if (sale.TrxType != IvTrxTypes.SalesOut || sale.FromBalLocId != plan.BalLocId
            || sale.ToBalLocId is not null || sale.FrStdQty is not > 0m
            || sale.TrxDtTime <= receipt.TrxDtTime || sale.TrxDtTime.Year != receipt.TrxDtTime.Year
            || sale.TrxDtTime.Month != receipt.TrxDtTime.Month
            || sale.LedgerEpochId != plan.EpochId || sale.StockPostingId is not > 0
            || sale.SoLineNo is not > 0 || !string.IsNullOrWhiteSpace(sale.DoNo)
            || string.IsNullOrWhiteSpace(sale.InvNo))
            return null;
        var businessDate = (await StockBusinessTime.NowAsync(db, company, ct)).Date;
        var reversals = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.ReversesHistoryId == sale.Id).ToArrayAsync(ct);
        if (reversals.Length != 1 || reversals[0].StockPostingId is not > 0)
            return null;
        var reverse = reversals[0];
        if (reverse.InvNo != sale.InvNo || reverse.DoNo != sale.DoNo || reverse.SoLineNo != sale.SoLineNo)
            return null;
        // The active WAC tail omits reversed value adjustments. Their temporary
        // value can affect the sale between its issue and reversal, so do not
        // treat the pair as neutral while any such adjustment overlaps it.
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == company && f.BranchCode == branch && f.ItemCode == plan.ItemCode
                && f.InventoryHistoryId == null && f.BaseQty == 0m && f.ReversesValuationFactId == null
                && (!hasVerifiedCycle || f.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValue)
                && f.EffectiveAt <= reverse.TrxDtTime
                && db.StockValuationFacts.Any(r => r.ReversesValuationFactId == f.Id
                    && r.EffectiveAt >= receipt.TrxDtTime), ct))
            return null;
        var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == sale.StockPostingId, ct);
        var reverseOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == reverse.StockPostingId, ct);
        var saleBatch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == sale.BatchNo, ct);
        if (owner is null || reverseOwner is null || saleBatch is null
            || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.SalesOut
            || owner.CommandType != $"IV_POST_{IvTrxTypes.SalesOut}"
            || reverseOwner.CommandType != $"IV_ROLLBACK_{IvTrxTypes.SalesOut}"
            || owner.SourceDocumentId != saleBatch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != sale.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || saleBatch.TrxType != IvTrxTypes.SalesOut || saleBatch.BatchStatus != IvBatchStatuses.New
            || saleBatch.RefNo != sale.InvNo || reverse.TrxDtTime.Year != receipt.TrxDtTime.Year
            || reverse.TrxDtTime.Month != receipt.TrxDtTime.Month
            || sale.TrxDtTime.Date > businessDate || reverse.TrxDtTime.Date > businessDate
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, sale.TrxDtTime, ct) is not null
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, reverse.TrxDtTime, ct) is not null)
            return null;
        var saleKey = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            owner.PostingSequence, $"H{sale.Id:D10}", 0);
        var reverseKey = new StockReplayEventKey(reverseOwner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            reverseOwner.PostingSequence, $"H{reverse.Id:D10}", 0);
        if (saleKey.CompareTo(plan.PhysicalAnchor.Key) <= 0 || reverseKey.CompareTo(saleKey) <= 0)
            return null;
        SaInvoice? invoice;
        SaInvoiceDetail[] invoiceLines;
        bool hasNotes;
        if (lockSalesEvidence && db.Database.IsSqlServer())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Sales evidence must be locked inside the replay transaction.");
            try
            {
                // Sales posting locks its invoice before the branch. Do not wait here while
                // holding the branch lock; a busy invoice means the receipt must be retried.
                invoice = await db.SaInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {sale.InvNo}")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                invoiceLines = await db.SaInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {sale.InvNo}")
                    .AsNoTracking().ToArrayAsync(ct);
                var notes = await db.SaCdns.FromSqlInterpolated($@"SELECT * FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {sale.InvNo}")
                    .AsNoTracking().ToArrayAsync(ct);
                hasNotes = notes.Length != 0;
            }
            catch (SqlException error) when (error.Number == 1222)
            {
                return null;
            }
        }
        else
        {
            invoice = await db.SaInvoices.AsNoTracking().SingleOrDefaultAsync(i =>
                i.CompanyCode == company && i.BranchCode == branch && i.InvNo == sale.InvNo, ct);
            invoiceLines = await db.SaInvoiceDetails.AsNoTracking().Where(l =>
                l.CompanyCode == company && l.BranchCode == branch && l.InvNo == sale.InvNo).ToArrayAsync(ct);
            hasNotes = await db.SaCdns.AsNoTracking().AnyAsync(c => c.CompanyCode == company
                && c.BranchCode == branch && c.InvNo == sale.InvNo, ct);
        }
        if (invoice is null || invoice.Status != SaInvoiceStatuses.New || invoice.DeletedAtUtc is not null
            || invoice.RollbackDate is null || invoice.InvDate.Date != sale.TrxDtTime.Date
            || invoiceLines.Length != 1 || invoiceLines[0] is not { } line
            || !line.StockControl || line.LinkDo || line.Line != sale.SoLineNo || line.ICode != sale.ICode
            || line.StdQty != sale.FrStdQty || line.StdUom != sale.FrStdUom
            || hasNotes)
            return null;
        var saleFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.InventoryHistoryId == sale.Id).ToArrayAsync(ct);
        var reverseFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.InventoryHistoryId == reverse.Id).ToArrayAsync(ct);
        if (saleFacts.Length != 1 || reverseFacts.Length != 1)
            return null;
        var fact = saleFacts[0];
        var inverse = reverseFacts[0];
        if (cycle is not null && (cycle.SaleFactId != fact.Id || cycle.ReversalFactId != inverse.Id
            || cycle.ReceiptBalanceId != plan.BalLocId))
            return null;
        var lineText = sale.SoLineNo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (fact.CompanyCode != company || fact.BranchCode != branch || fact.ItemCode != plan.ItemCode
            || fact.LedgerEpochId != plan.EpochId || fact.StockPostingId != owner.Id
            || fact.SourceDocumentType != "SA_INVOICE" || fact.SourceDocumentId != sale.InvNo
            || fact.SourceDocumentNo != sale.InvNo || fact.SourceDocumentLine != lineText
            || fact.EffectiveAt != owner.EffectiveAt || fact.BusinessDate != owner.BusinessDate || fact.PeriodKey != owner.PeriodKey
            || fact.MovementCode != "SALE_OUT" || fact.Direction != -1 || fact.BaseQty != sale.FrStdQty
            || fact.FromBalLocId != plan.BalLocId || fact.CostMethod != StockCostMethods.MovingAverage
            || fact.BaseUom != sale.FrStdUom || fact.ItemStatus != sale.IStatus
            || fact.LotId != sale.FromLotId || (fact.LotNo ?? string.Empty) != (sale.FrLotNo ?? string.Empty)
            || fact.PostingLineNo != sale.PostingLineNo
            || fact.ValuationStatus != StockValuationStatuses.Valued
            || fact.ValuationSource != StockValuationSources.MovingAverage
            || fact.CostAmount < 0m || fact.BaseCostAmount != fact.CostAmount
            || fact.OriginalValuationFactId is not null || fact.ReversesValuationFactId is not null
            || inverse.CompanyCode != company || inverse.BranchCode != branch || inverse.ItemCode != fact.ItemCode
            || inverse.LedgerEpochId != fact.LedgerEpochId || inverse.StockPostingId != reverseOwner.Id
            || inverse.SourceDocumentType != fact.SourceDocumentType || inverse.SourceDocumentId != fact.SourceDocumentId
            || inverse.SourceDocumentNo != fact.SourceDocumentNo || inverse.SourceDocumentLine != fact.SourceDocumentLine
            || inverse.EffectiveAt != reverseOwner.EffectiveAt || inverse.BusinessDate != reverseOwner.BusinessDate
            || inverse.PeriodKey != reverseOwner.PeriodKey || inverse.PostingLineNo != reverse.PostingLineNo
            || inverse.MovementCode != "SALE_OUT_REVERSAL" || inverse.Direction != 1
            || inverse.ToBalLocId != plan.BalLocId || inverse.BaseQty != fact.BaseQty
            || inverse.CostMethod != fact.CostMethod || inverse.UnitCost != fact.UnitCost
            || inverse.CostAmount != fact.CostAmount || inverse.BaseCostAmount != fact.BaseCostAmount
            || inverse.BaseUom != fact.BaseUom || inverse.BaseCurrency != fact.BaseCurrency
            || inverse.TransactionCurrency != fact.TransactionCurrency
            || inverse.TransactionCostAmount != fact.TransactionCostAmount || inverse.ExchangeRate != fact.ExchangeRate
            || inverse.ValuationStatus != StockValuationStatuses.Reversed
            || inverse.ValuationSource != StockValuationSources.OriginalReversal
            || inverse.OriginalValuationFactId != fact.Id || inverse.ReversesValuationFactId != fact.Id
            || await db.StockValuationFacts.AsNoTracking().CountAsync(f => f.ReversesValuationFactId == fact.Id, ct) != 1
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.OriginalValuationFactId == fact.Id && f.Id != inverse.Id
                && (!hasVerifiedCycle || f.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValue
                    && f.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValueUndo), ct)
            || await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a => a.OriginalValuationFactId == fact.Id
                || a.CompanyCode == company && a.BranchCode == branch && a.OriginalOwnerType == "SA_INVOICE"
                    && a.OriginalOwnerDocumentNo == sale.InvNo, ct)
            || await db.StockReplayEffects.AsNoTracking().AnyAsync(e =>
                e.AffectedEntityType == StockReplayEffectTypes.InventoryHistory
                    && (e.AffectedEntityId == sale.Id || e.AffectedEntityId == reverse.Id
                        || e.OriginalEntityId == sale.Id || e.OriginalEntityId == reverse.Id)
                || e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                    && (e.AffectedEntityId == fact.Id || e.AffectedEntityId == inverse.Id
                        || e.OriginalEntityId == fact.Id || e.OriginalEntityId == inverse.Id)
                    && (!hasVerifiedCycle || e.EffectKind != StockReplayEffectTypes.SalesReversalValue
                        && e.EffectKind != StockReplayEffectTypes.SalesReversalValueUndo), ct))
            return null;

        try
        {
            // This reader verifies the exact sealed business-owner and physical
            // reversal pair, including a reversal that crosses the sale key.
            await StockReplayPhysicalAsOfReader.ReadAtKeyAsync(db, company, branch,
                [plan.BalLocId], saleKey, ct);
            var physical = plan.PhysicalLater.Concat([
                new StockReplayPhysicalEvent(sale.Id, saleKey, -fact.BaseQty),
                new StockReplayPhysicalEvent(reverse.Id, reverseKey, fact.BaseQty)]).ToArray();
            var physicalFinal = plan.Removal
                ? StockReplayPhysicalSimulator.SimulateRemoval(plan.PhysicalCurrent, plan.PhysicalAnchor, physical).FinalQuantity
                : StockReplayPhysicalSimulator.Simulate(plan.PhysicalCurrent, plan.PhysicalAnchor, physical).FinalQuantity;
            var currentSaleCost = cycle?.EffectiveSaleCost ?? fact.CostAmount;
            var financial = plan.WacLater.Concat([
                new StockReplayWacEvent(sale.Id, saleKey, -fact.BaseQty, -currentSaleCost, -fact.BaseQty),
                // This projection uses the simulator's exact paired-issue rule;
                // it does not add a Production movement to the replay plan.
                new StockReplayWacEvent(reverse.Id, reverseKey, fact.BaseQty, currentSaleCost, fact.BaseQty,
                    currentSaleCost, ReversedProductionIssueEventId: sale.Id)]).ToArray();
            IReadOnlyList<StockReplayWacStep> steps;
            decimal finalQty, finalValue;
            if (plan.Removal)
            {
                var projected = StockReplayWacSimulator.SimulateRemoval(plan.WacCurrentQty, plan.WacCurrentValue,
                    plan.WacAnchor, financial);
                steps = projected.LaterSteps; finalQty = projected.FinalQuantity; finalValue = projected.FinalValue;
            }
            else
            {
                var projected = StockReplayWacSimulator.SimulateInsertion(plan.WacCurrentQty, plan.WacCurrentValue,
                    plan.WacAnchor, financial);
                steps = projected.LaterSteps; finalQty = projected.FinalQuantity; finalValue = projected.FinalValue;
            }
            var projectedSaleCost = -steps.Single(s => s.EventId == sale.Id).SignedValue;
            if (cycle?.ActiveRootId is not null
                && (plan.Removal || plan.PhysicalLater.Count != 0 || plan.WacLater.Count != 0
                    || projectedSaleCost != fact.CostAmount))
                return null;
            if (projectedSaleCost != fact.CostAmount)
            {
                // The paired sale correction currently supports a single receipt
                // removal with no other active descendants or foreign currency.
                if (!allowCostCorrection || !plan.Removal || plan.PhysicalLater.Count != 0
                    || plan.WacLater.Count != 0 || projectedSaleCost < 0m
                    || fact.TransactionCurrency is not null || fact.TransactionCostAmount is not null
                    || fact.ExchangeRate is not null)
                    return null;
            }
            if (physicalFinal != plan.PhysicalFinal || finalQty != plan.WacFinalQty || finalValue != plan.WacFinalValue
                || steps.Single(s => s.EventId == sale.Id).SignedValue != -projectedSaleCost
                || steps.Single(s => s.EventId == reverse.Id).SignedValue != projectedSaleCost
                || plan.WacSteps.Any(expected => steps.Count(s => s.EventId == expected.EventId
                    && s.SignedQuantity == expected.SignedQuantity && s.SignedValue == expected.SignedValue) != 1))
                return null;
            var fingerprint = StockPostingFingerprint.Create(new { ReceiptId = receipt.Id, SaleId = sale.Id, ReverseId = reverse.Id, plan.Removal }, new
            {
                SaleFact = fact.Id, ReverseFact = inverse.Id, fact.BaseQty, fact.CostAmount,
                Owner = owner.Id, owner.PostingSequence, owner.EffectiveAt,
                owner.SealedAtUtc, OwnerRequest = owner.RequestFingerprint, OwnerSnapshot = owner.SourceSnapshotHash,
                ReverseOwner = reverseOwner.Id, ReverseSequence = reverseOwner.PostingSequence, ReverseAt = reverseOwner.EffectiveAt,
                ReverseSeal = reverseOwner.SealedAtUtc, ReverseRequest = reverseOwner.RequestFingerprint,
                ReverseSnapshot = reverseOwner.SourceSnapshotHash,
                invoice.InvNo, invoice.Status, invoice.RollbackDate, RowVersion = Convert.ToHexString(invoice.RowVersion ?? []),
                InvoiceLine = line.Line, line.ICode, line.StdQty, line.StdUom, line.StockControl, line.LinkDo,
                sale.FromBalLocId, sale.FrStdQty, sale.FrStdUom, sale.IStatus, sale.FromLotId, sale.FrLotNo,
                fact.BaseCurrency, fact.TransactionCurrency, fact.TransactionCostAmount, fact.ExchangeRate,
                plan.PhysicalCurrent, plan.PhysicalFinal, plan.WacCurrentQty, plan.WacCurrentValue,
                plan.WacFinalQty, plan.WacFinalValue, projectedSaleCost, Cycle = cycle?.Fingerprint
            }).SourceSnapshotHash;
            return new(fingerprint, sale.Id, reverse.Id, fact.Id, inverse.Id,
                fact.BaseQty, currentSaleCost, projectedSaleCost);
        }
        catch (Exception error) when (error is StockLedgerException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static NeutralReversedSaleCandidate NeutralSaleCandidate(StockReplayWacInsertionPlan plan) =>
        new(plan.ItemCode, plan.PhysicalPlan.BalLocId, plan.PhysicalPlan.LedgerEpochId, false,
            plan.PhysicalPlan.CurrentQuantity, plan.PhysicalPlan.Candidate, plan.PhysicalPlan.LaterEvents,
            plan.PhysicalPlan.Projection.FinalQuantity, plan.Tail.CurrentQuantity, plan.Tail.CurrentValue,
            plan.Candidate, plan.Tail.LaterEvents, plan.Projection.LaterSteps,
            plan.Projection.FinalQuantity, plan.Projection.FinalValue);

    private static async Task AppendReversedSaleUndoAsync(
        StockPostingContext context, ReversedSaleCycleState cycle, ReversedSaleProof proof,
        StockReplayWacInsertionPlan plan, CancellationToken ct)
    {
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt || context.Posting.PostingRole != "PRIMARY"
            || cycle.SaleDeltaId is not long saleDeltaId || cycle.ReversalDeltaId is not long reverseDeltaId
            || cycle.ActiveRootId is not > 0 || cycle.ReceiptBalanceId != plan.PhysicalPlan.BalLocId
            || cycle.ReceiptQuantity != plan.Candidate.ReplaySignedQuantity || cycle.ReceiptValue != plan.Candidate.FixedSignedValue
            || cycle.EffectiveSaleCost != proof.OriginalCost || proof.SaleFactId != cycle.SaleFactId
            || proof.ReversalFactId != cycle.ReversalFactId || proof.OriginalCost == proof.ProjectedCost
            || plan.Tail.LaterEvents.Count != 0 || plan.PhysicalPlan.LaterEvents.Count != 0)
            throw MultiWacError("The reversed sale undo has invalid replay authority.");
        var db = context.Db;
        await db.SaveChangesAsync(ct);
        var priorIds = new[] { saleDeltaId, reverseDeltaId };
        var prior = await db.StockValuationFacts.AsNoTracking().Where(f => priorIds.Contains(f.Id)).ToArrayAsync(ct);
        var sources = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.Id == proof.SaleFactId || f.Id == proof.ReversalFactId).ToArrayAsync(ct);
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => h.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var state = await db.StockCostStates.SingleAsync(s => s.CompanyCode == context.CompanyCode
            && s.BranchCode == context.BranchCode && s.ItemCode == plan.ItemCode, ct);
        if (prior.Length != 2 || prior.Any(v => v.StockPostingId != cycle.ActiveRootId
                || v.ReversesValuationFactId is not null || v.ValuationStatus != StockValuationStatuses.Valued)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId.HasValue
                && priorIds.Contains(f.ReversesValuationFactId.Value), ct)
            || sources.Length != 2 || sources.Single(s => s.Id == proof.SaleFactId).CostAmount != proof.ProjectedCost
            || histories.Length != 1 || histories[0].EntryRole != "ORIGINAL" || histories[0].ToBalLocId != cycle.ReceiptBalanceId
            || rootFacts.Length != 1 || rootFacts[0].InventoryHistoryId != histories[0].Id
            || rootFacts[0].SignedQty != cycle.ReceiptQuantity || rootFacts[0].SignedValue != cycle.ReceiptValue
            || state.OnHandBaseQty != plan.Projection.FinalQuantity || state.InventoryValue != plan.Projection.FinalValue
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw MultiWacError("The receipt or reversed sale changed after replay planning.");

        var now = DateTime.UtcNow;
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = sources.Max(s => s.EffectiveAt), PlanWatermark = plan.PhysicalPlan.BranchWatermark,
            PlanHash = StockPostingFingerprint.Create(new { plan.PlanHash, cycle.Fingerprint, proof }, new { }).RequestFingerprint,
            AffectedHistoryCount = 2, AffectedItemCount = 1, AffectedProductionMovementCount = 0,
            CreatedAtUtc = now, CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var values = prior.OrderBy(v => v.Id).Select((value, index) => new StockValuationFact
        {
            CompanyCode = value.CompanyCode, BranchCode = value.BranchCode, LedgerEpochId = value.LedgerEpochId,
            StockPostingId = context.Posting.Id, PostingLineNo = index + 2, SplitOrdinal = 0,
            SourceLineId = value.SourceLineId, SourceDocumentType = value.SourceDocumentType,
            SourceDocumentId = value.SourceDocumentId, SourceDocumentNo = value.SourceDocumentNo,
            SourceDocumentLine = value.SourceDocumentLine, EffectiveAt = value.EffectiveAt,
            BusinessDate = value.BusinessDate, PeriodKey = value.PeriodKey, ItemCode = value.ItemCode,
            WarehouseCode = value.WarehouseCode, LocationCode = value.LocationCode,
            LotId = value.LotId, LotNo = value.LotNo, ItemStatus = value.ItemStatus, BaseUom = value.BaseUom,
            MovementCode = StockValuationMovementClassifier.ReversedSaleReplayValueUndo,
            Direction = -value.Direction, BaseQty = 0m, CostMethod = value.CostMethod,
            UnitCost = value.UnitCost, CostAmount = value.CostAmount, BaseCurrency = value.BaseCurrency,
            BaseCostAmount = value.BaseCostAmount, ValuationSource = StockValuationSources.OriginalReversal,
            ValuationStatus = StockValuationStatuses.Reversed, ValuationVersion = value.ValuationVersion,
            FromBalLocId = value.FromBalLocId, ToBalLocId = value.ToBalLocId,
            OriginalValuationFactId = value.OriginalValuationFactId, ReversesValuationFactId = value.Id,
            CreatedAtUtc = now, CreatedBy = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId
        }).ToArray();
        if (values.Sum(v => v.SignedValue) != 0m)
            throw MultiWacError("The reversed sale undo values do not balance.");
        db.StockValuationFacts.AddRange(values);
        await db.SaveChangesAsync(ct);
        db.StockReplayEffects.AddRange(values.Select(value =>
        {
            var source = sources.Single(s => s.Id == value.OriginalValuationFactId);
            return new StockReplayEffect
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, ReplayRunId = run.Id,
                RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = source.StockPostingId,
                EffectKind = StockReplayEffectTypes.SalesReversalValueUndo,
                AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
                AffectedEntityId = value.Id, OriginalEntityId = source.Id, CreatedAtUtc = now
            };
        }));
        state.LastValuationFactId = values.MaxBy(v => v.PostingLineNo)!.Id;
        await db.SaveChangesAsync(ct);
    }

    private static async Task AppendReversedSaleCorrectionAsync(
        StockPostingContext context, ReversedSaleProof proof, StockReplayWacRemovalPlan plan,
        CancellationToken ct)
    {
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.PostingRole != "REVERSAL"
            || proof.OriginalCost == proof.ProjectedCost || proof.Quantity <= 0m
            || proof.ProjectedCost < 0m || plan.Tail.LaterEvents.Count != 0
            || plan.PhysicalPlan.LaterEvents.Count != 0 || string.IsNullOrWhiteSpace(plan.PlanHash))
            throw MultiWacError("The reversed sale correction has invalid replay authority.");

        var db = context.Db;
        await db.SaveChangesAsync(ct);
        var receipt = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.Id == plan.PhysicalPlan.RemovedActiveEvent.EventId, ct);
        var roots = await db.IvTrxHistories.AsNoTracking().Where(h => h.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == context.Posting.Id).ToArrayAsync(ct);
        var sale = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == proof.SaleHistoryId, ct);
        var reversal = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == proof.ReversalHistoryId, ct);
        var saleFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == proof.SaleFactId, ct);
        var reverseFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f => f.Id == proof.ReversalFactId, ct);
        if (receipt.CompanyCode != context.CompanyCode || receipt.BranchCode != context.BranchCode
            || receipt.StockPostingId != context.Posting.ReversesPostingId || receipt.ICode != plan.ItemCode
            || roots.Length != 1 || roots[0].EntryRole != "REVERSAL" || roots[0].ReversesHistoryId != receipt.Id
            || rootFacts.Length != 1 || rootFacts[0].InventoryHistoryId != roots[0].Id
            || rootFacts[0].SignedQty != -plan.RemovedActiveEvent.OldSignedQuantity
            || rootFacts[0].SignedValue != -plan.RemovedActiveEvent.OldSignedValue
            || sale.CompanyCode != context.CompanyCode || sale.BranchCode != context.BranchCode
            || reversal.CompanyCode != context.CompanyCode || reversal.BranchCode != context.BranchCode
            || reversal.ReversesHistoryId != sale.Id || saleFact.InventoryHistoryId != sale.Id
            || reverseFact.InventoryHistoryId != reversal.Id || reverseFact.ReversesValuationFactId != saleFact.Id
            || saleFact.StockPostingId != sale.StockPostingId || reverseFact.StockPostingId != reversal.StockPostingId
            || saleFact.LedgerEpochId != context.Epoch.Id || reverseFact.LedgerEpochId != context.Epoch.Id
            || saleFact.CostAmount != proof.OriginalCost || reverseFact.CostAmount != proof.OriginalCost
            || saleFact.BaseQty != proof.Quantity || reverseFact.BaseQty != proof.Quantity
            || await db.StockReplayRuns.AsNoTracking().AnyAsync(r => r.RootStockPostingId == context.Posting.Id, ct))
            throw MultiWacError("The receipt or reversed sale changed after replay planning.");

        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 100 ? context.UserId[..100] : context.UserId;
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = new[] { context.Posting.EffectiveAt, saleFact.EffectiveAt, reverseFact.EffectiveAt }.Max(),
            PlanWatermark = plan.PhysicalPlan.BranchWatermark,
            PlanHash = StockPostingFingerprint.Create(new { plan.PlanHash, proof }, new { }).RequestFingerprint,
            AffectedHistoryCount = 2, AffectedItemCount = 1, AffectedProductionMovementCount = 0,
            CreatedAtUtc = now, CreatedBy = user
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var delta = StockLedgerPrecision.Money(proof.ProjectedCost - proof.OriginalCost);
        var values = new[] { NewValue(saleFact, 2), NewValue(reverseFact, 3) };
        if (delta == 0m || values.Sum(v => v.SignedValue) != 0m)
            throw MultiWacError("The reversed sale value corrections do not balance.");
        db.StockValuationFacts.AddRange(values);
        await db.SaveChangesAsync(ct);
        var owners = new[] { saleFact, reverseFact };
        db.StockReplayEffects.AddRange(values.Select((value, i) => new StockReplayEffect
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
            BusinessOwnerPostingId = owners[i].StockPostingId,
            EffectKind = StockReplayEffectTypes.SalesReversalValue,
            AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
            AffectedEntityId = value.Id, OriginalEntityId = owners[i].Id, CreatedAtUtc = now
        }));
        await db.SaveChangesAsync(ct);

        StockValuationFact NewValue(StockValuationFact original, int line) => new()
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id, StockPostingId = context.Posting.Id,
            PostingLineNo = line, SplitOrdinal = 0, SourceLineId = original.SourceLineId,
            SourceDocumentType = original.SourceDocumentType, SourceDocumentId = original.SourceDocumentId,
            SourceDocumentNo = original.SourceDocumentNo, SourceDocumentLine = original.SourceDocumentLine,
            EffectiveAt = original.EffectiveAt, BusinessDate = original.BusinessDate, PeriodKey = original.PeriodKey,
            ItemCode = original.ItemCode, WarehouseCode = original.WarehouseCode, LocationCode = original.LocationCode,
            LotId = original.LotId, LotNo = original.LotNo, ItemStatus = original.ItemStatus, BaseUom = original.BaseUom,
            MovementCode = StockValuationMovementClassifier.ReversedSaleReplayValue,
            Direction = original.Direction * Math.Sign(delta), BaseQty = 0m,
            CostMethod = StockCostMethods.MovingAverage, CostAmount = Math.Abs(delta),
            UnitCost = StockLedgerPrecision.Money(Math.Abs(delta) / proof.Quantity),
            BaseCurrency = original.BaseCurrency, BaseCostAmount = Math.Abs(delta),
            ValuationSource = StockValuationSources.MovingAverage, ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(original.ValuationVersion + 1),
            FromBalLocId = original.FromBalLocId, ToBalLocId = original.ToBalLocId,
            OriginalValuationFactId = original.Id, CreatedAtUtc = now, CreatedBy = user
        };
    }

    private static NeutralReversedSaleCandidate NeutralSaleCandidate(StockReplayWacRemovalPlan plan) =>
        new(plan.ItemCode, plan.PhysicalPlan.BalLocId, plan.PhysicalPlan.LedgerEpochId, true,
            plan.PhysicalPlan.CurrentQuantity, plan.PhysicalPlan.RemovedActiveEvent, plan.PhysicalPlan.LaterEvents,
            plan.PhysicalPlan.Projection.FinalQuantity, plan.Tail.CurrentQuantity, plan.Tail.CurrentValue,
            plan.RemovedActiveEvent, plan.Tail.LaterEvents, plan.Projection.LaterSteps,
            plan.Projection.FinalQuantity, plan.Projection.FinalValue);

    private static bool IsRepeatedItemWacMrShape(IvTrxBatch batch, IvTrxBatchDetail[] details) =>
        details.Length is >= 2 and <= 10
        && details.All(d => d.BatchId == batch.Id && d.CompanyCode == batch.CompanyCode
            && d.BranchCode == batch.BranchCode && d.BatchNo == batch.BatchNo
            && d.TrxType == IvTrxTypes.MiscellaneousReceipt && d.TrxLineNo > 0
            && !string.IsNullOrWhiteSpace(d.ICode))
        && details.Select(d => d.TrxLineNo).Distinct().Count() == details.Length
        && details.Select(d => d.DocumentRevision).Distinct().Count() == 1
        && details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).Count() < details.Length;

    private static string MultiWacMrDraftFingerprint(IvTrxBatch batch, IReadOnlyList<IvTrxBatchDetail> details)
    {
        var evidence = StockPostingFingerprint.Create(new
        {
            batch.Id, batch.CompanyCode, batch.BranchCode, batch.BatchNo, batch.TrxType,
            batch.BatchStatus, batch.TrxDtTime, batch.RefNo, batch.Remarks, batch.VendCode,
            batch.VendName, batch.LocationCode, batch.SourceFingerprint, batch.DeletedAtUtc,
            batch.ForceCloseDate, batch.ModifiedDate, batch.ModifiedBy
        }, details.OrderBy(d => d.TrxLineNo).Select(d => new
        {
            d.Id, d.BatchId, d.CompanyCode, d.BranchCode, d.BatchNo, d.DocumentRevision,
            d.TrxLineNo, d.TrxType, d.ICode, d.IDesc, d.ProdCode, d.ProdDesc,
            d.FromBalLocId, d.FromLotId, d.FrWarehouse, d.FrLocation, d.FrLotNo,
            d.FrStdQty, d.FrStdUom, d.FrPurQty, d.FrPurUom, d.ToBalLocId, d.ToLotId,
            d.ToWarehouse, d.ToLocation, d.ToLotNo, d.ToStdQty, d.ToStdUom, d.ToPurQty,
            d.ToPurUom, d.IStatus, d.IClassCode, d.ExpiryDate, d.Reason, d.DoNo, d.InvNo,
            d.SoNo, d.PoNo, d.PoRelNo, d.SoLineNo, d.PoLineNo, d.Remarks,
            d.PriceEvidence, d.CostEvidenceType, d.CostOverrideReason, d.CostApprovedBy,
            d.CostApprovedAtUtc, d.StockCountCostBasisId, d.UnitPrice, d.Cost, d.CostPrice,
            d.BaseUnitPrices, d.Currency, d.LocationCode
        }).ToArray());
        return $"{evidence.RequestFingerprint}:{evidence.SourceSnapshotHash}";
    }

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedRepeatedItemWacMrAsync(
        string company, string branch, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo
            && b.TrxType == IvTrxTypes.MiscellaneousReceipt && b.BatchStatus == IvBatchStatuses.New, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(batch, details)
            || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        var draftFingerprint = MultiWacMrDraftFingerprint(batch, details);
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        var items = details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasReversedOutgoing = await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct);

        var authorities = new List<StockReplayFifoMrReceiptAuthority>(details.Length);
        foreach (var detail in details)
        {
            var pile = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(read, company, branch, detail, ct);
            if (pile is not > 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Line {detail.TrxLineNo} needs an existing destination pile for replay.");
            authorities.Add(await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(
                read, company, branch, batchNo, pile.Value, detail.Id, ct));
        }
        var plans = new Dictionary<string, StockReplayWacInsertionPlan>(StringComparer.OrdinalIgnoreCase);
        var multiPiles = new Dictionary<string, StockReplayWacMultiPileReceiptInsertionPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in authorities.GroupBy(a => a.ItemCode, StringComparer.OrdinalIgnoreCase))
        {
            var itemLines = group.ToArray();
            if (itemLines.Select(a => a.BalLocId).Distinct().Count() != 1)
            {
                var multi = await StockReplayWacMultiPileReceiptPlanner.BuildInsertionAsync(
                    read, company, branch, itemLines, ct);
                if (multi.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                        read, multi.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string unsupportedMulti)
                    return IvInventoryPostingBatchResult.Fail(batchNo, unsupportedMulti);
                multiPiles.Add(group.Key, multi);
                continue;
            }
            var quantity = IvQty.Round(itemLines.Sum(a => a.Quantity));
            var value = StockLedgerPrecision.Money(itemLines.Sum(a => a.Value));
            var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(read, company, branch,
                itemLines[0].BalLocId, batch.TrxDtTime, quantity, ct);
            var candidate = new StockReplayWacEvent(physical.Candidate.EventId, physical.Candidate.Key,
                0m, 0m, quantity, value);
            var financial = await StockReplayWacInsertionPlanner.BuildAsync(read, company, branch,
                group.Key, physical, candidate, ct);
            if (financial.Tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has a later movement without valuation.");
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, financial.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            plans.Add(group.Key, financial);
        }

        ReversedSaleProof? reversedSaleProof = null;
        if (hasReversedOutgoing)
        {
            if (items.Length == 1 && multiPiles.Count == 0 && plans.TryGetValue(items[0], out var salePlan))
                reversedSaleProof = await ProveNeutralReversedSaleAsync(read, company, branch, batch,
                    NeutralSaleCandidate(salePlan), ct);
            if (reversedSaleProof is null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "This receipt has a later reversed issue that needs additional replay.");
        }

        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.New || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry posting.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(locked, currentDetails)
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || MultiWacMrDraftFingerprint(locked, currentDetails) != draftFingerprint
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct) != hasReversedOutgoing)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry posting.");
        if (hasReversedOutgoing && reversedSaleProof != await ProveNeutralReversedSaleAsync(write,
                company, branch, locked, NeutralSaleCandidate(plans[items[0]]), ct, lockSalesEvidence: true))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The reversed sale changed. Retry posting.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: false, ct,
                allowProvenReversedSale: reversedSaleProof is not null) is { } rejected)
            return rejected;
        foreach (var authority in authorities)
        {
            var current = await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(write, company, branch,
                batchNo, authority.BalLocId, authority.DetailId, ct);
            if (current != authority)
                return IvInventoryPostingBatchResult.Fail(batchNo, "An approved receipt line changed. Retry posting.");
        }
        foreach (var financial in plans.Values)
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, financial.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        foreach (var multi in multiPiles.Values)
            if (multi.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, multi.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: false, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("The receipt replay needs an active stock ledger.");
        var lines = authorities.Select(a => new MultiWacMrInsertLine(a,
            multiPiles.ContainsKey(a.ItemCode) ? null : plans[a.ItemCode])).ToArray();
        var required = await WriteMultiWacMrAsync(context, lines, ct, multiPiles);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return IvInventoryPostingBatchResult.Ok(batchNo, locked.PostingOperationId ?? Guid.Empty);
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedRepeatedItemWacMrAsync(
        string company, string branch, string userId, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo
            && b.TrxType == IvTrxTypes.MiscellaneousReceipt && b.BatchStatus == IvBatchStatuses.Posted, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsRepeatedItemWacMrShape(batch, details)
            || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        var route = await StockReplayInventoryRouteSelector.ReadRollbackAsync(read, company, branch,
            batchNo, IvTrxTypes.MiscellaneousReceipt, ct);
        if (!route.RequiresReplay) return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var items = details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasReversedOutgoing = await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct);
        var histories = (await _posting.LoadHistoryForBatchAsync(read, company, branch, batchNo, ct))
            .OrderBy(h => h.Id).ToArray();
        if (histories.Length != details.Length || histories.Select(h => h.StockPostingId).Distinct().Count() != 1
            || histories.Any(h => h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2 || h.StockPostingId is not > 0
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ValuationStatus != StockValuationStatuses.Valued)
            || details.Any(d => histories.Count(h => h.TrxLineNo == d.TrxLineNo && MultiWacMrHistoryMatches(d, h)) != 1))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has incomplete or changed posted history.");
        var owner = await read.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == histories[0].StockPostingId, ct);
        if (owner is null || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
            || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || owner.CommandType != $"IV_POST_{IvTrxTypes.MiscellaneousReceipt}"
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.PostingRole != "PRIMARY" || owner.EffectiveAt != batch.TrxDtTime
            || histories.Any(h => h.LedgerEpochId != owner.LedgerEpochId || h.TrxDtTime != owner.EffectiveAt
                || h.DocumentRevision != owner.DocumentRevision))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has no exact sealed owner.");

        var plans = new Dictionary<string, SameItemWacRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        var multiPiles = new Dictionary<string, StockReplayWacMultiPileReceiptRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in histories.GroupBy(h => h.ICode!, StringComparer.OrdinalIgnoreCase))
        {
            var originals = group.OrderBy(h => h.Id).ToArray();
            if (originals.Select(h => h.ToBalLocId).Distinct().Count() > 1)
            {
                var multi = await StockReplayWacMultiPileReceiptPlanner.BuildRemovalAsync(
                    read, company, branch, originals, owner, ct);
                if (multi.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                        read, multi.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string unsupportedMulti)
                    return IvInventoryPostingBatchResult.Fail(batchNo, unsupportedMulti);
                multiPiles.Add(group.Key, multi);
                continue;
            }
            var first = originals[0];
            var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{first.Id:D10}", 0);
            var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(read, company, branch,
                first.ToBalLocId!.Value, new StockReplayPhysicalEvent(first.Id, key, first.ToStdQty!.Value), ct);
            var anchor = await StockReplayWacRemovalPlanner.BuildAsync(read, company, branch, group.Key, physical, ct);
            var plan = ComposeReceiptItemRemoval(anchor, originals, owner);
            if (plan.Combined.Tail.LaterEvents.Count == 0 && plan.Combined.PhysicalPlan.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt has a later movement without valuation.");
            if (plan.Combined.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, plan.Combined.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            plans.Add(group.Key, plan);
        }

        ReversedSaleProof? reversedSaleProof = null;
        if (hasReversedOutgoing)
        {
            if (items.Length == 1 && multiPiles.Count == 0 && plans.TryGetValue(items[0], out var salePlan))
                reversedSaleProof = await ProveNeutralReversedSaleAsync(read, company, branch, batch,
                    NeutralSaleCandidate(salePlan.Combined), ct);
            if (reversedSaleProof is null)
                return IvInventoryPostingBatchResult.Fail(batchNo,
                    "This receipt has a later cost dependency from a reversed issue that needs additional replay before rollback.");
        }

        var fingerprint = MultiWacMrDraftFingerprint(batch, details);
        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.Posted || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry rollback.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        var currentHistories = (await _posting.LoadHistoryForBatchAsync(write, company, branch, batchNo, ct))
            .OrderBy(h => h.Id).ToArray();
        var currentOwner = await write.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == owner.Id, ct);
        if (!IsRepeatedItemWacMrShape(locked, currentDetails) || currentHistories.Length != histories.Length
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || MultiWacMrDraftFingerprint(locked, currentDetails) != fingerprint
            || !currentHistories.Select(h => h.Id).SequenceEqual(histories.Select(h => h.Id))
            || currentDetails.Any(d => currentHistories.Count(h => h.TrxLineNo == d.TrxLineNo
                && MultiWacMrHistoryMatches(d, h)) != 1)
            || currentHistories.Any(h => h.StockPostingId != owner.Id || h.LedgerEpochId != owner.LedgerEpochId
                || h.DocumentRevision != owner.DocumentRevision || h.TrxDtTime != owner.EffectiveAt
                || h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ValuationStatus != StockValuationStatuses.Valued)
            || currentOwner is null || currentOwner.CompanyCode != owner.CompanyCode
            || currentOwner.BranchCode != owner.BranchCode || currentOwner.LedgerEpochId != owner.LedgerEpochId
            || currentOwner.PostingSequence != owner.PostingSequence || currentOwner.CommandType != owner.CommandType
            || currentOwner.SourceModule != owner.SourceModule || currentOwner.SourceDocumentType != owner.SourceDocumentType
            || currentOwner.SourceDocumentId != owner.SourceDocumentId || currentOwner.SourceDocumentNo != owner.SourceDocumentNo
            || currentOwner.DocumentRevision != owner.DocumentRevision || currentOwner.PostingRole != owner.PostingRole
            || currentOwner.EffectiveAt != owner.EffectiveAt || currentOwner.SealedAtUtc != owner.SealedAtUtc
            || currentOwner.RequestFingerprint != owner.RequestFingerprint || currentOwner.SourceSnapshotHash != owner.SourceSnapshotHash
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct) != hasReversedOutgoing)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry rollback.");
        if (hasReversedOutgoing && reversedSaleProof != await ProveNeutralReversedSaleAsync(write,
                company, branch, locked, NeutralSaleCandidate(plans[items[0]].Combined), ct, lockSalesEvidence: true))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The reversed sale changed. Retry rollback.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: true, ct,
                allowProvenReversedSale: reversedSaleProof is not null) is { } rejected)
            return rejected;
        var qaRestore = await _qaPhysical.RestoreStockOutAsync(write, company, branch, batchNo, ct);
        if (qaRestore is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        foreach (var plan in plans.Values)
            if (plan.Combined.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, plan.Combined.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        foreach (var multi in multiPiles.Values)
            if (multi.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, multi.Tail.LaterEvents, batch.TrxDtTime, ct, allowBoundedSales: true) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: true, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("The receipt rollback needs an active stock ledger.");
        if (context.CostMethod != StockCostMethods.MovingAverage || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.SourceDocumentId != owner.SourceDocumentId
            || context.Posting.SourceDocumentNo != owner.SourceDocumentNo
            || context.Posting.PostingRole != "REVERSAL" || context.Posting.ReversesPostingId != owner.Id
            || context.Posting.EffectiveAt != owner.EffectiveAt)
            throw MultiWacError("The rollback root does not reverse its receipt owner.");
        var refreshedPlans = new Dictionary<string, SameItemWacRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        var refreshedMulti = new Dictionary<string, StockReplayWacMultiPileReceiptRemovalPlan>(StringComparer.OrdinalIgnoreCase);
        var permits = new Dictionary<string, StockReplayWacWritePermit>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var originals = currentHistories.Where(h => string.Equals(h.ICode, item, StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Id).ToArray();
            if (multiPiles.TryGetValue(item, out var multi))
            {
                var (refreshedMultiPlan, multiPermit) = await StockReplayWacMultiPileReceiptPlanner.PrepareRemovalAsync(
                    context, multi, originals, owner, ct);
                refreshedMulti.Add(item, refreshedMultiPlan);
                permits.Add(item, multiPermit);
                continue;
            }
            var plan = plans[item];
            await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(context, plan.Anchor, ct);
            var refreshed = ComposeReceiptItemRemoval(plan.Anchor, originals, owner);
            if (refreshed.Combined.PlanHash != plan.Combined.PlanHash)
                throw MultiWacError("The receipt removal plan changed after planning.");
            var combined = refreshed.Combined;
            refreshedPlans.Add(item, refreshed);
            permits.Add(item, new StockReplayWacWritePermit(context, item, combined.PlanHash,
                combined.Tail.CurrentQuantity, combined.Tail.CurrentValue,
                combined.Projection.FinalQuantity, combined.Projection.FinalValue,
                combined.PhysicalPlan.BalLocId, combined.PhysicalPlan.Projection.FinalQuantity));
        }
        var result = await RollBackInventoryMRCoreAsync(write, company, branch, userId, batchNo,
            IvTrxTypes.MiscellaneousReceipt, ct, context);
        if (!result.Succeeded) return result;
        await write.SaveChangesAsync(ct);
        var reversals = await write.IvTrxHistories.Where(h => h.StockPostingId == context.Posting.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(ct);
        if (reversals.Length != histories.Length)
            throw MultiWacError("The receipt has incomplete reversal histories.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(write, company, branch,
            currentHistories, ct);
        foreach (var plan in refreshedPlans.Values)
        {
            var combined = plan.Combined;
            var pile = await write.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == combined.PhysicalPlan.BalLocId
                && b.CompanyCode == company && b.BranchCode == branch, ct);
            if (pile.StdQty != combined.PhysicalPlan.Projection.FinalQuantity)
                throw MultiWacError("The receipt rollback does not reconcile with its destination pile.");
        }
        foreach (var multi in refreshedMulti.Values)
            foreach (var pilePlan in multi.Piles)
            {
                var pile = await write.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == pilePlan.BalLocId
                    && b.CompanyCode == company && b.BranchCode == branch, ct);
                if (pile.StdQty != pilePlan.Projection.FinalQuantity)
                    throw MultiWacError("The receipt rollback does not reconcile with its destination pile.");
            }
        for (var i = 0; i < currentHistories.Length; i++)
        {
            var original = currentHistories[i];
            var reversal = reversals.SingleOrDefault(h => h.ReversesHistoryId == original.Id);
            var facts = activeFacts[original.Id];
            var source = refreshedMulti.TryGetValue(original.ICode!, out var multi)
                ? multi.SourceEvents[original.Id] : refreshedPlans[original.ICode!].SourceEvents[original.Id];
            if (reversal is null || reversal.FromBalLocId != original.ToBalLocId
                || reversal.FrStdQty != original.ToStdQty || reversal.FromLotId != original.ToLotId
                || facts.Count != 1 || facts[0].CostMethod != StockCostMethods.MovingAverage
                || facts[0].SignedQty != source.OldSignedQuantity || facts[0].SignedValue != source.OldSignedValue
                || facts[0].LotId != original.ToLotId
                || !string.Equals(facts[0].LotNo ?? "", original.ToLotNo ?? "", StringComparison.OrdinalIgnoreCase))
                throw MultiWacError("A receipt line reversal does not reconcile with its active value.");
            await StockReplayValuationReversalWriter.AppendAsync(context, original.Id,
                [new StockReplayValuationReversalLine(facts[0].Id, i + 1, 0, reversal.Id)], ct);
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
        }
        var required = new List<int>();
        var nextLine = currentHistories.Length + 1;
        foreach (var item in items)
        {
            var hasMulti = refreshedMulti.TryGetValue(item, out var multi);
            var tail = hasMulti
                ? await StockReplayWacTailRepricingWriter.AppendPreparedMultiPileReceiptRemovalAsync(
                    context, permits[item], multi!, nextLine, ct, allowSalesReturn: true)
                : await StockReplayWacTailRepricingWriter.AppendPreparedRemovalTailAsync(
                    context, permits[item], refreshedPlans[item].Combined, nextLine, ct, allowSalesReturn: true);
            required.AddRange(tail.RepricedHistoryIds);
            nextLine = checked(nextLine + 2 * (hasMulti ? multi!.Tail.LaterEvents.Count
                : refreshedPlans[item].Combined.Tail.LaterEvents.Count));
            await StockReplayWacCostStateWriter.ApplyAsync(context, permits[item], ct);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return result;
    }

    private static SameItemWacRemovalPlan ComposeReceiptItemRemoval(
        StockReplayWacRemovalPlan anchor, IReadOnlyList<IvTrxHistory> originals, StockPosting owner)
    {
        if (originals.Count == 1 && originals[0].Id == anchor.RemovedActiveEvent.EventId
            && originals[0].ICode == anchor.ItemCode && originals[0].StockPostingId == owner.Id
            && originals[0].ToBalLocId == anchor.PhysicalPlan.BalLocId)
            return new(anchor, anchor, new Dictionary<int, StockReplayWacEvent>
            {
                [originals[0].Id] = anchor.RemovedActiveEvent
            });
        return ComposeSameItemWacRemoval(anchor, originals, owner);
    }

    private static SameItemWacRemovalPlan ComposeSameItemWacRemoval(
        StockReplayWacRemovalPlan anchor, IReadOnlyList<IvTrxHistory> originals, StockPosting owner)
    {
        if (originals.Count < 2 || originals.Select(h => h.Id).Distinct().Count() != originals.Count
            || originals.Any(h => h.ICode != anchor.ItemCode || h.ToBalLocId != anchor.PhysicalPlan.BalLocId
                || h.StockPostingId != owner.Id || h.ToStdQty is not > 0m)
            || originals.Min(h => h.Id) != anchor.RemovedActiveEvent.EventId)
            throw MultiWacError("The receipt lines do not share one verified item and pile.");
        var ids = originals.Select(h => (long)h.Id).ToHashSet();
        var physicalEvents = new[] { anchor.PhysicalPlan.RemovedActiveEvent }
            .Concat(anchor.PhysicalPlan.LaterEvents.Where(e => ids.Contains(e.EventId)))
            .OrderBy(e => e.Key).ToArray();
        if (physicalEvents.Length != originals.Count || physicalEvents.Select(e => e.EventId).Distinct().Count() != originals.Count
            || originals.Any(h => !physicalEvents.Any(e => e.EventId == h.Id && e.SignedDelta == h.ToStdQty
                && e.Key == new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                    owner.PostingSequence, $"H{h.Id:D10}", 0))))
            throw MultiWacError("The receipt lines do not have exact active physical events.");
        var lastKey = physicalEvents[^1].Key;
        if (anchor.PhysicalPlan.LaterEvents.Any(e => !ids.Contains(e.EventId) && e.Key.CompareTo(lastKey) <= 0)
            || anchor.Tail.LaterEvents.Any(e => !ids.Contains(e.EventId) && e.Key.CompareTo(lastKey) <= 0)
            || anchor.Tail.PhysicalByPile.Any(p => p.Key != anchor.PhysicalPlan.BalLocId
                && p.Value.Any(e => ids.Contains(e.EventId))))
            throw MultiWacError("Another movement overlaps the receipt lines.");
        var financialEvents = new[] { anchor.RemovedActiveEvent }
            .Concat(anchor.Tail.LaterEvents.Where(e => ids.Contains(e.EventId)))
            .ToDictionary(e => checked((int)e.EventId));
        if (financialEvents.Count != originals.Count || originals.Any(h =>
                !financialEvents.TryGetValue(h.Id, out var e)
                || e.Key != physicalEvents.Single(p => p.EventId == h.Id).Key
                || e.OldSignedQuantity != h.ToStdQty || e.ReplaySignedQuantity != h.ToStdQty
                || e.OldSignedValue < 0m || e.FixedSignedValue != e.OldSignedValue
                || e.AbsoluteCountQty is not null))
            throw MultiWacError("The receipt lines do not have exact active WAC values.");

        var quantity = IvQty.Round(physicalEvents.Sum(e => e.SignedDelta));
        var value = StockLedgerPrecision.Money(financialEvents.Values.Sum(e => e.OldSignedValue));
        var combinedPhysicalEvent = anchor.PhysicalPlan.RemovedActiveEvent with { SignedDelta = quantity };
        var laterPhysical = anchor.PhysicalPlan.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray();
        var physicalProjection = StockReplayPhysicalSimulator.SimulateRemoval(
            anchor.PhysicalPlan.CurrentQuantity, combinedPhysicalEvent, laterPhysical);
        var combinedPhysical = anchor.PhysicalPlan with { RemovedActiveEvent = combinedPhysicalEvent,
            LaterEvents = laterPhysical, Projection = physicalProjection };
        var combinedEvent = anchor.RemovedActiveEvent with { OldSignedQuantity = quantity,
            OldSignedValue = value, ReplaySignedQuantity = quantity, FixedSignedValue = value };
        var combinedTail = anchor.Tail with
        {
            LaterEvents = anchor.Tail.LaterEvents.Where(e => !ids.Contains(e.EventId)).ToArray(),
            PhysicalByPile = anchor.Tail.PhysicalByPile.ToDictionary(p => p.Key,
                p => (IReadOnlyList<StockReplayPhysicalEvent>)p.Value.Where(e => !ids.Contains(e.EventId)).ToArray())
        };
        var projection = StockReplayWacTailProjector.SimulateSinglePileRemoval(
            combinedTail, combinedPhysical, combinedEvent);
        var hash = StockPostingFingerprint.Create(new { anchor.PlanHash,
            originals = physicalEvents, values = financialEvents.OrderBy(p => p.Key).Select(p => p.Value).ToArray(),
            combinedPhysicalEvent, combinedEvent, projection }, new { }).RequestFingerprint;
        var combined = anchor with { PhysicalPlan = combinedPhysical, RemovedActiveEvent = combinedEvent,
            Tail = combinedTail, Projection = projection, PlanHash = hash };
        return new(anchor, combined, financialEvents);
    }
}
