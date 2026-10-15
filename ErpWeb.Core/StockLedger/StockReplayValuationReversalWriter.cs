using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayValuationReversalLine(
    long OriginalFactId,
    int PostingLineNo,
    int SplitOrdinal,
    int? ReversalHistoryId = null);

/// <summary>
/// Appends exact inverse facts for one complete active Inventory valuation
/// generation inside an unsealed replay root. The replay caller separately
/// appends replacement facts, updates the live cost state, and reconciles all
/// dependent Production and Sales costs before sealing.
/// </summary>
public static class StockReplayValuationReversalWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        int originalHistoryId,
        IReadOnlyCollection<StockReplayValuationReversalLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);
        context.EnsureUnsealed();
        if (originalHistoryId <= 0 || lines.Count == 0
            || lines.Any(l => l.OriginalFactId <= 0
                || l.PostingLineNo <= 0 || l.SplitOrdinal < 0)
            || lines.Select(l => l.OriginalFactId).Distinct().Count() != lines.Count
            || lines.Select(l => (l.PostingLineNo, l.SplitOrdinal))
                .Distinct().Count() != lines.Count)
            throw Invalid("Replay valuation reversal requires unique positive fact and root line identities.");

        var db = context.Db;
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == originalHistoryId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (history is null || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL" || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued)
            throw Invalid($"History {originalHistoryId} is not a V2 original in the replay epoch.");
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken);
        var originals = active[originalHistoryId];
        var requestedIds = lines.Select(l => l.OriginalFactId).Order().ToArray();
        if (!requestedIds.SequenceEqual(originals.Select(f => f.Id).Order()))
            throw Invalid($"History {originalHistoryId} reversal omits or adds an active valuation fact.");
        var byId = originals.ToDictionary(f => f.Id);

        var ownerIds = originals.Select(f => f.StockPostingId)
            .Append(history.StockPostingId ?? 0L).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (ownerIds.Any(id => !owners.TryGetValue(id, out var owner)
                || owner.CompanyCode != context.CompanyCode
                || owner.BranchCode != context.BranchCode
                || owner.LedgerEpochId != context.Epoch.Id
                || owner.SealedAtUtc is null))
            throw Invalid($"History {originalHistoryId} has an unsealed valuation owner.");
        var earliest = originals.Min(f => f.EffectiveAt);
        var periodRefusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            context.CompanyCode, context.BranchCode, earliest, cancellationToken);
        if (periodRefusal is not null)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod,
                periodRefusal));

        var targetIds = lines.Where(l => l.ReversalHistoryId.HasValue)
            .Select(l => l.ReversalHistoryId!.Value).Distinct().ToArray();
        var targets = await db.IvTrxHistories.AsNoTracking()
            .Where(h => targetIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        if (targets.Count != targetIds.Length
            || targets.Values.Any(h => h.CompanyCode != context.CompanyCode
                || h.BranchCode != context.BranchCode
                || h.LedgerEpochId != context.Epoch.Id
                || h.StockPostingId != context.Posting.Id
                || h.LedgerVersion != 2 || h.EntryRole != "REVERSAL"
                || h.ReversesHistoryId != originalHistoryId
                || h.BatchStatus != "POSTED"))
            throw Invalid("A valuation reversal has no exact root Inventory reversal history.");

        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id)
            .Select(f => new { f.PostingLineNo, f.SplitOrdinal })
            .ToArrayAsync(cancellationToken);
        var occupied = rootFacts.Select(f => (f.PostingLineNo, f.SplitOrdinal))
            .Concat(db.ChangeTracker.Entries<StockValuationFact>()
                .Where(e => e.State == EntityState.Added
                    && e.Entity.StockPostingId == context.Posting.Id)
                .Select(e => (e.Entity.PostingLineNo, e.Entity.SplitOrdinal)))
            .ToHashSet();
        if (lines.Any(l => occupied.Contains((l.PostingLineNo, l.SplitOrdinal))))
            throw Invalid("A replay valuation root line identity is already used.");

        var now = DateTime.UtcNow;
        var reversals = new List<StockValuationFact>(lines.Count);
        foreach (var line in lines.OrderBy(l => l.PostingLineNo)
            .ThenBy(l => l.SplitOrdinal))
        {
            var original = byId[line.OriginalFactId];
            var target = line.ReversalHistoryId is int targetId
                ? targets[targetId] : history;
            reversals.Add(new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = line.PostingLineNo,
                SplitOrdinal = line.SplitOrdinal,
                SourceLineId = original.SourceLineId,
                SourceDocumentType = original.SourceDocumentType,
                SourceDocumentId = original.SourceDocumentId,
                SourceDocumentNo = original.SourceDocumentNo,
                SourceDocumentLine = original.SourceDocumentLine,
                EffectiveAt = original.EffectiveAt,
                BusinessDate = original.BusinessDate,
                PeriodKey = original.PeriodKey,
                ItemCode = original.ItemCode,
                WarehouseCode = original.WarehouseCode,
                LocationCode = original.LocationCode,
                LotId = original.LotId,
                LotNo = original.LotNo,
                ItemStatus = original.ItemStatus,
                BaseUom = original.BaseUom,
                MovementCode = original.MovementCode,
                Direction = -original.Direction,
                BaseQty = original.BaseQty,
                CostMethod = original.CostMethod,
                UnitCost = original.UnitCost,
                CostAmount = original.CostAmount,
                TransactionCurrency = original.TransactionCurrency,
                TransactionCostAmount = original.TransactionCostAmount,
                ExchangeRate = original.ExchangeRate,
                BaseCurrency = original.BaseCurrency,
                BaseCostAmount = original.BaseCostAmount,
                ValuationSource = StockValuationSources.OriginalReversal,
                ValuationStatus = StockValuationStatuses.Reversed,
                ValuationVersion = original.ValuationVersion,
                InventoryHistoryId = target.Id,
                FromBalLocId = target.FromBalLocId,
                ToBalLocId = target.ToBalLocId,
                ProductionMovementId = original.ProductionMovementId,
                WorkOrderId = original.WorkOrderId,
                WorkOrderOperationId = original.WorkOrderOperationId,
                ProductionPostingLinkId = original.ProductionPostingLinkId,
                OriginalValuationFactId = original.OriginalValuationFactId
                    ?? original.Id,
                ReversesValuationFactId = original.Id,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        db.StockValuationFacts.AddRange(reversals);
        return reversals;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
