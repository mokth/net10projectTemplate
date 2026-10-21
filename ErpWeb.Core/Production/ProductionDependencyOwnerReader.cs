using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Verifies the business owner of active dependency edges written by a replay root.</summary>
internal static class ProductionDependencyOwnerReader
{
    internal const string EffectKind = "PRODUCTION_DEPENDENCY";
    internal const string AffectedEntityType = "PRODUCTION_POOL_DEPENDENCY";

    public static async Task<string?> ValidateForConsumerAsync(
        AppDbContext db, string companyCode, string branchCode,
        ProductionBalLotMovement consumer,
        IReadOnlyCollection<ProductionPoolDependency> activeEdges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(activeEdges);
        if (consumer.StockPostingId is not long businessOwnerId
            || consumer.LedgerEpochId is not long epochId
            || consumer.CompanyCode != companyCode || consumer.BranchCode != branchCode)
            return $"Production movement {consumer.Uid} has no V2 business owner.";
        var foreign = activeEdges.Where(e => e.StockPostingId != businessOwnerId)
            .ToArray();
        if (foreign.Length == 0)
            return null;
        var edgeIds = foreign.Select(e => e.Id).Distinct().ToArray();
        if (edgeIds.Length != foreign.Length || edgeIds.Any(id => id <= 0))
            return $"Production movement {consumer.Uid} has duplicate foreign dependency identities.";
        var effects = await db.StockReplayEffects.AsNoTracking()
            .Where(e => e.CompanyCode == companyCode && e.BranchCode == branchCode
                && e.EffectKind == EffectKind
                && e.AffectedEntityType == AffectedEntityType
                && edgeIds.Contains(e.AffectedEntityId))
            .ToArrayAsync(cancellationToken);
        if (effects.Length != foreign.Length
            || effects.GroupBy(e => e.AffectedEntityId).Any(g => g.Count() != 1))
            return $"Production movement {consumer.Uid} has a foreign dependency without one replay effect.";
        var effectByEdge = effects.ToDictionary(e => e.AffectedEntityId);
        var priorIds = effects.Select(e => e.OriginalEntityId ?? 0).Distinct().ToArray();
        var priors = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => priorIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);
        var reversals = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => e.ReversesDependencyId.HasValue
                && priorIds.Contains(e.ReversesDependencyId.Value))
            .ToArrayAsync(cancellationToken);
        var rootIds = foreign.Select(e => e.StockPostingId).Distinct().ToArray();
        var postingIds = rootIds.Append(businessOwnerId).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var runs = await db.StockReplayRuns.AsNoTracking()
            .Where(r => rootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, cancellationToken);
        if (priors.Count != priorIds.Length || postings.Count != postingIds.Length
            || runs.Count != rootIds.Length)
            return $"Production movement {consumer.Uid} has incomplete foreign dependency lineage.";
        var owner = postings[businessOwnerId];
        if (owner.CompanyCode != companyCode || owner.BranchCode != branchCode
            || owner.LedgerEpochId != epochId || owner.SealedAtUtc is null
            || owner.EffectiveAt != consumer.MovementDate)
            return $"Production movement {consumer.Uid} has an invalid original business owner.";
        if (await db.StockPostings.AsNoTracking().AnyAsync(p =>
                p.SealedAtUtc != null && p.ReversesPostingId.HasValue
                && postingIds.Contains(p.ReversesPostingId.Value),
                cancellationToken))
            return $"Production movement {consumer.Uid} has a reversed dependency owner.";

        foreach (var edge in foreign)
        {
            var effect = effectByEdge[edge.Id];
            var root = postings[edge.StockPostingId];
            var run = runs[edge.StockPostingId];
            var reverseRows = reversals.Where(r =>
                r.ReversesDependencyId == effect.OriginalEntityId).ToArray();
            if (edge.ConsumerMovementId != consumer.Uid
                || edge.ReversesDependencyId is not null
                || effect.ReplayRunId != run.Id
                || effect.RootStockPostingId != root.Id
                || effect.BusinessOwnerPostingId != owner.Id
                || effect.OriginalEntityId is not long priorId
                || !priors.TryGetValue(priorId, out var prior)
                || prior.StockPostingId != owner.Id
                || prior.ContributorMovementId != edge.ContributorMovementId
                || prior.ConsumerMovementId != edge.ConsumerMovementId
                || prior.ReversesDependencyId is not null
                || reverseRows.Length != 1
                || reverseRows[0].StockPostingId != root.Id
                || reverseRows[0].ContributorMovementId != edge.ContributorMovementId
                || reverseRows[0].ConsumerMovementId != edge.ConsumerMovementId
                || root.CompanyCode != companyCode || root.BranchCode != branchCode
                || root.LedgerEpochId != epochId || root.SealedAtUtc is null
                || root.PostingSequence <= owner.PostingSequence
                || run.CompanyCode != companyCode || run.BranchCode != branchCode
                || run.RootRequestId != root.RequestId
                || run.RootDocumentType != root.SourceDocumentType
                || run.RootDocumentId != root.SourceDocumentId
                || run.EarliestEffectiveAt > consumer.MovementDate
                || run.LatestEffectiveAt < consumer.MovementDate)
                return $"Production dependency {edge.Id} has no exact foreign-root business ownership proof.";
        }
        return null;
    }
}
