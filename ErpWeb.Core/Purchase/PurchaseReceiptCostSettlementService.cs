using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed record PurchaseReceiptSettlementResult(
    bool Succeeded,
    string? Error,
    IReadOnlyList<PurchaseReceiptCostSettlement> Settlements,
    IReadOnlyList<PurchaseCostAdjustmentRequest> Adjustments)
{
    public static PurchaseReceiptSettlementResult Fail(string error) =>
        new(false, error, [], []);

    public static PurchaseReceiptSettlementResult Success(
        IReadOnlyList<PurchaseReceiptCostSettlement> settlements,
        IReadOnlyList<PurchaseCostAdjustmentRequest> adjustments) =>
        new(true, null, settlements, adjustments);
}

public interface IPurchaseReceiptCostSettlementService
{
    Task<PurchaseReceiptSettlementResult> AllocateInvoiceAsync(
        AppDbContext db,
        PoInvoice invoice,
        string costMethod,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default,
        StockPostingContext? replayRoot = null);

    Task<PurchaseReceiptSettlementResult> ReverseInvoiceAsync(
        AppDbContext db,
        PoInvoice invoice,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<PurchaseReceiptSettlementResult> RemapValueNeutralInvoiceAsync(
        StockPostingContext replayRoot,
        PoInvoice invoice,
        IPurchaseCostAdjustmentPostingService adjustmentWriter,
        CancellationToken cancellationToken = default,
        bool removingReceipt = false,
        bool allowPostedCreditNote = false,
        string? replayPlanHash = null,
        long? replayBranchWatermark = null);

    Task<PurchaseReceiptSettlementResult> ReverseCreditNoteAsync(
        AppDbContext db,
        PoInvoice creditNote,
        PoInvoice referencedInvoice,
        string costMethod,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<PurchaseReceiptSettlementResult> RollbackCreditNoteAsync(
        AppDbContext db,
        PoInvoice creditNote,
        long primaryStockPostingId,
        long rollbackStockPostingId,
        string userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Allocates a stock-controlled purchase invoice against immutable GR valuation facts.
/// An unsealed receipt is eligible only when it belongs to the current GR replay root.
/// This service deliberately does not infer cost from the current PO or item master.
/// </summary>
public sealed class PurchaseReceiptCostSettlementService : IPurchaseReceiptCostSettlementService
{
    private const decimal Epsilon = 0.0000005m;
    private const int MaxCreditNoteReplayGenerations = 8;

    /// <summary>
    /// Rebuilds one posted PI's active receipt allocation in an existing GR replay
    /// transaction. WAC and FIFO require zero variance; Standard permits
    /// purchase variance only when it has no inventory-value effect.
    /// callers must roll back their transaction if this method throws.
    /// </summary>
    public async Task<PurchaseReceiptSettlementResult> RemapValueNeutralInvoiceAsync(
        StockPostingContext replayRoot,
        PoInvoice invoice,
        IPurchaseCostAdjustmentPostingService adjustmentWriter,
        CancellationToken cancellationToken = default,
        bool removingReceipt = false,
        bool allowPostedCreditNote = false,
        string? replayPlanHash = null,
        long? replayBranchWatermark = null)
    {
        ArgumentNullException.ThrowIfNull(replayRoot);
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(adjustmentWriter);
        replayRoot.EnsureUnsealed();
        var db = replayRoot.Db;
        var root = replayRoot.Posting;
        var persistedInvoice = await db.PoInvoices.AsNoTracking()
            .Include(x => x.Details)
            .SingleOrDefaultAsync(x =>
                x.CompanyCode == invoice.CompanyCode
                && x.BranchCode == invoice.BranchCode
                && x.DocNo == invoice.DocNo,
                cancellationToken);
        if (db.Database.CurrentTransaction is null
            || root.Id <= 0
            || root.CompanyCode != invoice.CompanyCode
            || root.BranchCode != invoice.BranchCode
            || root.SourceModule != "INVENTORY"
            || root.SourceDocumentType != IvTrxTypes.GoodsReceive
            || root.PostingRole != (removingReceipt ? "REVERSAL" : "PRIMARY")
            || invoice.Type != PoInvoiceTypes.Invoice
            || invoice.Status != PoInvoiceStatuses.Posted
            || replayRoot.CostMethod is not (StockCostMethods.MovingAverage
                or StockCostMethods.Standard or StockCostMethods.Fifo)
            || persistedInvoice is null
            || persistedInvoice.Type != PoInvoiceTypes.Invoice
            || persistedInvoice.Status != PoInvoiceStatuses.Posted
            || persistedInvoice.DeletedAtUtc is not null
            || persistedInvoice.CostingRevision != invoice.CostingRevision)
            return PurchaseReceiptSettlementResult.Fail(
                "Value-neutral PI remap needs one posted invoice and an active GR replay transaction.");
        invoice = persistedInvoice;

        var dependentNotes = await db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == invoice.CompanyCode
                && x.BranchCode == invoice.BranchCode
                && x.Type == PoInvoiceTypes.CreditNote
                && x.InvNo == invoice.DocNo
                && (x.Status == PoInvoiceStatuses.Posted
                    || x.Status == PoInvoiceStatuses.New))
            .Select(x => new { x.DocNo, x.Status, x.RollbackDate })
            .ToArrayAsync(cancellationToken);
        var drafts = dependentNotes
            .Where(x => x.Status == PoInvoiceStatuses.New).ToArray();
        var hasUnsafeDraft = false;
        if (drafts.Length > 0)
        {
            var docNos = drafts.Select(x => x.DocNo).ToArray();
            var postings = await db.StockPostings.AsNoTracking()
                .Where(p => p.CompanyCode == invoice.CompanyCode
                    && p.BranchCode == invoice.BranchCode
                    && p.SourceModule == "PROCUREMENT"
                    && p.SourceDocumentType == "PO_INVOICE_CN"
                    && docNos.Contains(p.SourceDocumentNo))
                .Select(p => new { p.Id, p.SourceDocumentNo,
                    p.PostingRole, p.ReversesPostingId, p.SealedAtUtc })
                .ToArrayAsync(cancellationToken);
            var adjustmentDocNos = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(a => a.CompanyCode == invoice.CompanyCode
                    && a.BranchCode == invoice.BranchCode
                    && a.SourceDocumentType == "PO_INVOICE_CN"
                    && docNos.Contains(a.SourceDocumentNo))
                .Select(a => a.SourceDocumentNo)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            hasUnsafeDraft = drafts.Any(note =>
            {
                var notePostings = postings.Where(p =>
                    p.SourceDocumentNo == note.DocNo).ToArray();
                if (note.RollbackDate is null)
                    return notePostings.Length != 0
                        || adjustmentDocNos.Contains(note.DocNo);
                var primaries = notePostings.Where(p =>
                    p.PostingRole == "PRIMARY").ToArray();
                return primaries.Length == 0
                    || notePostings.Length != primaries.Length * 2
                    || primaries.Any(primary =>
                    primary.SealedAtUtc is null
                    || notePostings.Count(reversal =>
                        reversal.PostingRole == "REVERSAL"
                        && reversal.ReversesPostingId == primary.Id
                        && reversal.SealedAtUtc is not null) != 1);
            });
        }
        var postedNotes = dependentNotes.Where(x =>
            x.Status == PoInvoiceStatuses.Posted).ToArray();
        if (postedNotes.Length > 1
            || postedNotes.Length == 1 && (!allowPostedCreditNote
                || postedNotes[0].RollbackDate is not null)
            || hasUnsafeDraft
            || await db.PoCdns.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == invoice.CompanyCode
                && x.BranchCode == invoice.BranchCode
                && x.InvNo == invoice.DocNo
                && (x.Status == PoCdnStatuses.New
                    || x.Status == PoCdnStatuses.Posted),
                cancellationToken))
            return PurchaseReceiptSettlementResult.Fail(
                "A purchase credit/debit note depends on this invoice; PI remap needs CN generation replay.");

        PoInvoice? postedNote = null;
        long noteOwnerId = 0;
        PurchaseReceiptSettlementResult? noteRemoval = null;
        if (postedNotes.Length == 1)
        {
            if (replayPlanHash is null || replayPlanHash.Length != 64
                || replayPlanHash.Any(c => !Uri.IsHexDigit(c))
                || replayBranchWatermark is not long watermark
                || watermark < 0 || root.PostingSequence <= 0
                || watermark != root.PostingSequence - 1)
                return PurchaseReceiptSettlementResult.Fail(
                    "Posted credit-note replay needs the validated GR plan hash and branch watermark.");
            postedNote = await db.PoInvoices.AsNoTracking()
                .Include(x => x.Details)
                .SingleAsync(x => x.CompanyCode == invoice.CompanyCode
                    && x.BranchCode == invoice.BranchCode
                    && x.DocNo == postedNotes[0].DocNo,
                    cancellationToken);
            var creditLine = postedNote.Details.SingleOrDefault();
            var invoiceLine = invoice.Details.SingleOrDefault();
            if (postedNote.DeletedAtUtc is not null
                || creditLine is null || invoiceLine is null
                || creditLine.ReferencedInvoiceLineNo != invoiceLine.Line
                || creditLine.ICode != invoiceLine.ICode
                || creditLine.PoNo != invoiceLine.PoNo
                || creditLine.PoRelNo != invoiceLine.PoRelNo
                || creditLine.PoLineNo != invoiceLine.PoLineNo
                || creditLine.StdQty <= 0m
                || creditLine.StdQty > invoiceLine.StdQty
                || creditLine.Qty != creditLine.StdQty
                || postedNote.DocDate.Year != root.EffectiveAt.Year
                || postedNote.DocDate.Month != root.EffectiveAt.Month)
                return PurchaseReceiptSettlementResult.Fail(
                    "Posted credit note is outside the bounded single-line GR replay.");
            var owners = await db.StockPostings.AsNoTracking()
                .Where(x => x.CompanyCode == invoice.CompanyCode
                    && x.BranchCode == invoice.BranchCode
                    && x.SourceModule == "PROCUREMENT"
                    && x.SourceDocumentType == "PO_INVOICE_CN"
                    && x.SourceDocumentNo == postedNote.DocNo
                    && x.DocumentRevision == postedNote.CostingRevision
                    && x.PostingRole == "PRIMARY"
                    && x.SealedAtUtc != null)
                .Select(x => x.Id).ToArrayAsync(cancellationToken);
            if (owners.Length != 1)
                return PurchaseReceiptSettlementResult.Fail(
                    "Posted credit note has no exact sealed costing owner.");
            noteOwnerId = owners[0];
            var priorEffects = await db.StockReplayEffects.AsNoTracking()
                .CountAsync(x => x.CompanyCode == invoice.CompanyCode
                    && x.BranchCode == invoice.BranchCode
                    && x.BusinessOwnerPostingId == noteOwnerId
                    && x.EffectKind == StockReplayEffectTypes
                        .PurchaseCreditNoteCostReplacement,
                    cancellationToken);
            if (priorEffects >= MaxCreditNoteReplayGenerations * 2)
                return PurchaseReceiptSettlementResult.Fail(
                    "The posted credit note reached its supported GR replay generation limit.");
            noteRemoval = await RollbackCreditNoteAsync(db, postedNote,
                noteOwnerId, root.Id, replayRoot.UserId, cancellationToken);
            if (!noteRemoval.Succeeded
                || noteRemoval.Settlements.Count != 1
                || noteRemoval.Adjustments.Count != 1
                || !HasNoInventoryValueChangeForCreditNote(noteRemoval.Adjustments,
                    replayRoot.CostMethod))
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.ReversalDependency,
                    noteRemoval.Error ?? "The posted credit note cannot be reversed without changing inventory value."));
            await adjustmentWriter.AppendInTransactionAsync(replayRoot,
                noteRemoval.Adjustments, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var reverse = await ReverseInvoiceAsync(db, invoice, root.Id,
            replayRoot.UserId, cancellationToken);
        if (!reverse.Succeeded)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ReversalDependency,
                reverse.Error ?? "The active invoice generation cannot be reversed."));
        if (!MatchesPostedInvoiceAmounts(reverse.Adjustments, invoice))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ReversalDependency,
                "The posted invoice amount or quantity changed; its purchase price variance cannot be remapped."));
        if (!HasNoInventoryValueChange(reverse.Adjustments,
                replayRoot.CostMethod))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ReversalDependency,
                "The active invoice has inventory-affecting purchase price variance and needs full cost replay."));

        await adjustmentWriter.AppendInTransactionAsync(replayRoot,
            reverse.Adjustments, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var replacement = await AllocateInvoiceAsync(db, invoice,
            replayRoot.CostMethod, root.Id, replayRoot.UserId,
            cancellationToken, removingReceipt ? null : replayRoot);
        if (!replacement.Succeeded)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ReversalDependency,
                replacement.Error ?? "The invoice could not be reallocated after GR replay."));
        if (!HasNoInventoryValueChange(replacement.Adjustments,
                replayRoot.CostMethod))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ReversalDependency,
                "The revised invoice allocation has inventory-affecting purchase price variance and needs full cost replay."));
        await adjustmentWriter.AppendInTransactionAsync(replayRoot,
            replacement.Adjustments, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (postedNote is not null)
        {
            var noteReplacement = await ReverseCreditNoteAsync(db, postedNote,
                invoice, replayRoot.CostMethod, root.Id,
                replayRoot.UserId, cancellationToken);
            if (!noteReplacement.Succeeded
                || noteReplacement.Settlements.Count != 1
                || noteReplacement.Adjustments.Count != 1
                || !HasNoInventoryValueChangeForCreditNote(noteReplacement.Adjustments,
                    replayRoot.CostMethod)
                || noteReplacement.Settlements[0].SettledBaseQty
                    != noteRemoval!.Settlements[0].SettledBaseQty
                || noteReplacement.Adjustments[0].ActualBaseAmount
                    != noteRemoval.Adjustments[0].ActualBaseAmount
                || noteReplacement.Adjustments[0].ReferenceBaseAmount
                    != noteRemoval.Adjustments[0].ReferenceBaseAmount
                || noteReplacement.Adjustments[0].TotalAdjustmentAmount
                    != -noteRemoval.Adjustments[0].TotalAdjustmentAmount)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.ReversalDependency,
                    noteReplacement.Error ?? "The posted credit note could not be reallocated without changing its costing."));
            var replacementAdjustments = await adjustmentWriter
                .AppendInTransactionAsync(replayRoot,
                    noteReplacement.Adjustments, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var oldSettlementId = noteRemoval.Settlements[0]
                .ReversesSettlementId;
            var oldAdjustmentId = noteRemoval.Adjustments[0]
                .ReversesAdjustmentId;
            if (oldSettlementId is null || oldAdjustmentId is null
                || replacementAdjustments.Count != 1)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.ReversalDependency,
                    "Credit-note replacement has no exact prior generation."));
            var now = DateTime.UtcNow;
            var run = new StockReplayRun
            {
                CompanyCode = replayRoot.CompanyCode,
                BranchCode = replayRoot.BranchCode,
                RootStockPostingId = root.Id,
                RootRequestId = root.RequestId,
                RootDocumentType = root.SourceDocumentType,
                RootDocumentId = root.SourceDocumentId,
                EarliestEffectiveAt = root.EffectiveAt,
                LatestEffectiveAt = root.EffectiveAt,
                PlanWatermark = replayBranchWatermark!.Value,
                PlanHash = replayPlanHash!,
                AffectedHistoryCount = 0,
                AffectedItemCount = 1,
                AffectedProductionMovementCount = 0,
                CreatedAtUtc = now,
                CreatedBy = replayRoot.UserId
            };
            db.StockReplayRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            db.StockReplayEffects.AddRange(
                new StockReplayEffect
                {
                    CompanyCode = replayRoot.CompanyCode,
                    BranchCode = replayRoot.BranchCode,
                    ReplayRunId = run.Id,
                    RootStockPostingId = root.Id,
                    BusinessOwnerPostingId = noteOwnerId,
                    EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
                    AffectedEntityType = StockReplayEffectTypes.PurchaseReceiptSettlement,
                    AffectedEntityId = noteReplacement.Settlements[0].Id,
                    OriginalEntityId = oldSettlementId,
                    CreatedAtUtc = now
                },
                new StockReplayEffect
                {
                    CompanyCode = replayRoot.CompanyCode,
                    BranchCode = replayRoot.BranchCode,
                    ReplayRunId = run.Id,
                    RootStockPostingId = root.Id,
                    BusinessOwnerPostingId = noteOwnerId,
                    EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
                    AffectedEntityType = StockReplayEffectTypes.PurchaseCostAdjustment,
                    AffectedEntityId = replacementAdjustments[0].Id,
                    OriginalEntityId = oldAdjustmentId,
                    CreatedAtUtc = now
                });
            await db.SaveChangesAsync(cancellationToken);
        }
        return replacement;
    }

    private static bool HasNoInventoryValueChange(
        IReadOnlyList<PurchaseCostAdjustmentRequest> adjustments,
        string costMethod) =>
        adjustments.Count > 0 && adjustments.All(x =>
            x.CostMethod == costMethod
            && x.InventoryAdjustmentAmount == 0m
            && x.ValueAdjustment is null
            && (costMethod == StockCostMethods.Standard
                ? x.ConsumedVarianceAmount == x.TotalAdjustmentAmount
                    && x.TotalAdjustmentAmount == StockLedgerPrecision.Money(
                        (x.ReversesAdjustmentId is null ? 1m : -1m)
                        * (x.ActualBaseAmount - x.ReferenceBaseAmount))
                : x.TotalAdjustmentAmount == 0m
                    && x.ConsumedVarianceAmount == 0m));

    // CN removal reverses a CN reversal; its Standard PPV sign is the opposite
    // of PI removal even though both requests have ReversesAdjustmentId set.
    private static bool HasNoInventoryValueChangeForCreditNote(
        IReadOnlyList<PurchaseCostAdjustmentRequest> adjustments,
        string costMethod) =>
        adjustments.Count == 1 && adjustments.All(x =>
            x.CostMethod == costMethod
            && x.InventoryAdjustmentAmount == 0m
            && x.ValueAdjustment is null
            && x.ConsumedVarianceAmount == x.TotalAdjustmentAmount
            && (costMethod == StockCostMethods.Standard
                || x.TotalAdjustmentAmount == 0m));

    private static bool MatchesPostedInvoiceAmounts(
        IReadOnlyList<PurchaseCostAdjustmentRequest> adjustments,
        PoInvoice invoice)
    {
        if (invoice.Details.Count != 1 || adjustments.Count == 0)
            return false;
        var line = invoice.Details.Single();
        return adjustments.All(x => x.SourceDocumentLine == line.Line)
            && StockLedgerPrecision.Quantity(adjustments.Sum(x => x.BaseQty))
                == StockLedgerPrecision.Quantity(line.StdQty)
            && StockLedgerPrecision.Money(adjustments.Sum(x => x.ActualBaseAmount))
                == StockLedgerPrecision.Money(line.NetAmount * invoice.CurrRate);
    }

    public async Task<PurchaseReceiptSettlementResult> AllocateInvoiceAsync(
        AppDbContext db,
        PoInvoice invoice,
        string costMethod,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default,
        StockPostingContext? replayRoot = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoice);

        if (!string.Equals(invoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase))
            return PurchaseReceiptSettlementResult.Fail("Only purchase invoices allocate new receipt settlements.");
        if (stockPostingId <= 0)
            return PurchaseReceiptSettlementResult.Fail("A sealed stock posting identity is required for purchase settlement.");
        if (string.IsNullOrWhiteSpace(costMethod))
            return PurchaseReceiptSettlementResult.Fail("An active inventory cost method is required for purchase settlement.");
        if (invoice.CurrRate <= 0m)
            return PurchaseReceiptSettlementResult.Fail("Invoice currency rate must be greater than zero.");

        var ownUnsealedReceiptId = 0L;
        if (replayRoot is not null)
        {
            replayRoot.EnsureUnsealed();
            var root = replayRoot.Posting;
            if (!ReferenceEquals(replayRoot.Db, db)
                || root.Id != stockPostingId
                || root.CompanyCode != invoice.CompanyCode
                || root.BranchCode != invoice.BranchCode
                || root.SourceModule != "INVENTORY"
                || root.SourceDocumentType != IvTrxTypes.GoodsReceive
                || root.PostingRole != "PRIMARY"
                || root.ReversesPostingId is not null
                || root.SealedAtUtc is not null
                || !await db.StockPostings.AsNoTracking().AnyAsync(p =>
                    p.Id == root.Id && p.CompanyCode == root.CompanyCode
                    && p.BranchCode == root.BranchCode
                    && p.SourceModule == root.SourceModule
                    && p.SourceDocumentType == root.SourceDocumentType
                    && p.SourceDocumentId == root.SourceDocumentId
                    && p.PostingRole == "PRIMARY"
                    && p.SealedAtUtc == null,
                    cancellationToken))
                return PurchaseReceiptSettlementResult.Fail(
                    "Only this transaction's unsealed GR root may supply a receipt to PI replay.");
            ownUnsealedReceiptId = root.Id;
        }

        var lines = invoice.Details
            .OrderBy(x => x.Line)
            .ToArray();
        if (lines.Length == 0)
            return PurchaseReceiptSettlementResult.Fail("Purchase invoice has no lines.");

        var invalid = lines.FirstOrDefault(x =>
            string.IsNullOrWhiteSpace(x.ICode)
            || x.PoRelNo is null
            || x.PoLineNo is null
            || string.IsNullOrWhiteSpace(x.PoNo)
            || StockLedgerPrecision.Quantity(x.StdQty) <= 0m
            || x.NetAmount < 0m);
        if (invalid is not null)
        {
            return PurchaseReceiptSettlementResult.Fail(
                $"Line {invalid.Line}: item, PO link, positive base quantity, and non-negative net amount are required for receipt settlement.");
        }

        var poKeys = lines
            .Select(x => new
            {
                PoNo = x.PoNo!.Trim(),
                Rel = x.PoRelNo!.Value,
                Line = x.PoLineNo!.Value,
                Item = x.ICode.Trim()
            })
            .Distinct()
            .ToArray();
        var poNos = poKeys.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var itemCodes = poKeys.Select(x => x.Item).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rels = poKeys.Select(x => x.Rel).Distinct().ToArray();
        var poLineNos = poKeys.Select(x => x.Line).Distinct().ToArray();

        var candidates = await (
            from fact in db.StockValuationFacts.AsNoTracking()
            join history in db.IvTrxHistories.AsNoTracking()
                on fact.InventoryHistoryId equals history.Id
            join posting in db.StockPostings.AsNoTracking()
                on new { fact.CompanyCode, fact.BranchCode, Id = fact.StockPostingId }
                equals new { posting.CompanyCode, posting.BranchCode, Id = posting.Id }
            where fact.CompanyCode == invoice.CompanyCode
                  && fact.BranchCode == invoice.BranchCode
                  && fact.Direction == 1
                  && fact.BaseQty > 0m
                  && fact.ValuationStatus == StockValuationStatuses.Valued
                  && fact.SourceDocumentType == IvTrxTypes.GoodsReceive
                  && history.TrxType == IvTrxTypes.GoodsReceive
                  && history.PoNo != null
                  && poNos.Contains(history.PoNo)
                  && history.PoRelNo != null
                  && rels.Contains(history.PoRelNo.Value)
                  && history.PoLineNo != null
                  && poLineNos.Contains(history.PoLineNo.Value)
                  && itemCodes.Contains(history.ICode)
                  && (posting.SealedAtUtc != null
                      || posting.Id == ownUnsealedReceiptId)
                  && !db.StockValuationFacts.Any(reversal =>
                      reversal.CompanyCode == fact.CompanyCode
                      && reversal.BranchCode == fact.BranchCode
                      && reversal.ReversesValuationFactId == fact.Id)
            select new ReceiptCandidate(
                fact.Id,
                fact.BaseQty,
                fact.UnitCost,
                fact.CostAmount,
                fact.BusinessDate,
                posting.PostingSequence,
                fact.InventoryHistoryId,
                history.BatchNo,
                history.PoNo!,
                history.PoRelNo!.Value,
                history.PoLineNo!.Value,
                history.ICode,
                history.BaseUnitPrices,
                history.ToStdUom,
                fact.StockPostingId))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return PurchaseReceiptSettlementResult.Fail("No eligible goods-receipt valuation facts match the invoice lines.");

        var factIds = candidates.Select(x => x.FactId).Distinct().ToArray();
        var priorSettlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == invoice.CompanyCode
                        && x.BranchCode == invoice.BranchCode
                        && factIds.Contains(x.ReceiptValuationFactId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var priorByFact = priorSettlements
            .GroupBy(x => x.ReceiptValuationFactId)
            .ToDictionary(x => x.Key, CalculateActiveTotals);

        var candidateMap = candidates
            .GroupBy(x => (PoNo: x.PoNo.Trim(), x.PoRelNo, x.PoLineNo, ItemCode: x.ItemCode.Trim()),
                StringTupleComparer.Instance)
            .ToDictionary(x => x.Key, x => x
                .OrderBy(y => y.BusinessDate)
                .ThenBy(y => y.PostingSequence)
                .ThenBy(y => y.FactId)
                .ToArray(), StringTupleComparer.Instance);

        var settlements = new List<PurchaseReceiptCostSettlement>();
        var factTotals = candidates.ToDictionary(
            x => x.FactId,
            x => priorByFact.GetValueOrDefault(x.FactId) ?? new PriorSettlementTotals(0m, 0m, 0m));
        var settlementRowsByItem = new List<SettlementWithUom>();

        foreach (var line in lines)
        {
            var key = (line.PoNo!.Trim(), line.PoRelNo!.Value, line.PoLineNo!.Value, line.ICode.Trim());
            if (!candidateMap.TryGetValue(key, out var lineCandidates))
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Line {line.Line}: no eligible GR valuation fact matches PO {line.PoNo}/{line.PoRelNo} line {line.PoLineNo} item {line.ICode}.");
            }

            var lineQty = StockLedgerPrecision.Quantity(line.StdQty);
            var remainingQty = lineQty;
            var actualAmount = StockLedgerPrecision.Money(line.NetAmount * invoice.CurrRate);
            var remainingActual = actualAmount;

            foreach (var candidate in lineCandidates)
            {
                var prior = factTotals[candidate.FactId];
                if (prior.SettledQty < -Epsilon
                    || prior.SettledQty > candidate.BaseQty + Epsilon)
                {
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Receipt valuation fact {candidate.FactId} has an invalid active settlement quantity.");
                }
                var availableQty = StockLedgerPrecision.Quantity(candidate.BaseQty - prior.SettledQty);
                if (availableQty <= Epsilon)
                    continue;

                if (candidate.CommercialUnitCost is null || candidate.CommercialUnitCost < 0m
                    || string.IsNullOrWhiteSpace(candidate.BaseUom))
                {
                    return PurchaseReceiptSettlementResult.Fail(
                        $"GR valuation fact {candidate.FactId} has incomplete immutable commercial cost evidence.");
                }

                var sliceQty = remainingQty <= availableQty + Epsilon
                    ? remainingQty
                    : availableQty;
                sliceQty = StockLedgerPrecision.Quantity(sliceQty);
                if (sliceQty <= Epsilon)
                    continue;

                var usesRemainingFact = sliceQty >= availableQty - Epsilon;
                var sourceCommercialAmount = StockLedgerPrecision.Money(
                    candidate.BaseQty * candidate.CommercialUnitCost.Value);
                var sourceValuationAmount = StockLedgerPrecision.Money(candidate.CostAmount);
                var commercialAmount = usesRemainingFact
                    ? StockLedgerPrecision.Money(sourceCommercialAmount - prior.CommercialAmount)
                    : StockLedgerPrecision.Money(candidate.CommercialUnitCost.Value * sliceQty);
                var valuationAmount = usesRemainingFact
                    ? StockLedgerPrecision.Money(sourceValuationAmount - prior.ValuationAmount)
                    : StockLedgerPrecision.Money(candidate.UnitCost * sliceQty);
                var actualSlice = remainingQty <= sliceQty + Epsilon
                    ? remainingActual
                    : StockLedgerPrecision.Money(actualAmount * sliceQty / lineQty);

                var settlement = new PurchaseReceiptCostSettlement
                {
                    CompanyCode = invoice.CompanyCode,
                    BranchCode = invoice.BranchCode,
                    PiDocNo = invoice.DocNo,
                    PiLineNo = line.Line,
                    PiCostingRevision = invoice.CostingRevision,
                    PoNo = candidate.PoNo.Trim(),
                    PoRelNo = candidate.PoRelNo,
                    PoLineNo = candidate.PoLineNo,
                    ItemCode = candidate.ItemCode.Trim(),
                    ReceiptValuationFactId = candidate.FactId,
                    ReceiptInventoryHistoryId = candidate.InventoryHistoryId,
                    ReceiptBatchNo = candidate.BatchNo,
                    SettledBaseQty = sliceQty,
                    ReceiptCommercialUnitCost = StockLedgerPrecision.Money(candidate.CommercialUnitCost.Value),
                    ReceiptCommercialBaseAmount = commercialAmount,
                    ReceiptValuationUnitCost = StockLedgerPrecision.Money(candidate.UnitCost),
                    ReceiptValuationBaseAmount = valuationAmount,
                    AllocatedActualBaseAmount = actualSlice,
                    CommercialVarianceAmount = StockLedgerPrecision.Money(actualSlice - commercialAmount),
                    ValuationVarianceAmount = StockLedgerPrecision.Money(actualSlice - valuationAmount),
                    StockPostingId = stockPostingId,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = Truncate(userId, 100)
                };
                settlements.Add(settlement);
                settlementRowsByItem.Add(new SettlementWithUom(settlement, candidate.BaseUom!.Trim()));
                db.PurchaseReceiptCostSettlements.Add(settlement);

                factTotals[candidate.FactId] = new PriorSettlementTotals(
                    StockLedgerPrecision.Quantity(prior.SettledQty + sliceQty),
                    StockLedgerPrecision.Money(prior.CommercialAmount + commercialAmount),
                    StockLedgerPrecision.Money(prior.ValuationAmount + valuationAmount));

                remainingQty = StockLedgerPrecision.Quantity(remainingQty - sliceQty);
                remainingActual = StockLedgerPrecision.Money(remainingActual - actualSlice);
                if (remainingQty <= Epsilon)
                    break;
            }

            if (remainingQty > Epsilon)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Line {line.Line}: quantity {lineQty:0.######} cannot be fully mapped to active eligible GR receipt quantity.");
            }
        }

        var adjustments = BuildAdjustments(
            invoice,
            costMethod,
            settlementRowsByItem,
            await LoadStatesAsync(db, invoice, settlementRowsByItem, costMethod, cancellationToken));

        return PurchaseReceiptSettlementResult.Success(settlements, adjustments);
    }

    public async Task<PurchaseReceiptSettlementResult> ReverseInvoiceAsync(
        AppDbContext db,
        PoInvoice invoice,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoice);
        if (stockPostingId <= 0)
            return PurchaseReceiptSettlementResult.Fail("A sealed stock posting identity is required for purchase reversal.");

        var settlementRows = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == invoice.CompanyCode
                        && x.BranchCode == invoice.BranchCode
                        && x.PiDocNo == invoice.DocNo
                        && x.PiCostingRevision == invoice.CostingRevision)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var settlementIds = settlementRows.Select(x => x.Id).ToHashSet();
        var settlementChildrenRows = settlementRows
            .Where(x => x.ReversesSettlementId is not null).ToArray();
        if (settlementChildrenRows.Any(x =>
                !settlementIds.Contains(x.ReversesSettlementId!.Value))
            || settlementChildrenRows.GroupBy(x => x.ReversesSettlementId)
                .Any(x => x.Count() != 1))
            return PurchaseReceiptSettlementResult.Fail(
                $"Invoice {invoice.DocNo} has invalid settlement reversal lineage.");
        var settlementChildren = settlementChildrenRows.ToDictionary(
            x => x.ReversesSettlementId!.Value);
        var settlementLeafByRoot = new Dictionary<long, long>();
        var activeSettlements = new List<PurchaseReceiptCostSettlement>();
        foreach (var root in settlementRows.Where(x =>
                     x.ReversesSettlementId is null))
        {
            var chain = BuildSettlementChain(root, settlementChildren);
            var quantity = SignedSettlementAmount(chain, x => x.SettledBaseQty);
            if (quantity == 0m)
            {
                if (chain.Count % 2 != 0
                    || SignedSettlementMoney(chain,
                        x => x.AllocatedActualBaseAmount) != 0m
                    || SignedSettlementMoney(chain,
                        x => x.ReceiptCommercialBaseAmount) != 0m
                    || SignedSettlementMoney(chain,
                        x => x.ReceiptValuationBaseAmount) != 0m)
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Invoice {invoice.DocNo} has an unbalanced reversed settlement generation.");
                continue;
            }
            if (quantity != root.SettledBaseQty
                || SignedSettlementMoney(chain,
                    x => x.AllocatedActualBaseAmount)
                    != root.AllocatedActualBaseAmount
                || SignedSettlementMoney(chain,
                    x => x.ReceiptCommercialBaseAmount)
                    != root.ReceiptCommercialBaseAmount
                || SignedSettlementMoney(chain,
                    x => x.ReceiptValuationBaseAmount)
                    != root.ReceiptValuationBaseAmount
                || chain.Count % 2 == 0)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Invoice {invoice.DocNo} has a partially reversed settlement generation.");
            activeSettlements.Add(root);
            settlementLeafByRoot.Add(root.Id, chain[^1].Id);
        }
        var originals = activeSettlements.ToArray();
        if (originals.Length == 0)
            return PurchaseReceiptSettlementResult.Fail(
                $"No active receipt settlements were found for invoice {invoice.DocNo} revision {invoice.CostingRevision}.");

        var adjustmentRoots = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == invoice.CompanyCode
                        && x.BranchCode == invoice.BranchCode
                        && x.SourceDocumentType == "PO_INVOICE"
                        && x.SourceDocumentNo == invoice.DocNo
                        && x.SourceCostingRevision == invoice.CostingRevision
                        && x.ReversesAdjustmentId == null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var allAdjustments = adjustmentRoots.ToList();
        var pendingAdjustmentIds = adjustmentRoots.Select(x => x.Id).ToArray();
        while (pendingAdjustmentIds.Length > 0)
        {
            var children = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(x => x.ReversesAdjustmentId != null
                    && pendingAdjustmentIds.Contains(x.ReversesAdjustmentId.Value))
                .OrderBy(x => x.Id).ToListAsync(cancellationToken);
            if (children.Count == 0)
                break;
            var fresh = children.Where(x => allAdjustments.All(a => a.Id != x.Id))
                .ToArray();
            if (fresh.Length != children.Count)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Invoice {invoice.DocNo} has cyclic adjustment reversal lineage.");
            allAdjustments.AddRange(fresh);
            pendingAdjustmentIds = fresh.Select(x => x.Id).ToArray();
        }
        var adjustmentChildrenRows = allAdjustments
            .Where(x => x.ReversesAdjustmentId is not null).ToArray();
        if (adjustmentChildrenRows.GroupBy(x => x.ReversesAdjustmentId)
            .Any(x => x.Count() != 1))
            return PurchaseReceiptSettlementResult.Fail(
                $"Invoice {invoice.DocNo} has invalid adjustment reversal lineage.");
        var adjustmentChildren = adjustmentChildrenRows.ToDictionary(
            x => x.ReversesAdjustmentId!.Value);
        var adjustmentLeafByRoot = new Dictionary<long, long>();
        var activeAdjustments = new List<PurchaseCostAdjustment>();
        foreach (var root in adjustmentRoots)
        {
            var chain = BuildAdjustmentChain(root, adjustmentChildren);
            var quantity = SignedAdjustmentQuantity(chain);
            if (quantity == 0m)
            {
                if (chain.Count % 2 != 0
                    || SignedAdjustmentMoney(chain,
                        x => x.ActualBaseAmount) != 0m
                    || StockLedgerPrecision.Money(chain.Sum(x =>
                        x.TotalAdjustmentAmount)) != 0m
                    || StockLedgerPrecision.Money(chain.Sum(x =>
                        x.InventoryAdjustmentAmount)) != 0m
                    || StockLedgerPrecision.Money(chain.Sum(x =>
                        x.ConsumedVarianceAmount)) != 0m)
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Invoice {invoice.DocNo} has an unbalanced reversed purchase-cost adjustment generation.");
                continue;
            }
            if (chain.Count > 1 && root.InventoryAdjustmentAmount != 0m)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Invoice {invoice.DocNo} has a credit-note value adjustment that needs full inventory replay before PI rollback.");
            if (quantity != root.BaseQty
                || SignedAdjustmentMoney(chain,
                    x => x.ActualBaseAmount) != root.ActualBaseAmount
                || SignedAdjustmentMoney(chain,
                    x => x.ReferenceBaseAmount) != root.ReferenceBaseAmount
                || chain.Any(x => x.CommercialReferenceAmount.HasValue
                    != root.CommercialReferenceAmount.HasValue)
                || root.CommercialReferenceAmount is decimal commercialReference
                    && SignedAdjustmentMoney(chain,
                        x => x.CommercialReferenceAmount ?? 0m)
                        != commercialReference
                || StockLedgerPrecision.Money(chain.Sum(x =>
                    x.TotalAdjustmentAmount)) != root.TotalAdjustmentAmount
                || StockLedgerPrecision.Money(chain.Sum(x =>
                    x.InventoryAdjustmentAmount)) != root.InventoryAdjustmentAmount
                || StockLedgerPrecision.Money(chain.Sum(x =>
                    x.ConsumedVarianceAmount)) != root.ConsumedVarianceAmount
                || chain.Count % 2 == 0)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Invoice {invoice.DocNo} has a partially restored purchase-cost adjustment generation.");
            activeAdjustments.Add(root);
            adjustmentLeafByRoot.Add(root.Id, chain[^1].Id);
        }
        var adjustments = activeAdjustments.ToArray();

        // A receipt remap can leave older roots in the audit trail. Before
        // reversing the active PI generation, require its cost adjustments to
        // cover the same active slices; otherwise a rollback would remove
        // settlement quantity while leaving inventory/consumed variance live.
        if (adjustments.Length != originals.Length
            || originals.GroupBy(x => new
                {
                    Key = CostingKey(x.PiLineNo, x.ItemCode,
                        x.PoNo, x.PoRelNo, x.PoLineNo).ToUpperInvariant(),
                    Qty = x.SettledBaseQty,
                    Actual = x.AllocatedActualBaseAmount
                })
                .Any(group => adjustments.Count(x => string.Equals(
                    CostingKey(x.SourceDocumentLine, x.ItemCode, x.PoNo,
                        x.PoRelNo, x.PoLineNo), group.Key.Key,
                    StringComparison.OrdinalIgnoreCase)
                    && x.BaseQty == group.Key.Qty
                    && x.ActualBaseAmount == group.Key.Actual)
                    != group.Count()))
            return PurchaseReceiptSettlementResult.Fail(
                $"Invoice {invoice.DocNo} has incomplete active purchase-cost adjustment evidence.");

        var factIds = adjustments.Where(x => x.InventoryAdjustmentFactId is not null)
            .Select(x => x.InventoryAdjustmentFactId!.Value)
            .Distinct()
            .ToArray();
        var baseUoms = factIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => factIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BaseUom, cancellationToken);

        var reversalSettlements = new List<PurchaseReceiptCostSettlement>(originals.Length);
        foreach (var original in originals)
        {
            var reversal = new PurchaseReceiptCostSettlement
            {
                CompanyCode = original.CompanyCode,
                BranchCode = original.BranchCode,
                PiDocNo = original.PiDocNo,
                PiLineNo = original.PiLineNo,
                PiCostingRevision = original.PiCostingRevision,
                PoNo = original.PoNo,
                PoRelNo = original.PoRelNo,
                PoLineNo = original.PoLineNo,
                ItemCode = original.ItemCode,
                ReceiptValuationFactId = original.ReceiptValuationFactId,
                ReceiptInventoryHistoryId = original.ReceiptInventoryHistoryId,
                ReceiptBatchNo = original.ReceiptBatchNo,
                SettledBaseQty = original.SettledBaseQty,
                ReceiptCommercialUnitCost = original.ReceiptCommercialUnitCost,
                ReceiptCommercialBaseAmount = original.ReceiptCommercialBaseAmount,
                ReceiptValuationUnitCost = original.ReceiptValuationUnitCost,
                ReceiptValuationBaseAmount = original.ReceiptValuationBaseAmount,
                AllocatedActualBaseAmount = original.AllocatedActualBaseAmount,
                CommercialVarianceAmount = original.CommercialVarianceAmount,
                ValuationVarianceAmount = original.ValuationVarianceAmount,
                StockPostingId = stockPostingId,
                ReversesSettlementId = settlementLeafByRoot[original.Id],
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = Truncate(userId, 100)
            };
            reversalSettlements.Add(reversal);
            db.PurchaseReceiptCostSettlements.Add(reversal);
        }

        var reversalRequests = new List<PurchaseCostAdjustmentRequest>(adjustments.Length);
        var splitByLine = new Dictionary<int, int>();
        foreach (var original in adjustments)
        {
            var signedInventory = StockLedgerPrecision.Money(original.InventoryAdjustmentAmount);
            StockValueAdjustmentRequest? valueAdjustment = null;
            if (signedInventory != 0m)
            {
                if (original.InventoryAdjustmentFactId is not long factId
                    || !baseUoms.TryGetValue(factId, out var baseUom)
                    || string.IsNullOrWhiteSpace(baseUom))
                {
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Inventory adjustment {original.Id} is missing its immutable base-UOM evidence.");
                }

                var split = splitByLine.GetValueOrDefault(original.SourceDocumentLine);
                splitByLine[original.SourceDocumentLine] = split + 1;
                valueAdjustment = new StockValueAdjustmentRequest(
                    original.SourceDocumentLine,
                    split,
                    original.ItemCode,
                    baseUom,
                    Math.Abs(signedInventory),
                    signedInventory > 0m ? -1 : 1,
                    signedInventory > 0m ? "PURCHASE_PRICE_VARIANCE_OUT" : "PURCHASE_PRICE_VARIANCE_IN",
                    StockValuationSources.PurchasePriceVariance,
                    SourceDocumentLine: original.SourceDocumentLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    OriginalValuationFactId: original.InventoryAdjustmentFactId,
                    ReversesValuationFactId: original.InventoryAdjustmentFactId);
            }

            reversalRequests.Add(new PurchaseCostAdjustmentRequest(
                AdjustmentType: original.AdjustmentType,
                SourceDocumentType: original.SourceDocumentType,
                SourceDocumentNo: original.SourceDocumentNo,
                SourceDocumentLine: original.SourceDocumentLine,
                SourceCostingRevision: original.SourceCostingRevision,
                ItemCode: original.ItemCode,
                CostMethod: original.CostMethod,
                BaseQty: original.BaseQty,
                ActualBaseAmount: original.ActualBaseAmount,
                ReferenceBaseAmount: original.ReferenceBaseAmount,
                CommercialReferenceAmount: original.CommercialReferenceAmount,
                TotalAdjustmentAmount: StockLedgerPrecision.Money(-original.TotalAdjustmentAmount),
                InventoryAdjustmentAmount: StockLedgerPrecision.Money(-signedInventory),
                ConsumedVarianceAmount: StockLedgerPrecision.Money(-original.ConsumedVarianceAmount),
                ValueAdjustment: valueAdjustment,
                PoNo: original.PoNo,
                PoRelNo: original.PoRelNo,
                PoLineNo: original.PoLineNo,
                ReversesAdjustmentId: adjustmentLeafByRoot[original.Id]));
        }

        return PurchaseReceiptSettlementResult.Success(reversalSettlements, reversalRequests);
    }

    public async Task<PurchaseReceiptSettlementResult> ReverseCreditNoteAsync(
        AppDbContext db,
        PoInvoice creditNote,
        PoInvoice referencedInvoice,
        string costMethod,
        long stockPostingId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(creditNote);
        ArgumentNullException.ThrowIfNull(referencedInvoice);

        if (!string.Equals(creditNote.Type, PoInvoiceTypes.CreditNote, StringComparison.OrdinalIgnoreCase))
            return PurchaseReceiptSettlementResult.Fail("Only purchase credit notes reverse receipt settlements.");
        if (!string.Equals(referencedInvoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(referencedInvoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            return PurchaseReceiptSettlementResult.Fail("The referenced purchase invoice must be POSTED.");
        if (!string.Equals(creditNote.InvNo?.Trim(), referencedInvoice.DocNo, StringComparison.OrdinalIgnoreCase))
            return PurchaseReceiptSettlementResult.Fail("Credit note reference does not match the posted purchase invoice.");
        if (stockPostingId <= 0)
            return PurchaseReceiptSettlementResult.Fail("A sealed stock posting identity is required for credit-note settlement reversal.");
        if (string.IsNullOrWhiteSpace(costMethod))
            return PurchaseReceiptSettlementResult.Fail("An active inventory cost method is required for credit-note settlement reversal.");

        var lines = creditNote.Details
            .OrderBy(x => x.Line)
            .ToArray();
        if (lines.Length == 0)
            return PurchaseReceiptSettlementResult.Fail("Purchase credit note has no lines.");
        if (lines.Any(x => x.ReferencedInvoiceLineNo is null || StockLedgerPrecision.Quantity(x.StdQty) <= 0m))
            return PurchaseReceiptSettlementResult.Fail("Every purchase credit-note line requires an exact referenced invoice line and positive base quantity.");
        if (lines.GroupBy(x => x.ReferencedInvoiceLineNo!.Value).Any(x => x.Count() > 1))
            return PurchaseReceiptSettlementResult.Fail("A credit note may reference each purchase-invoice line only once per posting.");

        var referencedLineNos = lines.Select(x => x.ReferencedInvoiceLineNo!.Value).ToArray();
        var missingLine = lines.FirstOrDefault(x => referencedInvoice.Details.All(y => y.Line != x.ReferencedInvoiceLineNo!.Value));
        if (missingLine is not null)
            return PurchaseReceiptSettlementResult.Fail(
                $"Referenced purchase invoice line {missingLine.ReferencedInvoiceLineNo} was not found.");

        var settlementRows = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == referencedInvoice.CompanyCode
                        && x.BranchCode == referencedInvoice.BranchCode
                        && x.PiDocNo == referencedInvoice.DocNo
                        && x.PiCostingRevision == referencedInvoice.CostingRevision
                        && referencedLineNos.Contains(x.PiLineNo))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var originals = settlementRows.Where(x => x.ReversesSettlementId is null).ToList();
        if (originals.Count == 0)
            return PurchaseReceiptSettlementResult.Fail(
                $"No receipt settlements were found for purchase invoice {referencedInvoice.DocNo}.");

        var duplicateSettlementParents = settlementRows
            .Where(x => x.ReversesSettlementId is not null)
            .GroupBy(x => x.ReversesSettlementId!.Value)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateSettlementParents is not null)
            return PurchaseReceiptSettlementResult.Fail(
                $"Receipt settlement {duplicateSettlementParents.Key} has more than one reversal child.");
        var settlementChildren = settlementRows
            .Where(x => x.ReversesSettlementId is not null)
            .ToDictionary(x => x.ReversesSettlementId!.Value);

        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == referencedInvoice.CompanyCode
                        && x.BranchCode == referencedInvoice.BranchCode
                        && x.SourceDocumentType == "PO_INVOICE"
                        && x.SourceDocumentNo == referencedInvoice.DocNo
                        && x.SourceCostingRevision == referencedInvoice.CostingRevision
                        && x.ReversesAdjustmentId == null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (adjustments.Count == 0)
            return PurchaseReceiptSettlementResult.Fail(
                $"No purchase-cost adjustment evidence was found for invoice {referencedInvoice.DocNo}.");

        var allAdjustments = adjustments.ToList();
        var pendingAdjustmentIds = adjustments.Select(x => x.Id).ToArray();
        while (pendingAdjustmentIds.Length > 0)
        {
            var children = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(x => x.ReversesAdjustmentId != null
                            && pendingAdjustmentIds.Contains(x.ReversesAdjustmentId.Value))
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);
            if (children.Count == 0)
                break;

            var duplicateAdjustmentParents = children
                .GroupBy(x => x.ReversesAdjustmentId!.Value)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicateAdjustmentParents is not null)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Purchase-cost adjustment {duplicateAdjustmentParents.Key} has more than one reversal child.");

            var newChildren = children.Where(x => allAdjustments.All(y => y.Id != x.Id)).ToList();
            if (newChildren.Count == 0)
                break;
            allAdjustments.AddRange(newChildren);
            pendingAdjustmentIds = newChildren.Select(x => x.Id).ToArray();
        }

        var method = costMethod.Trim().ToUpperInvariant();
        if (allAdjustments.Any(x => !string.Equals(x.CostMethod, method, StringComparison.OrdinalIgnoreCase)))
        {
            return PurchaseReceiptSettlementResult.Fail(
                "The active cost method does not match the referenced invoice adjustment basis.");
        }

        var adjustmentByKey = adjustments
            .GroupBy(x => CostingKey(x.SourceDocumentLine, x.ItemCode, x.PoNo, x.PoRelNo, x.PoLineNo))
            .ToDictionary(x => x.Key, x => new Queue<PurchaseCostAdjustment>(x.OrderBy(y => y.Id)),
                StringComparer.OrdinalIgnoreCase);
        var adjustmentForSettlement = new Dictionary<long, PurchaseCostAdjustment>();
        foreach (var settlement in originals)
        {
            var key = CostingKey(settlement.PiLineNo, settlement.ItemCode,
                settlement.PoNo, settlement.PoRelNo, settlement.PoLineNo);
            if (!adjustmentByKey.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"No exact purchase-cost adjustment evidence matches receipt settlement {settlement.Id}.");
            }

            adjustmentForSettlement[settlement.Id] = queue.Dequeue();
        }

        var adjustmentChildren = allAdjustments
            .Where(x => x.ReversesAdjustmentId is not null)
            .GroupBy(x => x.ReversesAdjustmentId!.Value)
            .ToDictionary(x => x.Key, x => x.Single());
        var activeOriginals = new List<ActiveSettlementSlice>(originals.Count);
        foreach (var original in originals)
        {
            var settlementChain = BuildSettlementChain(original, settlementChildren);
            var activeQty = SignedSettlementAmount(settlementChain, x => x.SettledBaseQty);
            if (activeQty <= Epsilon)
                continue;
            if (settlementChain.Count % 2 == 0)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Receipt settlement {original.Id} is partially reversed and cannot be reused safely.");
            }

            var adjustmentRoot = adjustmentForSettlement[original.Id];
            var adjustmentChain = BuildAdjustmentChain(adjustmentRoot, adjustmentChildren);
            if (adjustmentChain.Count != settlementChain.Count)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Receipt settlement {original.Id} and its adjustment evidence have different reversal histories.");
            }

            var settlementLeaf = settlementChain[^1];
            var adjustmentLeaf = adjustmentChain[^1];
            activeOriginals.Add(new ActiveSettlementSlice(
                settlementLeaf,
                settlementLeaf.SettledBaseQty,
                StockLedgerPrecision.Money(settlementLeaf.ReceiptCommercialBaseAmount),
                StockLedgerPrecision.Money(settlementLeaf.ReceiptValuationBaseAmount),
                StockLedgerPrecision.Money(settlementLeaf.AllocatedActualBaseAmount),
                adjustmentLeaf));
        }

        var factIds = allAdjustments.Where(x => x.InventoryAdjustmentFactId is not null)
            .Select(x => x.InventoryAdjustmentFactId!.Value)
            .Distinct()
            .ToArray();
        var baseUoms = factIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => factIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BaseUom, cancellationToken);

        var itemCodes = activeOriginals.Select(x => x.Original.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = await db.StockCostStates
            .Where(x => x.CompanyCode == referencedInvoice.CompanyCode
                        && x.BranchCode == referencedInvoice.BranchCode
                        && x.CostMethod == method
                        && itemCodes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);
        var runningValues = states.ToDictionary(
            x => x.ItemCode,
            x => StockLedgerPrecision.Money(x.InventoryValue),
            StringComparer.OrdinalIgnoreCase);

        var reversalSettlements = new List<PurchaseReceiptCostSettlement>();
        var reversalRequests = new List<PurchaseCostAdjustmentRequest>();
        var activeByLine = activeOriginals
            .GroupBy(x => x.Original.PiLineNo)
            .ToDictionary(x => x.Key, x => new Queue<ActiveSettlementSlice>(x.OrderBy(y => y.Original.Id)));
        var splitByLine = new Dictionary<int, int>();

        foreach (var creditLine in lines)
        {
            var referencedLine = referencedInvoice.Details.First(x =>
                x.Line == creditLine.ReferencedInvoiceLineNo!.Value);
            var remainingQty = StockLedgerPrecision.Quantity(creditLine.StdQty);
            if (!activeByLine.TryGetValue(referencedLine.Line, out var candidates))
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Invoice line {referencedLine.Line} has no active receipt settlement quantity remaining.");
            }

            while (remainingQty > Epsilon && candidates.Count > 0)
            {
                var candidate = candidates.Peek();
                var original = candidate.Original;
                var sliceQty = StockLedgerPrecision.Quantity(Math.Min(remainingQty, candidate.RemainingQty));
                if (sliceQty <= Epsilon)
                {
                    candidates.Dequeue();
                    continue;
                }

                var ratio = sliceQty / original.SettledBaseQty;
                var usesWholeCandidate = sliceQty >= candidate.RemainingQty - Epsilon;
                var commercialAmount = usesWholeCandidate
                    ? candidate.RemainingCommercialAmount
                    : StockLedgerPrecision.Money(candidate.RemainingCommercialAmount * ratio / (candidate.RemainingQty / original.SettledBaseQty));
                var valuationAmount = usesWholeCandidate
                    ? candidate.RemainingValuationAmount
                    : StockLedgerPrecision.Money(candidate.RemainingValuationAmount * ratio / (candidate.RemainingQty / original.SettledBaseQty));
                var actualAmount = usesWholeCandidate
                    ? candidate.RemainingActualAmount
                    : StockLedgerPrecision.Money(candidate.RemainingActualAmount * ratio / (candidate.RemainingQty / original.SettledBaseQty));

                var settlement = new PurchaseReceiptCostSettlement
                {
                    CompanyCode = referencedInvoice.CompanyCode,
                    BranchCode = referencedInvoice.BranchCode,
                    PiDocNo = referencedInvoice.DocNo,
                    PiLineNo = original.PiLineNo,
                    PiCostingRevision = referencedInvoice.CostingRevision,
                    PoNo = original.PoNo,
                    PoRelNo = original.PoRelNo,
                    PoLineNo = original.PoLineNo,
                    ItemCode = original.ItemCode,
                    ReceiptValuationFactId = original.ReceiptValuationFactId,
                    ReceiptInventoryHistoryId = original.ReceiptInventoryHistoryId,
                    ReceiptBatchNo = original.ReceiptBatchNo,
                    SettledBaseQty = sliceQty,
                    ReceiptCommercialUnitCost = original.ReceiptCommercialUnitCost,
                    ReceiptCommercialBaseAmount = commercialAmount,
                    ReceiptValuationUnitCost = original.ReceiptValuationUnitCost,
                    ReceiptValuationBaseAmount = valuationAmount,
                    AllocatedActualBaseAmount = actualAmount,
                    CommercialVarianceAmount = StockLedgerPrecision.Money(
                        actualAmount - commercialAmount),
                    ValuationVarianceAmount = StockLedgerPrecision.Money(
                        actualAmount - valuationAmount),
                    StockPostingId = stockPostingId,
                    ReversesSettlementId = original.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = Truncate(userId, 100)
                };
                reversalSettlements.Add(settlement);
                db.PurchaseReceiptCostSettlements.Add(settlement);

                var adjustment = candidate.Adjustment;

                var reversalTotal = StockLedgerPrecision.Money(
                    -adjustment.TotalAdjustmentAmount * ratio);
                var desiredInventory = StockLedgerPrecision.Money(
                    -adjustment.InventoryAdjustmentAmount * ratio);
                var runningValue = runningValues.GetValueOrDefault(adjustment.ItemCode);
                var appliedInventory = desiredInventory < 0m
                    ? Math.Max(-runningValue, desiredInventory)
                    : desiredInventory;
                appliedInventory = StockLedgerPrecision.Money(appliedInventory);
                var consumedVariance = StockLedgerPrecision.Money(reversalTotal - appliedInventory);
                runningValues[adjustment.ItemCode] = StockLedgerPrecision.Money(
                    runningValue + appliedInventory);

                StockValueAdjustmentRequest? valueAdjustment = null;
                if (appliedInventory != 0m)
                {
                    if (adjustment.InventoryAdjustmentFactId is not long factId
                        || !baseUoms.TryGetValue(factId, out var baseUom)
                        || string.IsNullOrWhiteSpace(baseUom))
                    {
                        return PurchaseReceiptSettlementResult.Fail(
                            $"Purchase-cost adjustment {adjustment.Id} is missing immutable base-UOM evidence.");
                    }

                    var split = splitByLine.GetValueOrDefault(creditLine.Line);
                    splitByLine[creditLine.Line] = split + 1;
                    valueAdjustment = new StockValueAdjustmentRequest(
                        creditLine.Line,
                        split,
                        adjustment.ItemCode,
                        baseUom,
                        Math.Abs(appliedInventory),
                        appliedInventory > 0m ? 1 : -1,
                        appliedInventory > 0m
                            ? "PURCHASE_PRICE_VARIANCE_IN"
                            : "PURCHASE_PRICE_VARIANCE_OUT",
                        StockValuationSources.PurchasePriceVariance,
                        SourceDocumentLine: creditLine.Line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OriginalValuationFactId: adjustment.InventoryAdjustmentFactId,
                        ReversesValuationFactId: adjustment.InventoryAdjustmentFactId);
                }

                reversalRequests.Add(new PurchaseCostAdjustmentRequest(
                    AdjustmentType: "PURCHASE_CN_PRICE_ADJUSTMENT",
                    SourceDocumentType: "PO_INVOICE_CN",
                    SourceDocumentNo: creditNote.DocNo,
                    SourceDocumentLine: creditLine.Line,
                    SourceCostingRevision: creditNote.CostingRevision,
                    ItemCode: adjustment.ItemCode,
                    CostMethod: adjustment.CostMethod,
                    BaseQty: sliceQty,
                    ActualBaseAmount: StockLedgerPrecision.Money(adjustment.ActualBaseAmount * ratio),
                    ReferenceBaseAmount: StockLedgerPrecision.Money(adjustment.ReferenceBaseAmount * ratio),
                    CommercialReferenceAmount: adjustment.CommercialReferenceAmount is null
                        ? null
                        : StockLedgerPrecision.Money(adjustment.CommercialReferenceAmount.Value * ratio),
                    TotalAdjustmentAmount: reversalTotal,
                    InventoryAdjustmentAmount: appliedInventory,
                    ConsumedVarianceAmount: consumedVariance,
                    ValueAdjustment: valueAdjustment,
                    PoNo: adjustment.PoNo,
                    PoRelNo: adjustment.PoRelNo,
                    PoLineNo: adjustment.PoLineNo,
                    ReversesAdjustmentId: adjustment.Id));

                remainingQty = StockLedgerPrecision.Quantity(remainingQty - sliceQty);
                candidate.Consume(sliceQty, commercialAmount, valuationAmount, actualAmount);
                if (candidate.RemainingQty <= Epsilon)
                    candidates.Dequeue();
            }

            if (remainingQty > Epsilon)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Credit-note line {creditLine.Line}: quantity {creditLine.StdQty:0.######} exceeds the active settlement quantity on invoice line {referencedLine.Line}.");
            }
        }

        return PurchaseReceiptSettlementResult.Success(reversalSettlements, reversalRequests);
    }

    public async Task<PurchaseReceiptSettlementResult> RollbackCreditNoteAsync(
        AppDbContext db,
        PoInvoice creditNote,
        long primaryStockPostingId,
        long rollbackStockPostingId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(creditNote);
        if (primaryStockPostingId <= 0 || rollbackStockPostingId <= 0)
            return PurchaseReceiptSettlementResult.Fail("Both credit-note posting identities are required for rollback.");

        var originalSettlementRows = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == creditNote.CompanyCode
                        && x.BranchCode == creditNote.BranchCode
                        && x.StockPostingId == primaryStockPostingId
                        && x.ReversesSettlementId != null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var originalAdjustmentRows = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == creditNote.CompanyCode
                        && x.BranchCode == creditNote.BranchCode
                        && x.StockPostingId == primaryStockPostingId
                        && x.ReversesAdjustmentId != null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var activeGeneration = await ResolveCreditNoteCostGenerationAsync(
            db, creditNote, primaryStockPostingId, originalSettlementRows,
            originalAdjustmentRows, cancellationToken);
        if (activeGeneration.Error is not null)
            return PurchaseReceiptSettlementResult.Fail(activeGeneration.Error);
        var settlementRows = activeGeneration.Settlements;
        var adjustmentRows = activeGeneration.Adjustments;
        if (settlementRows.Count == 0 && adjustmentRows.Count == 0)
            return PurchaseReceiptSettlementResult.Fail(
                $"No credit-note costing rows were found for posting {primaryStockPostingId}.");
        if (settlementRows.Count != adjustmentRows.Count
            || settlementRows.Any(x => x.PiDocNo != creditNote.InvNo)
            || adjustmentRows.Any(x =>
                x.SourceDocumentType != "PO_INVOICE_CN"
                || x.SourceDocumentNo != creditNote.DocNo
                || x.SourceCostingRevision != creditNote.CostingRevision))
            return PurchaseReceiptSettlementResult.Fail(
                $"Credit note {creditNote.DocNo} has incomplete or mismatched costing evidence.");
        var creditLines = creditNote.Details.ToArray();
        if (creditLines.Length == 0
            || creditLines.Any(line => line.ReferencedInvoiceLineNo is null
                || StockLedgerPrecision.Quantity(line.StdQty) <= 0m)
            || creditLines.GroupBy(line => line.Line).Any(group => group.Count() != 1)
            || creditLines.GroupBy(line => line.ReferencedInvoiceLineNo)
                .Any(group => group.Count() != 1)
            || creditLines.Any(line =>
                StockLedgerPrecision.Quantity(settlementRows
                    .Where(row => row.PiLineNo == line.ReferencedInvoiceLineNo
                        && row.ItemCode == line.ICode
                        && row.PoNo == line.PoNo
                        && row.PoRelNo == line.PoRelNo
                        && row.PoLineNo == line.PoLineNo)
                    .Sum(row => row.SettledBaseQty))
                    != StockLedgerPrecision.Quantity(line.StdQty)))
            return PurchaseReceiptSettlementResult.Fail(
                $"Credit note {creditNote.DocNo} has incomplete or mismatched costing evidence.");
        var unmatchedAdjustments = adjustmentRows.ToList();
        foreach (var settlement in settlementRows)
        {
            var matchingLine = creditLines.FirstOrDefault(line =>
                line.ReferencedInvoiceLineNo == settlement.PiLineNo
                && line.ICode == settlement.ItemCode
                && line.PoNo == settlement.PoNo
                && line.PoRelNo == settlement.PoRelNo
                && line.PoLineNo == settlement.PoLineNo);
            var adjustment = matchingLine is null ? null : unmatchedAdjustments
                .FirstOrDefault(row => row.SourceDocumentLine == matchingLine.Line
                    && row.ItemCode == settlement.ItemCode
                    && row.PoNo == settlement.PoNo
                    && row.PoRelNo == settlement.PoRelNo
                    && row.PoLineNo == settlement.PoLineNo
                    && StockLedgerPrecision.Quantity(row.BaseQty)
                        == StockLedgerPrecision.Quantity(settlement.SettledBaseQty)
                    && StockLedgerPrecision.Money(row.ActualBaseAmount)
                        == StockLedgerPrecision.Money(settlement.AllocatedActualBaseAmount));
            if (adjustment is null)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Credit note {creditNote.DocNo} has incomplete or mismatched costing evidence.");
            unmatchedAdjustments.Remove(adjustment);
        }
        var settlementIds = settlementRows.Select(x => x.Id).ToArray();
        var adjustmentIds = adjustmentRows.Select(x => x.Id).ToArray();
        if (await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(x =>
                x.ReversesSettlementId != null
                && settlementIds.Contains(x.ReversesSettlementId.Value),
                cancellationToken)
            || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(x =>
                x.ReversesAdjustmentId != null
                && adjustmentIds.Contains(x.ReversesAdjustmentId.Value),
                cancellationToken))
            return PurchaseReceiptSettlementResult.Fail(
                $"Credit note {creditNote.DocNo} original costing generation was already reversed; active-generation lineage is required.");

        var factIds = adjustmentRows.Where(x => x.InventoryAdjustmentFactId is not null)
            .Select(x => x.InventoryAdjustmentFactId!.Value)
            .Distinct()
            .ToArray();
        var baseUoms = factIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => factIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BaseUom, cancellationToken);

        var method = adjustmentRows.Select(x => x.CostMethod)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? StockCostMethods.MovingAverage;
        var itemCodes = adjustmentRows.Select(x => x.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = await db.StockCostStates
            .Where(x => x.CompanyCode == creditNote.CompanyCode
                        && x.BranchCode == creditNote.BranchCode
                        && x.CostMethod == method
                        && itemCodes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);
        var runningValues = states.ToDictionary(
            x => x.ItemCode,
            x => StockLedgerPrecision.Money(x.InventoryValue),
            StringComparer.OrdinalIgnoreCase);

        var reversalSettlements = new List<PurchaseReceiptCostSettlement>(settlementRows.Count);
        foreach (var original in settlementRows)
        {
            var reversal = new PurchaseReceiptCostSettlement
            {
                CompanyCode = original.CompanyCode,
                BranchCode = original.BranchCode,
                PiDocNo = original.PiDocNo,
                PiLineNo = original.PiLineNo,
                PiCostingRevision = original.PiCostingRevision,
                PoNo = original.PoNo,
                PoRelNo = original.PoRelNo,
                PoLineNo = original.PoLineNo,
                ItemCode = original.ItemCode,
                ReceiptValuationFactId = original.ReceiptValuationFactId,
                ReceiptInventoryHistoryId = original.ReceiptInventoryHistoryId,
                ReceiptBatchNo = original.ReceiptBatchNo,
                SettledBaseQty = original.SettledBaseQty,
                ReceiptCommercialUnitCost = original.ReceiptCommercialUnitCost,
                ReceiptCommercialBaseAmount = original.ReceiptCommercialBaseAmount,
                ReceiptValuationUnitCost = original.ReceiptValuationUnitCost,
                ReceiptValuationBaseAmount = original.ReceiptValuationBaseAmount,
                AllocatedActualBaseAmount = original.AllocatedActualBaseAmount,
                CommercialVarianceAmount = original.CommercialVarianceAmount,
                ValuationVarianceAmount = original.ValuationVarianceAmount,
                StockPostingId = rollbackStockPostingId,
                ReversesSettlementId = original.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = Truncate(userId, 100)
            };
            reversalSettlements.Add(reversal);
            db.PurchaseReceiptCostSettlements.Add(reversal);
        }

        var reversalRequests = new List<PurchaseCostAdjustmentRequest>(adjustmentRows.Count);
        var splitByLine = new Dictionary<int, int>();
        foreach (var original in adjustmentRows)
        {
            var reversalTotal = StockLedgerPrecision.Money(-original.TotalAdjustmentAmount);
            var desiredInventory = StockLedgerPrecision.Money(-original.InventoryAdjustmentAmount);
            var runningValue = runningValues.GetValueOrDefault(original.ItemCode);
            var appliedInventory = desiredInventory < 0m
                ? Math.Max(-runningValue, desiredInventory)
                : desiredInventory;
            appliedInventory = StockLedgerPrecision.Money(appliedInventory);
            runningValues[original.ItemCode] = StockLedgerPrecision.Money(
                runningValue + appliedInventory);
            var consumedVariance = StockLedgerPrecision.Money(reversalTotal - appliedInventory);

            StockValueAdjustmentRequest? valueAdjustment = null;
            if (appliedInventory != 0m)
            {
                if (original.InventoryAdjustmentFactId is not long factId
                    || !baseUoms.TryGetValue(factId, out var baseUom)
                    || string.IsNullOrWhiteSpace(baseUom))
                {
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Credit-note adjustment {original.Id} is missing immutable base-UOM evidence.");
                }

                var split = splitByLine.GetValueOrDefault(original.SourceDocumentLine);
                splitByLine[original.SourceDocumentLine] = split + 1;
                valueAdjustment = new StockValueAdjustmentRequest(
                    original.SourceDocumentLine,
                    split,
                    original.ItemCode,
                    baseUom,
                    Math.Abs(appliedInventory),
                    appliedInventory > 0m ? 1 : -1,
                    appliedInventory > 0m
                        ? "PURCHASE_PRICE_VARIANCE_IN"
                        : "PURCHASE_PRICE_VARIANCE_OUT",
                    StockValuationSources.PurchasePriceVariance,
                    SourceDocumentLine: original.SourceDocumentLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    OriginalValuationFactId: original.InventoryAdjustmentFactId,
                    ReversesValuationFactId: original.InventoryAdjustmentFactId);
            }

            reversalRequests.Add(new PurchaseCostAdjustmentRequest(
                AdjustmentType: original.AdjustmentType,
                SourceDocumentType: "PO_INVOICE_CN",
                SourceDocumentNo: creditNote.DocNo,
                SourceDocumentLine: original.SourceDocumentLine,
                SourceCostingRevision: creditNote.CostingRevision,
                ItemCode: original.ItemCode,
                CostMethod: original.CostMethod,
                BaseQty: original.BaseQty,
                ActualBaseAmount: original.ActualBaseAmount,
                ReferenceBaseAmount: original.ReferenceBaseAmount,
                CommercialReferenceAmount: original.CommercialReferenceAmount,
                TotalAdjustmentAmount: reversalTotal,
                InventoryAdjustmentAmount: appliedInventory,
                ConsumedVarianceAmount: consumedVariance,
                ValueAdjustment: valueAdjustment,
                PoNo: original.PoNo,
                PoRelNo: original.PoRelNo,
                PoLineNo: original.PoLineNo,
                ReversesAdjustmentId: original.Id));
        }

        return PurchaseReceiptSettlementResult.Success(reversalSettlements, reversalRequests);
    }

    private static async Task<(
        List<PurchaseReceiptCostSettlement> Settlements,
        List<PurchaseCostAdjustment> Adjustments,
        string? Error)> ResolveCreditNoteCostGenerationAsync(
        AppDbContext db, PoInvoice creditNote, long primaryStockPostingId,
        List<PurchaseReceiptCostSettlement> originalSettlements,
        List<PurchaseCostAdjustment> originalAdjustments,
        CancellationToken cancellationToken)
    {
        var effects = await db.StockReplayEffects.AsNoTracking()
            .Where(x => x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode
                && x.BusinessOwnerPostingId == primaryStockPostingId
                && x.EffectKind == StockReplayEffectTypes.PurchaseCreditNoteCostReplacement)
            .ToArrayAsync(cancellationToken);
        if (effects.Length == 0)
            return (originalSettlements, originalAdjustments, null);

        const string invalid = "Credit-note active costing generation has incomplete or mismatched replay ownership.";
        if (originalSettlements.Count != 1 || originalAdjustments.Count != 1
            || effects.Length < 2
            || effects.Length > MaxCreditNoteReplayGenerations * 2
            || effects.Length % 2 != 0)
            return (originalSettlements, originalAdjustments, invalid);
        var settlementEffects = effects.Where(x =>
            x.AffectedEntityType == StockReplayEffectTypes.PurchaseReceiptSettlement)
            .ToArray();
        var adjustmentEffects = effects.Where(x =>
            x.AffectedEntityType == StockReplayEffectTypes.PurchaseCostAdjustment)
            .ToArray();
        if (settlementEffects.Length != effects.Length / 2
            || adjustmentEffects.Length != effects.Length / 2
            || settlementEffects.Any(x => x.OriginalEntityId is null)
            || adjustmentEffects.Any(x => x.OriginalEntityId is null)
            || settlementEffects.GroupBy(x => x.OriginalEntityId).Any(x => x.Count() != 1)
            || adjustmentEffects.GroupBy(x => x.OriginalEntityId).Any(x => x.Count() != 1))
            return (originalSettlements, originalAdjustments, invalid);
        var settlementByPrior = settlementEffects.ToDictionary(x =>
            x.OriginalEntityId!.Value);
        var adjustmentByPrior = adjustmentEffects.ToDictionary(x =>
            x.OriginalEntityId!.Value);
        var currentSettlement = originalSettlements[0];
        var currentAdjustment = originalAdjustments[0];
        var usedEffectIds = new HashSet<long>();
        for (var generation = 0;
             generation <= MaxCreditNoteReplayGenerations; generation++)
        {
            var hasSettlement = settlementByPrior.TryGetValue(
                currentSettlement.Id, out var settlementEffect);
            var hasAdjustment = adjustmentByPrior.TryGetValue(
                currentAdjustment.Id, out var adjustmentEffect);
            if (!hasSettlement && !hasAdjustment)
            {
                if (usedEffectIds.Count != effects.Length
                    || await db.PurchaseReceiptCostSettlements.AsNoTracking()
                        .AnyAsync(x => x.ReversesSettlementId
                            == currentSettlement.Id, cancellationToken)
                    || await db.PurchaseCostAdjustments.AsNoTracking()
                        .AnyAsync(x => x.ReversesAdjustmentId
                            == currentAdjustment.Id, cancellationToken))
                    return (originalSettlements, originalAdjustments, invalid);
                return ([currentSettlement], [currentAdjustment], null);
            }
            if (!hasSettlement || !hasAdjustment
                || !usedEffectIds.Add(settlementEffect!.Id)
                || !usedEffectIds.Add(adjustmentEffect!.Id)
                || settlementEffect.ReplayRunId != adjustmentEffect.ReplayRunId
                || settlementEffect.RootStockPostingId
                    != adjustmentEffect.RootStockPostingId
                || settlementEffect.AffectedEntityId <= 0
                || adjustmentEffect.AffectedEntityId <= 0)
                return (originalSettlements, originalAdjustments, invalid);

        var rootId = settlementEffect.RootStockPostingId;
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == primaryStockPostingId
                && x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode,
                cancellationToken);
        var root = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == rootId
                && x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode,
                cancellationToken);
        var run = await db.StockReplayRuns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == settlementEffect.ReplayRunId
                && x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode,
                cancellationToken);
        if (owner is null || owner.SourceModule != "PROCUREMENT"
            || owner.SourceDocumentType != "PO_INVOICE_CN"
            || owner.SourceDocumentNo != creditNote.DocNo
            || owner.DocumentRevision != creditNote.CostingRevision
            || owner.PostingRole != "PRIMARY" || owner.SealedAtUtc is null
            || root is null || root.SourceModule != "INVENTORY"
            || root.SourceDocumentType != IvTrxTypes.GoodsReceive
            || root.PostingRole is not ("PRIMARY" or "REVERSAL")
            || root.SealedAtUtc is null
            || run is null || run.RootStockPostingId != rootId
            || run.RootRequestId != root.RequestId
            || run.RootDocumentType != root.SourceDocumentType
            || run.RootDocumentId != root.SourceDocumentId
            || string.IsNullOrWhiteSpace(run.PlanHash))
            return (originalSettlements, originalAdjustments, invalid);

        var replacementSettlement = await db.PurchaseReceiptCostSettlements
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == settlementEffect.AffectedEntityId
                && x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode,
                cancellationToken);
        var replacementAdjustment = await db.PurchaseCostAdjustments
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == adjustmentEffect.AffectedEntityId
                && x.CompanyCode == creditNote.CompanyCode
                && x.BranchCode == creditNote.BranchCode,
                cancellationToken);
        var replacementPiSettlement = replacementSettlement?.ReversesSettlementId
            is long piSettlementId
            ? await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == piSettlementId,
                    cancellationToken)
            : null;
        var replacementPiAdjustment = replacementAdjustment?.ReversesAdjustmentId
            is long piAdjustmentId
            ? await db.PurchaseCostAdjustments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == piAdjustmentId,
                    cancellationToken)
            : null;
        var oldSettlement = currentSettlement;
        var oldAdjustment = currentAdjustment;
        if (replacementSettlement is null || replacementAdjustment is null
            || replacementPiSettlement is null || replacementPiAdjustment is null
            || replacementSettlement.StockPostingId != rootId
            || replacementAdjustment.StockPostingId != rootId
            || replacementPiSettlement.StockPostingId != rootId
            || replacementPiAdjustment.StockPostingId != rootId
            || replacementSettlement.PiDocNo != creditNote.InvNo
            || replacementSettlement.PiCostingRevision != oldSettlement.PiCostingRevision
            || replacementSettlement.PiLineNo != oldSettlement.PiLineNo
            || replacementSettlement.ItemCode != oldSettlement.ItemCode
            || replacementSettlement.PoNo != oldSettlement.PoNo
            || replacementSettlement.PoRelNo != oldSettlement.PoRelNo
            || replacementSettlement.PoLineNo != oldSettlement.PoLineNo
            || replacementPiSettlement.ReversesSettlementId is not null
            || replacementPiSettlement.PiDocNo != replacementSettlement.PiDocNo
            || replacementPiSettlement.PiCostingRevision
                != replacementSettlement.PiCostingRevision
            || replacementPiSettlement.PiLineNo != replacementSettlement.PiLineNo
            || replacementPiSettlement.ItemCode != replacementSettlement.ItemCode
            || replacementPiSettlement.PoNo != replacementSettlement.PoNo
            || replacementPiSettlement.PoRelNo != replacementSettlement.PoRelNo
            || replacementPiSettlement.PoLineNo != replacementSettlement.PoLineNo
            || replacementAdjustment.SourceDocumentType != "PO_INVOICE_CN"
            || replacementAdjustment.SourceDocumentNo != creditNote.DocNo
            || replacementAdjustment.SourceCostingRevision != creditNote.CostingRevision
            || replacementPiAdjustment.ReversesAdjustmentId is not null
            || replacementPiAdjustment.SourceDocumentType != "PO_INVOICE"
            || replacementPiAdjustment.SourceDocumentNo != creditNote.InvNo
            || replacementPiAdjustment.ItemCode != replacementAdjustment.ItemCode
            || replacementPiAdjustment.PoNo != replacementAdjustment.PoNo
            || replacementPiAdjustment.PoRelNo != replacementAdjustment.PoRelNo
            || replacementPiAdjustment.PoLineNo != replacementAdjustment.PoLineNo
            || replacementSettlement.SettledBaseQty != oldSettlement.SettledBaseQty
            || replacementAdjustment.BaseQty != oldAdjustment.BaseQty
            || replacementAdjustment.ActualBaseAmount != oldAdjustment.ActualBaseAmount
            || oldAdjustment.InventoryAdjustmentAmount != 0m
            || replacementAdjustment.InventoryAdjustmentAmount != 0m)
            return (originalSettlements, originalAdjustments, invalid);

        var settlementChildren = await db.PurchaseReceiptCostSettlements
            .AsNoTracking().Where(x => x.ReversesSettlementId == oldSettlement.Id)
            .ToArrayAsync(cancellationToken);
        var adjustmentChildren = await db.PurchaseCostAdjustments
            .AsNoTracking().Where(x => x.ReversesAdjustmentId == oldAdjustment.Id)
            .ToArrayAsync(cancellationToken);
        if (settlementChildren.Length != 1
            || settlementChildren[0].ReversesSettlementId != oldSettlement.Id
            || settlementChildren[0].StockPostingId != rootId
            || settlementChildren[0].SettledBaseQty != oldSettlement.SettledBaseQty
            || adjustmentChildren.Length != 1
            || adjustmentChildren[0].ReversesAdjustmentId != oldAdjustment.Id
            || adjustmentChildren[0].StockPostingId != rootId
            || adjustmentChildren[0].BaseQty != oldAdjustment.BaseQty
            || adjustmentChildren[0].ActualBaseAmount != oldAdjustment.ActualBaseAmount
            || adjustmentChildren[0].TotalAdjustmentAmount
                != -oldAdjustment.TotalAdjustmentAmount)
            return (originalSettlements, originalAdjustments, invalid);
            currentSettlement = replacementSettlement;
            currentAdjustment = replacementAdjustment;
        }
        return (originalSettlements, originalAdjustments, invalid);
    }

    private static IReadOnlyList<PurchaseReceiptCostSettlement> BuildSettlementChain(
        PurchaseReceiptCostSettlement root,
        IReadOnlyDictionary<long, PurchaseReceiptCostSettlement> children)
    {
        var chain = new List<PurchaseReceiptCostSettlement> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child))
        {
            if (!seen.Add(child.Id))
                break;
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static IReadOnlyList<PurchaseCostAdjustment> BuildAdjustmentChain(
        PurchaseCostAdjustment root,
        IReadOnlyDictionary<long, PurchaseCostAdjustment> children)
    {
        var chain = new List<PurchaseCostAdjustment> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child))
        {
            if (!seen.Add(child.Id))
                break;
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static decimal SignedSettlementAmount(
        IReadOnlyList<PurchaseReceiptCostSettlement> chain,
        Func<PurchaseReceiptCostSettlement, decimal> selector)
    {
        decimal total = 0m;
        for (var index = 0; index < chain.Count; index++)
            total += (index % 2 == 0 ? 1m : -1m) * selector(chain[index]);
        return StockLedgerPrecision.Quantity(total);
    }

    private static decimal SignedAdjustmentQuantity(
        IReadOnlyList<PurchaseCostAdjustment> chain)
    {
        decimal total = 0m;
        for (var index = 0; index < chain.Count; index++)
            total += (index % 2 == 0 ? 1m : -1m) * chain[index].BaseQty;
        return StockLedgerPrecision.Quantity(total);
    }

    private static decimal SignedSettlementMoney(
        IReadOnlyList<PurchaseReceiptCostSettlement> chain,
        Func<PurchaseReceiptCostSettlement, decimal> selector)
    {
        decimal total = 0m;
        for (var index = 0; index < chain.Count; index++)
            total += (index % 2 == 0 ? 1m : -1m) * selector(chain[index]);
        return StockLedgerPrecision.Money(total);
    }

    private static decimal SignedAdjustmentMoney(
        IReadOnlyList<PurchaseCostAdjustment> chain,
        Func<PurchaseCostAdjustment, decimal> selector)
    {
        decimal total = 0m;
        for (var index = 0; index < chain.Count; index++)
            total += (index % 2 == 0 ? 1m : -1m) * selector(chain[index]);
        return StockLedgerPrecision.Money(total);
    }

    private static PriorSettlementTotals CalculateActiveTotals(
        IEnumerable<PurchaseReceiptCostSettlement> rows)
    {
        var materialized = rows.ToArray();
        var byId = materialized.ToDictionary(x => x.Id);
        decimal quantity = 0m;
        decimal commercial = 0m;
        decimal valuation = 0m;

        foreach (var row in materialized)
        {
            var sign = 1m;
            var parentId = row.ReversesSettlementId;
            var seen = new HashSet<long> { row.Id };
            while (parentId is long parent
                   && byId.TryGetValue(parent, out var parentRow)
                   && seen.Add(parentRow.Id))
            {
                sign = -sign;
                parentId = parentRow.ReversesSettlementId;
            }

            quantity += sign * row.SettledBaseQty;
            commercial += sign * row.ReceiptCommercialBaseAmount;
            valuation += sign * row.ReceiptValuationBaseAmount;
        }

        return new PriorSettlementTotals(
            StockLedgerPrecision.Quantity(quantity),
            StockLedgerPrecision.Money(commercial),
            StockLedgerPrecision.Money(valuation));
    }

    private sealed class ActiveSettlementSlice
    {
        public ActiveSettlementSlice(
            PurchaseReceiptCostSettlement original,
            decimal remainingQty,
            decimal remainingCommercialAmount,
            decimal remainingValuationAmount,
            decimal remainingActualAmount,
            PurchaseCostAdjustment adjustment)
        {
            Original = original;
            RemainingQty = remainingQty;
            RemainingCommercialAmount = remainingCommercialAmount;
            RemainingValuationAmount = remainingValuationAmount;
            RemainingActualAmount = remainingActualAmount;
            Adjustment = adjustment;
        }

        public PurchaseReceiptCostSettlement Original { get; }
        public PurchaseCostAdjustment Adjustment { get; }
        public decimal RemainingQty { get; private set; }
        public decimal RemainingCommercialAmount { get; private set; }
        public decimal RemainingValuationAmount { get; private set; }
        public decimal RemainingActualAmount { get; private set; }

        public void Consume(
            decimal quantity,
            decimal commercialAmount,
            decimal valuationAmount,
            decimal actualAmount)
        {
            RemainingQty = StockLedgerPrecision.Quantity(RemainingQty - quantity);
            RemainingCommercialAmount = StockLedgerPrecision.Money(
                RemainingCommercialAmount - commercialAmount);
            RemainingValuationAmount = StockLedgerPrecision.Money(
                RemainingValuationAmount - valuationAmount);
            RemainingActualAmount = StockLedgerPrecision.Money(
                RemainingActualAmount - actualAmount);
        }
    }

    private static async Task<Dictionary<string, StockCostState>> LoadStatesAsync(
        AppDbContext db,
        PoInvoice invoice,
        IReadOnlyCollection<SettlementWithUom> rows,
        string costMethod,
        CancellationToken cancellationToken)
    {
        var items = rows.Select(x => x.Row.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0)
            return new(StringComparer.OrdinalIgnoreCase);

        return (await db.StockCostStates
                .Where(x => x.CompanyCode == invoice.CompanyCode
                            && x.BranchCode == invoice.BranchCode
                            && x.CostMethod == costMethod
                            && items.Contains(x.ItemCode))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PurchaseCostAdjustmentRequest> BuildAdjustments(
        PoInvoice invoice,
        string costMethod,
        IReadOnlyList<SettlementWithUom> rows,
        IReadOnlyDictionary<string, StockCostState> states)
    {
        var method = costMethod.Trim().ToUpperInvariant();
        var grouped = rows.GroupBy(x => x.Row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<PurchaseCostAdjustmentRequest>(rows.Count);

        foreach (var group in grouped)
        {
            var state = states.GetValueOrDefault(group.Key);
            var runningValue = StockLedgerPrecision.Money(state?.InventoryValue ?? 0m);
            var settledQty = StockLedgerPrecision.Quantity(group.Value.Sum(x => x.Row.SettledBaseQty));
            var remainingCapitalizableQty = StockLedgerPrecision.Quantity(
                Math.Min(Math.Max(state?.OnHandBaseQty ?? 0m, 0m), settledQty));

            foreach (var entry in group.Value)
            {
                var row = entry.Row;
                var referenceAmount = method == StockCostMethods.Standard
                    ? row.ReceiptValuationBaseAmount
                    : row.ReceiptCommercialBaseAmount;
                var variance = StockLedgerPrecision.Money(row.AllocatedActualBaseAmount - referenceAmount);
                var capitalizableQty = StockLedgerPrecision.Quantity(
                    Math.Min(remainingCapitalizableQty, row.SettledBaseQty));
                var inventoryAmount = variance == 0m || capitalizableQty <= 0m
                    ? 0m
                    : StockLedgerPrecision.Money(variance * capitalizableQty / row.SettledBaseQty);

                if (method == StockCostMethods.Standard)
                    inventoryAmount = 0m;
                else if (inventoryAmount < 0m)
                    inventoryAmount = Math.Max(-runningValue, inventoryAmount);

                inventoryAmount = StockLedgerPrecision.Money(inventoryAmount);
                var consumedAmount = StockLedgerPrecision.Money(variance - inventoryAmount);
                runningValue = StockLedgerPrecision.Money(runningValue + inventoryAmount);
                remainingCapitalizableQty = StockLedgerPrecision.Quantity(
                    remainingCapitalizableQty - capitalizableQty);

                var splitOrdinal = result.Count(x => x.SourceDocumentLine == row.PiLineNo);
                var valueAdjustment = inventoryAmount == 0m
                    ? null
                    : new StockValueAdjustmentRequest(
                        row.PiLineNo,
                        splitOrdinal,
                        row.ItemCode,
                        entry.BaseUom,
                        Math.Abs(inventoryAmount),
                        inventoryAmount > 0m ? 1 : -1,
                        inventoryAmount > 0m ? "PURCHASE_PRICE_VARIANCE_IN" : "PURCHASE_PRICE_VARIANCE_OUT",
                        StockValuationSources.PurchasePriceVariance,
                        SourceDocumentLine: row.PiLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OriginalValuationFactId: row.ReceiptValuationFactId);

                result.Add(new PurchaseCostAdjustmentRequest(
                    AdjustmentType: "PURCHASE_PRICE_VARIANCE",
                    SourceDocumentType: "PO_INVOICE",
                    SourceDocumentNo: invoice.DocNo,
                    SourceDocumentLine: row.PiLineNo,
                    SourceCostingRevision: invoice.CostingRevision,
                    ItemCode: row.ItemCode,
                    CostMethod: method,
                    BaseQty: row.SettledBaseQty,
                    ActualBaseAmount: row.AllocatedActualBaseAmount,
                    ReferenceBaseAmount: referenceAmount,
                    CommercialReferenceAmount: row.ReceiptCommercialBaseAmount,
                    TotalAdjustmentAmount: variance,
                    InventoryAdjustmentAmount: inventoryAmount,
                    ConsumedVarianceAmount: consumedAmount,
                    ValueAdjustment: valueAdjustment,
                    PoNo: row.PoNo,
                    PoRelNo: row.PoRelNo,
                    PoLineNo: row.PoLineNo));
            }
        }

        return result;
    }

    private sealed record ReceiptCandidate(
        long FactId,
        decimal BaseQty,
        decimal UnitCost,
        decimal CostAmount,
        DateTime BusinessDate,
        long PostingSequence,
        int? InventoryHistoryId,
        int BatchNo,
        string PoNo,
        short PoRelNo,
        short PoLineNo,
        string ItemCode,
        decimal? CommercialUnitCost,
        string? BaseUom,
        long StockPostingId);

    private sealed record PriorSettlementTotals(
        decimal SettledQty,
        decimal CommercialAmount,
        decimal ValuationAmount);

    private sealed record SettlementWithUom(
        PurchaseReceiptCostSettlement Row,
        string BaseUom);

    private sealed class StringTupleComparer : IEqualityComparer<(string PoNo, short PoRelNo, short PoLineNo, string ItemCode)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals(
            (string PoNo, short PoRelNo, short PoLineNo, string ItemCode) x,
            (string PoNo, short PoRelNo, short PoLineNo, string ItemCode) y) =>
            x.PoRelNo == y.PoRelNo
            && x.PoLineNo == y.PoLineNo
            && string.Equals(x.PoNo, y.PoNo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ItemCode, y.ItemCode, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string PoNo, short PoRelNo, short PoLineNo, string ItemCode) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PoNo),
                obj.PoRelNo,
                obj.PoLineNo,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemCode));
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? "system"
            : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    private static string CostingKey(
        int line,
        string itemCode,
        string? poNo,
        short? poRelNo,
        short? poLineNo) =>
        $"{line}|{(itemCode ?? string.Empty).Trim().ToUpperInvariant()}|{(poNo ?? string.Empty).Trim().ToUpperInvariant()}|{poRelNo?.ToString() ?? string.Empty}|{poLineNo?.ToString() ?? string.Empty}";
}
