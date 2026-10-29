using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Reads effective Production costs only after verifying sealed replay ownership.</summary>
internal static class ProductionReplayEffectiveCostReader
{
    public static async Task<IReadOnlyDictionary<long, ProductionEffectiveCost>> ReadAsync(
        AppDbContext db, string company, string branch, IReadOnlyCollection<long> movementIds,
        CancellationToken ct = default)
    {
        var ids = movementIds.Distinct().ToArray();
        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db, company, branch, ids, ct);
        if (ids.Length == 0) return costs;
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid)).ToDictionaryAsync(m => m.Uid, ct);
        var facts = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => ids.Contains(f.ProductionBalLotMovementId)).ToArrayAsync(ct);
        if (facts.Length == 0) return costs;
        var rootIds = facts.Select(f => f.ReplayStockPostingId).Distinct().ToArray();
        var ownerIds = movements.Values.Select(m => m.StockPostingId ?? 0).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => rootIds.Contains(p.Id) || ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var runs = await db.StockReplayRuns.AsNoTracking()
            .Where(r => rootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, ct);
        var factIds = facts.Select(f => f.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking()
            .Where(e => e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"
                && factIds.Contains(e.AffectedEntityId)).ToArrayAsync(ct);
        if (movements.Count != ids.Length || runs.Count != rootIds.Length
            || effects.Length != facts.Length || effects.GroupBy(e => e.AffectedEntityId).Any(g => g.Count() != 1))
            throw Invalid("Production cost restatement ownership is incomplete.");
        var effectByFact = effects.ToDictionary(e => e.AffectedEntityId);
        foreach (var fact in facts)
        {
            if (!movements.TryGetValue(fact.ProductionBalLotMovementId, out var movement)
                || movement.StockPostingId is not long ownerId
                || !postings.TryGetValue(ownerId, out var owner)
                || !postings.TryGetValue(fact.ReplayStockPostingId, out var root)
                || !runs.TryGetValue(fact.ReplayStockPostingId, out var run)
                || !effectByFact.TryGetValue(fact.Id, out var effect)
                || movement.CompanyCode != company || movement.BranchCode != branch
                || fact.CompanyCode != company || fact.BranchCode != branch
                || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
                || root.CompanyCode != company || root.BranchCode != branch || root.SealedAtUtc is null
                || movement.LedgerEpochId != owner.LedgerEpochId || root.LedgerEpochId != owner.LedgerEpochId
                || root.PostingSequence <= owner.PostingSequence
                || fact.EffectiveAt != movement.MovementDate || owner.EffectiveAt != movement.MovementDate
                || fact.ReplayRequestId != root.RequestId || run.RootRequestId != root.RequestId
                || run.CompanyCode != company || run.BranchCode != branch
                || run.RootDocumentType != root.SourceDocumentType || run.RootDocumentId != root.SourceDocumentId
                || run.EarliestEffectiveAt > movement.MovementDate || run.LatestEffectiveAt < movement.MovementDate
                || effect.CompanyCode != company || effect.BranchCode != branch
                || effect.ReplayRunId != run.Id || effect.RootStockPostingId != root.Id
                || effect.BusinessOwnerPostingId != ownerId || effect.OriginalEntityId != movement.Uid
                || effect.EffectKind != "PRODUCTION_COST_RESTATEMENT"
                || movement.BaseQty <= 0m
                || fact.CorrectedUnitCost != StockLedgerPrecision.Money(
                    fact.CorrectedEffectiveTotalCost / movement.BaseQty))
                throw Invalid($"Production cost restatement {fact.Id} has invalid replay ownership.");
        }
        return costs;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
