using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends the first count generation after its optional ADJ history has been
/// flushed inside the count anchor transaction. A zero adjustment still gets
/// one generation row, with no batch or history identity.
/// </summary>
public static class StockCountInitialGenerationWriter
{
    public static async Task<IReadOnlyList<StockCountReplayGeneration>> AppendAsync(
        StockPostingContext context,
        StockCountSheetWritePermit permit,
        int? adjustmentBatchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (permit.CountStatus != "COUNTED" || context.Posting.DocumentRevision != 0
            || context.Posting.Id != permit.RootPostingId
            || context.Posting.PostingSequence != permit.PostingSequence
            || context.Posting.EffectiveAt != permit.EffectiveAt
            || context.Epoch.Id != permit.LedgerEpochId
            || context.CompanyCode != permit.CompanyCode
            || context.BranchCode != permit.BranchCode
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != permit.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || permit.Lines.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(permit.PlanHash))
            throw Invalid("The initial count generation lacks a validated root permit.");

        var db = context.Db;
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == permit.CountHeaderId
                && h.CompanyCode == permit.CompanyCode
                && h.BranchCode == permit.BranchCode)
            .Select(h => new { h.CountNo, h.CountDate, h.Status, h.PostedBatchNo })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || header.Status != "POSTED"
            || header.CountDate.Date != permit.EffectiveAt.Date
            || header.PostedBatchNo != adjustmentBatchNo
            || context.Posting.SourceDocumentNo != header.CountNo)
            throw Invalid($"Count {permit.CountHeaderId} is not posted under its original anchor.");

        var lineIds = permit.Lines.Select(l => l.LineId).ToArray();
        if (lineIds.Distinct().Count() != lineIds.Length
            || permit.Lines.Select(l => l.BalLocId).Distinct().Count() != lineIds.Length
            || await db.StockCountReplayGenerations.AsNoTracking()
                .AnyAsync(g => g.CompanyCode == permit.CompanyCode
                    && g.BranchCode == permit.BranchCode
                    && lineIds.Contains(g.StockCountLineId), cancellationToken))
            throw Invalid("The count already has a generation or duplicate pile identity.");
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == permit.CountHeaderId
                && l.PhysicalQty != null)
            .Select(l => new { l.Id, l.BalLocId, l.ICode, l.PhysicalQty })
            .ToArrayAsync(cancellationToken);
        if (lines.Length != permit.Lines.Length)
            throw Invalid("The count's posted observations differ from the validated sheet.");
        var byLine = lines.ToDictionary(l => l.Id);
        if (permit.Lines.Any(p => !byLine.TryGetValue(p.LineId, out var line)
                || line.BalLocId != p.BalLocId
                || line.PhysicalQty != p.PhysicalQty
                || IvQty.Round(p.SignedAdjustmentQty) != p.SignedAdjustmentQty
                || IvQty.Round(p.FinalQuantity) != p.FinalQuantity))
            throw Invalid("A counted pile changed after the root permit was prepared.");
        var balanceIds = permit.Lines.Select(p => p.BalLocId).ToArray();
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => balanceIds.Contains(b.Id)
                && b.CompanyCode == permit.CompanyCode
                && b.BranchCode == permit.BranchCode)
            .Select(b => new { b.Id, b.StdQty })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (permit.Lines.Any(p => !balances.TryGetValue(p.BalLocId, out var balance)
                || balance.StdQty != p.FinalQuantity))
            throw Invalid("The physical replay did not close to the planned live pile quantities.");

        var positiveIds = lines.Where(l => l.PhysicalQty > 0m)
            .Select(l => l.Id).ToArray();
        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => lineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (bases.Length != positiveIds.Length
            || bases.Any(b => !positiveIds.Contains(b.StockCountLineId))
            || bases.GroupBy(b => b.StockCountLineId).Any(g => g.Count() != 1)
            || bases.Any(b => b.CompanyCode != permit.CompanyCode
                || b.BranchCode != permit.BranchCode)
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, bases))
            throw Invalid("The count is missing immutable cost basis for a positive observation.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);

        var nonzero = permit.Lines.Where(l => l.SignedAdjustmentQty != 0m).ToArray();
        if ((adjustmentBatchNo is null) != (nonzero.Length == 0))
            throw Invalid("The count ADJ batch does not match its projected nonzero lines.");
        IvTrxHistory[] histories;
        if (adjustmentBatchNo is int batchNo)
        {
            var batch = await db.IvTrxBatches.AsNoTracking()
                .Where(b => b.CompanyCode == permit.CompanyCode
                    && b.BranchCode == permit.BranchCode && b.BatchNo == batchNo)
                .Select(b => new { b.TrxType, b.TrxDtTime, b.BatchStatus, b.RefNo })
                .SingleOrDefaultAsync(cancellationToken);
            if (batch is null || batch.TrxType != IvTrxTypes.StockAdjustment
                || batch.TrxDtTime != header.CountDate.Date
                || batch.BatchStatus != IvBatchStatuses.Posted
                || batch.RefNo != header.CountNo)
                throw Invalid($"Count {permit.CountHeaderId} has an invalid ADJ batch.");
            histories = await db.IvTrxHistories.AsNoTracking()
                .Where(h => h.CompanyCode == permit.CompanyCode
                    && h.BranchCode == permit.BranchCode && h.BatchNo == batchNo)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            histories = [];
        }
        if (histories.Length != nonzero.Length)
            throw Invalid("Count adjustment history does not match the projected nonzero lines.");

        var now = DateTime.UtcNow;
        var generations = new List<StockCountReplayGeneration>(permit.Lines.Length);
        var usedHistoryIds = new HashSet<int>();
        foreach (var projection in permit.Lines)
        {
            var line = byLine[projection.LineId];
            IvTrxHistory? history = null;
            if (projection.SignedAdjustmentQty != 0m)
            {
                var matches = histories.Where(h => h.ICode == line.ICode
                    && (h.FromBalLocId == line.BalLocId
                        || h.ToBalLocId == line.BalLocId)).ToArray();
                if (matches.Length != 1 || !usedHistoryIds.Add(matches[0].Id))
                    throw Invalid($"Count line {line.Id} has ambiguous ADJ history.");
                history = matches[0];
                var delta = IvQty.Round((history.ToBalLocId == line.BalLocId
                        ? history.ToStdQty ?? 0m : 0m)
                    - (history.FromBalLocId == line.BalLocId
                        ? history.FrStdQty ?? 0m : 0m));
                if (delta != projection.SignedAdjustmentQty
                    || history.TrxDtTime != header.CountDate.Date
                    || history.TrxType != IvTrxTypes.StockAdjustment
                    || history.BatchStatus != IvBatchStatuses.Posted
                    || history.LedgerVersion != 2
                    || history.LedgerEpochId != permit.LedgerEpochId
                    || history.StockPostingId != context.Posting.Id
                    || history.DocumentRevision != 0
                    || history.EntryRole != "ORIGINAL"
                    || history.ReversesHistoryId is not null
                    || projection.SignedAdjustmentQty > 0m
                        && (!basisByLine.TryGetValue(line.Id, out var basis)
                            || history.StockCountCostBasisId != basis.Id)
                    || projection.SignedAdjustmentQty < 0m
                        && history.StockCountCostBasisId is not null)
                    throw Invalid($"Count line {line.Id} ADJ history differs from its projected adjustment.");
            }
            generations.Add(new StockCountReplayGeneration
            {
                CompanyCode = permit.CompanyCode,
                BranchCode = permit.BranchCode,
                StockCountLineId = line.Id,
                AnchorPostingId = context.Posting.Id,
                WritePostingId = context.Posting.Id,
                EffectiveAt = permit.EffectiveAt,
                AnchorPostingSequence = permit.PostingSequence,
                SignedAdjustmentQty = projection.SignedAdjustmentQty,
                AdjustmentBatchNo = history?.BatchNo,
                AdjustmentHistoryId = history?.Id,
                CreatedAtUtc = now
            });
        }
        db.StockCountReplayGenerations.AddRange(generations);
        await db.SaveChangesAsync(cancellationToken);
        return generations;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
