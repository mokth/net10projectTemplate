using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Records the business owner of each value-only FG correction.</summary>
internal static class ProductionFinishedGoodWacReceiptCorrection
{
    public static async Task<StockReplayWacReceiptRestatementResult?> AppendWithinRootAsync(
        StockPostingContext context, int historyId, long receiptFactId,
        decimal correctedValue, int reversalLine, int replacementLine,
        CancellationToken ct = default)
    {
        var db = context.Db;
        var receipt = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(f => f.Id == receiptFactId && f.InventoryHistoryId == historyId, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == receipt.StockPostingId, ct);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == historyId, ct);
        if (owner.CompanyCode != context.CompanyCode || owner.BranchCode != context.BranchCode
            || owner.LedgerEpochId != context.Epoch.Id || owner.SealedAtUtc is null
            || owner.SourceModule != "PRODUCTION"
            || owner.SourceDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || owner.PostingRole != "PRIMARY" || history.StockPostingId != owner.Id
            || history.TrxType != IvTrxTypes.FinishedGoods
            || receipt.SourceDocumentType != owner.SourceDocumentType
            || receipt.SourceDocumentId != owner.SourceDocumentId)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The value correction is not owned by a finished-good receipt."));
        var run = await db.StockReplayRuns.AsNoTracking().SingleOrDefaultAsync(r =>
            r.CompanyCode == context.CompanyCode && r.BranchCode == context.BranchCode
            && r.RootStockPostingId == context.Posting.Id, ct);
        if (run is null || run.RootRequestId != context.Posting.RequestId
            || run.RootDocumentType != context.Posting.SourceDocumentType
            || run.RootDocumentId != context.Posting.SourceDocumentId
            || run.EarliestEffectiveAt > receipt.EffectiveAt
            || run.LatestEffectiveAt < receipt.EffectiveAt)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The finished-good value correction has no matching replay run."));

        var result = await StockReplayWacReceiptRestatementWriter.AppendAsync(context,
            historyId, receiptFactId, correctedValue, StockValuationSources.ProductionActual,
            reversalLine, replacementLine, ct);
        if (result is null) return null;
        await db.SaveChangesAsync(ct);
        foreach (var fact in new[] { result.Reversal, result.ActiveDelta }.OfType<StockValuationFact>())
            db.StockReplayEffects.Add(new StockReplayEffect
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                ReplayRunId = run.Id, RootStockPostingId = context.Posting.Id,
                BusinessOwnerPostingId = receipt.StockPostingId,
                EffectKind = StockReplayEffectTypes.FinishedGoodReceiptValue,
                AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
                AffectedEntityId = fact.Id, OriginalEntityId = receipt.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync(ct);
        return result;
    }
}
