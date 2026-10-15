using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockCountCorrectionHistories(
    int CountLineId, int? ReversalHistoryId, int? AdjustmentHistoryId);

/// <summary>
/// Appends count lineage after a triggering root has written and flushed its
/// count reversal/replacement ADJ histories. Financial replay must subsequently
/// value every replacement and reversal before the root may be sealed.
/// </summary>
public static class StockCountCorrectionGenerationWriter
{
    public static async Task<IReadOnlyList<StockCountReplayGeneration>> AppendAsync(
        StockPostingContext context,
        StockCountCorrectionWritePermit permit,
        IReadOnlyCollection<StockCountCorrectionHistories> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(evidence);
        context.EnsureUnsealed();
        if (context.Posting.Id != permit.RootPostingId
            || context.Epoch.Id != permit.LedgerEpochId
            || context.CompanyCode != permit.CompanyCode
            || context.BranchCode != permit.BranchCode
            || string.IsNullOrWhiteSpace(permit.PlanHash)
            || permit.Lines.IsDefaultOrEmpty
            || evidence.Count != permit.Lines.Length
            || evidence.Select(e => e.CountLineId).Distinct().Count() != evidence.Count)
            throw Invalid("Count corrections lack a complete validated root permit.");

        var db = context.Db;
        var balance = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.Id == permit.BalLocId && b.CompanyCode == permit.CompanyCode
                && b.BranchCode == permit.BranchCode)
            .Select(b => (decimal?)b.StdQty)
            .SingleOrDefaultAsync(cancellationToken);
        if (balance != permit.FinalQuantity)
            throw Invalid("The replayed pile does not close to its projected live quantity.");
        var candidateHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.StockPostingId == context.Posting.Id && h.LedgerVersion == 2
                && h.EntryRole == "ORIGINAL"
                && h.TrxDtTime == permit.Candidate.Key.EffectiveAt
                && (h.FromBalLocId == permit.BalLocId
                    || h.ToBalLocId == permit.BalLocId))
            .ToArrayAsync(cancellationToken);
        if (candidateHistories.Length != 1
            || IvQty.Round((candidateHistories[0].ToBalLocId == permit.BalLocId
                    ? candidateHistories[0].ToStdQty ?? 0m : 0m)
                - (candidateHistories[0].FromBalLocId == permit.BalLocId
                    ? candidateHistories[0].FrStdQty ?? 0m : 0m))
                != permit.Candidate.SignedDelta)
            throw Invalid("The triggering physical event was not persisted in this replay root.");
        var rootPostingLines = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.StockPostingId == context.Posting.Id && h.LedgerVersion == 2)
            .Select(h => h.PostingLineNo).ToArrayAsync(cancellationToken);
        if (rootPostingLines.Any(n => n is not > 0)
            || rootPostingLines.Distinct().Count() != rootPostingLines.Length)
            throw Invalid("The replay root has duplicate Inventory posting line numbers.");

        var lineIds = permit.Lines.Select(l => l.LineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, permit.CompanyCode, permit.BranchCode, lineIds, cancellationToken);
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => lineIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id, l.BalLocId, l.ICode, l.PhysicalQty,
                l.Header.CountDate, l.Header.CountNo, l.Header.Status
            }).ToDictionaryAsync(l => l.Id, cancellationToken);
        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => lineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (bases.GroupBy(b => b.StockCountLineId).Any(g => g.Count() != 1))
            throw Invalid("A corrected count has ambiguous frozen cost basis.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);
        var evidenceByLine = evidence.ToDictionary(e => e.CountLineId);
        if (permit.Lines.Any(p => !evidenceByLine.ContainsKey(p.LineId)
                || !lines.TryGetValue(p.LineId, out var line)
                || !active.TryGetValue(p.LineId, out var prior)
                || prior.Id != p.PreviousGenerationId || prior.IsRemoval
                || prior.AnchorPostingId != p.AnchorPostingId
                || prior.AnchorPostingSequence != p.AnchorPostingSequence
                || prior.EffectiveAt != p.EffectiveAt
                || prior.SignedAdjustmentQty != p.OldSignedAdjustmentQty
                || line.BalLocId != permit.BalLocId
                || line.PhysicalQty != p.PhysicalQty
                || line.Status != IvStockCountStatuses.Posted
                || StockReplayEventKey.CountEndOfDay(line.CountDate) != p.EffectiveAt
                || p.NewSignedAdjustmentQty == p.OldSignedAdjustmentQty
                || IvQty.Round(p.NewSignedAdjustmentQty) != p.NewSignedAdjustmentQty
                || line.PhysicalQty > 0m && !basisByLine.ContainsKey(p.LineId)))
            throw Invalid("Count correction no longer matches its sealed active observation.");

        var historyIds = evidence.SelectMany(e => new[]
            { e.ReversalHistoryId, e.AdjustmentHistoryId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        if (historyIds.Length != historyIds.Distinct().Count())
            throw Invalid("Count correction reuses an Inventory history identity.");
        if (await db.StockCountReplayGenerations.AsNoTracking()
            .AnyAsync(g => g.CompanyCode == permit.CompanyCode
                && g.BranchCode == permit.BranchCode
                && (g.AdjustmentHistoryId.HasValue
                    && historyIds.Contains(g.AdjustmentHistoryId.Value)
                    || g.ReversalHistoryId.HasValue
                    && historyIds.Contains(g.ReversalHistoryId.Value)), cancellationToken))
            throw Invalid("Count correction reuses history from an earlier generation.");
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        if (histories.Count != historyIds.Length)
            throw Invalid("Count correction lacks persisted Inventory history.");
        var batchNos = histories.Values.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == permit.CompanyCode
                && b.BranchCode == permit.BranchCode && batchNos.Contains(b.BatchNo))
            .ToDictionaryAsync(b => b.BatchNo, cancellationToken);
        if (batches.Count != batchNos.Length)
            throw Invalid("Count correction lacks a posted ADJ batch.");
        var batchHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == permit.CompanyCode
                && h.BranchCode == permit.BranchCode && batchNos.Contains(h.BatchNo))
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (batchHistories.Length != historyIds.Length
            || !batchHistories.Order().SequenceEqual(historyIds.Order()))
            throw Invalid("A count correction batch contains unrelated Inventory histories.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.CompanyCode == permit.CompanyCode
                && d.BranchCode == permit.BranchCode
                && batchNos.Contains(d.BatchNo))
            .ToArrayAsync(cancellationToken);
        if (details.Length != historyIds.Length
            || details.GroupBy(d => (d.BatchNo, d.TrxLineNo))
                .Any(g => g.Count() != 1))
            throw Invalid("A count correction batch lacks exact ADJ detail lines.");
        var detailsByHistoryKey = details.ToDictionary(d => (d.BatchNo, d.TrxLineNo));

        var result = new List<StockCountReplayGeneration>(permit.Lines.Length);
        foreach (var projection in permit.Lines)
        {
            var line = lines[projection.LineId];
            var prior = active[projection.LineId];
            var pair = evidenceByLine[projection.LineId];
            if ((prior.AdjustmentHistoryId.HasValue) != pair.ReversalHistoryId.HasValue
                || (projection.NewSignedAdjustmentQty != 0m)
                    != pair.AdjustmentHistoryId.HasValue)
                throw Invalid($"Count line {line.Id} has incomplete reversal or replacement evidence.");

            IvTrxHistory? reversal = null;
            if (pair.ReversalHistoryId is int reversalId)
            {
                reversal = histories[reversalId];
                if (!ValidHistory(reversal, context, line.ICode,
                        line.CountNo, line.CountDate, permit.BalLocId,
                        -prior.SignedAdjustmentQty, "REVERSAL")
                    || reversal.ReversesHistoryId != prior.AdjustmentHistoryId)
                    throw Invalid($"Count line {line.Id} has an invalid ADJ reversal.");
                var original = await db.IvTrxHistories.AsNoTracking()
                    .SingleAsync(h => h.Id == prior.AdjustmentHistoryId, cancellationToken);
                if (reversal.StockCountCostBasisId != original.StockCountCostBasisId
                    || reversal.FromBalLocId != original.ToBalLocId
                    || reversal.ToBalLocId != original.FromBalLocId
                    || reversal.FrStdQty != original.ToStdQty
                    || reversal.ToStdQty != original.FrStdQty)
                    throw Invalid($"Count line {line.Id} does not exactly reverse its active ADJ.");
            }
            IvTrxHistory? replacement = null;
            if (pair.AdjustmentHistoryId is int replacementId)
            {
                replacement = histories[replacementId];
                if (!ValidHistory(replacement, context, line.ICode,
                        line.CountNo, line.CountDate, permit.BalLocId,
                        projection.NewSignedAdjustmentQty, "ORIGINAL")
                    || replacement.ReversesHistoryId is not null
                    || replacement.StockCountCostBasisId is long basisId
                        && (!basisByLine.TryGetValue(line.Id, out var basis)
                            || basis.Id != basisId))
                    throw Invalid($"Count line {line.Id} has an invalid replacement ADJ.");
            }
            result.Add(new StockCountReplayGeneration
            {
                CompanyCode = permit.CompanyCode,
                BranchCode = permit.BranchCode,
                StockCountLineId = line.Id,
                AnchorPostingId = prior.AnchorPostingId,
                AnchorPostingSequence = prior.AnchorPostingSequence,
                WritePostingId = context.Posting.Id,
                PreviousGenerationId = prior.Id,
                EffectiveAt = prior.EffectiveAt,
                SignedAdjustmentQty = projection.NewSignedAdjustmentQty,
                ReversalBatchNo = reversal?.BatchNo,
                ReversalHistoryId = reversal?.Id,
                AdjustmentBatchNo = replacement?.BatchNo,
                AdjustmentHistoryId = replacement?.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        db.StockCountReplayGenerations.AddRange(result);
        await db.SaveChangesAsync(cancellationToken);
        return result;

        bool ValidHistory(IvTrxHistory history, StockPostingContext root,
            string itemCode, string countNo, DateTime countDate,
            int pileId, decimal delta, string role)
        {
            if (!batches.TryGetValue(history.BatchNo, out var batch)
                || !detailsByHistoryKey.TryGetValue(
                    (history.BatchNo, history.TrxLineNo), out var detail)
                || batch.TrxType != IvTrxTypes.StockAdjustment
                || batch.BatchStatus != IvBatchStatuses.Posted
                || batch.TrxDtTime != countDate.Date
                || batch.RefNo != countNo
                || detail.BatchId != batch.Id
                || detail.CompanyCode != permit.CompanyCode
                || detail.BranchCode != permit.BranchCode
                || history.CompanyCode != permit.CompanyCode
                || history.BranchCode != permit.BranchCode
                || history.StockPostingId != root.Posting.Id
                || history.LedgerVersion != 2
                || history.LedgerEpochId != permit.LedgerEpochId
                || history.DocumentRevision != root.Posting.DocumentRevision
                || history.TrxType != IvTrxTypes.StockAdjustment
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.TrxDtTime != countDate.Date
                || history.RefNo != countNo
                || history.ICode != itemCode
                || detail.ICode != itemCode
                || detail.TrxType != IvTrxTypes.StockAdjustment
                || detail.DocumentRevision != root.Posting.DocumentRevision
                || detail.FromBalLocId != history.FromBalLocId
                || detail.ToBalLocId != history.ToBalLocId
                || detail.FrStdQty != history.FrStdQty
                || detail.ToStdQty != history.ToStdQty
                || detail.StockCountCostBasisId != history.StockCountCostBasisId
                || history.EntryRole != role
                || history.PostingLineNo is not > 0)
                return false;
            if (delta > 0m)
                return history.ToBalLocId == pileId
                    && history.FromBalLocId is null
                    && history.ToStdQty == delta && history.FrStdQty is null;
            if (delta < 0m)
                return history.FromBalLocId == pileId
                    && history.ToBalLocId is null
                    && history.FrStdQty == -delta && history.ToStdQty is null;
            return false;
        }
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
