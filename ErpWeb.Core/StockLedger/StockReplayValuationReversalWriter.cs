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

internal sealed record StockReplayValuationReversalRequest(
    int OriginalHistoryId, IReadOnlyCollection<StockReplayValuationReversalLine> Lines);

/// <summary>
/// Appends exact inverse facts for one complete active Inventory valuation
/// generation inside an unsealed replay root. The replay caller separately
/// appends replacement facts, updates the live cost state, and reconciles all
/// dependent Production and Sales costs before sealing.
/// </summary>
public static class StockReplayValuationReversalWriter
{
    public static Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        int originalHistoryId,
        IReadOnlyCollection<StockReplayValuationReversalLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return AppendManyAsync(context, [new(originalHistoryId, lines)], cancellationToken);
    }

    internal static async Task<IReadOnlyList<StockValuationFact>> AppendManyAsync(
        StockPostingContext context,
        IReadOnlyCollection<StockReplayValuationReversalRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);
        context.EnsureUnsealed();
        if (requests.Count == 0 || requests.Any(r => r.OriginalHistoryId <= 0 || r.Lines is null || r.Lines.Count == 0)
            || requests.Select(r => r.OriginalHistoryId).Distinct().Count() != requests.Count)
            throw Invalid("Replay valuation reversal requires unique positive history identities.");
        var lines = requests.SelectMany(r => r.Lines).ToArray();
        if (lines.Any(l => l.OriginalFactId <= 0
                || l.PostingLineNo <= 0 || l.SplitOrdinal < 0)
            || lines.Select(l => l.OriginalFactId).Distinct().Count() != lines.Length
            || lines.Select(l => (l.PostingLineNo, l.SplitOrdinal))
                .Distinct().Count() != lines.Length)
            throw Invalid("Replay valuation reversal requires unique positive fact and root line identities.");

        var db = context.Db;
        var historyIds = requests.Select(r => r.OriginalHistoryId).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => historyIds.Contains(h.Id)
            && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        if (histories.Length != historyIds.Length || histories.Any(h => h.LedgerVersion != 2
                || h.LedgerEpochId != context.Epoch.Id || h.EntryRole != "ORIGINAL"
                || h.BatchStatus != "POSTED" || h.ValuationStatus != StockValuationStatuses.Valued))
            throw Invalid("A reversal history is not a V2 original in the replay epoch.");
        var historiesById = histories.ToDictionary(h => h.Id);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, histories, cancellationToken);
        foreach (var request in requests)
            if (!request.Lines.Select(l => l.OriginalFactId).Order()
                .SequenceEqual(active[request.OriginalHistoryId].Select(f => f.Id).Order()))
                throw Invalid($"History {request.OriginalHistoryId} reversal omits or adds an active valuation fact.");
        var originals = active.Values.SelectMany(f => f).ToArray();
        var byId = originals.ToDictionary(f => f.Id);

        var ownerIds = originals.Select(f => f.StockPostingId)
            .Concat(histories.Select(h => h.StockPostingId ?? 0L)).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (ownerIds.Any(id => !owners.TryGetValue(id, out var owner)
                || owner.CompanyCode != context.CompanyCode
                || owner.BranchCode != context.BranchCode
                || owner.LedgerEpochId != context.Epoch.Id
                || owner.SealedAtUtc is null))
            throw Invalid("A reversal history has an unsealed valuation owner.");
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
        if (targets.Count != targetIds.Length || requests.Any(r => r.Lines.Any(l =>
                l.ReversalHistoryId is int id && (!targets.TryGetValue(id, out var h)
                    || h.CompanyCode != context.CompanyCode || h.BranchCode != context.BranchCode
                    || h.LedgerEpochId != context.Epoch.Id || h.StockPostingId != context.Posting.Id
                    || h.LedgerVersion != 2 || h.EntryRole != "REVERSAL"
                    || h.ReversesHistoryId != r.OriginalHistoryId || h.BatchStatus != "POSTED"))))
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
        var reversals = new List<StockValuationFact>(lines.Length);
        foreach (var request in requests)
        foreach (var line in request.Lines.OrderBy(l => l.PostingLineNo).ThenBy(l => l.SplitOrdinal))
        {
            var original = byId[line.OriginalFactId];
            var target = line.ReversalHistoryId is int targetId
                ? targets[targetId] : historiesById[request.OriginalHistoryId];
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
