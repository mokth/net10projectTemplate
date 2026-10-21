using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionReplayDependencyEdge(
    long Id, long ContributorMovementId, long ConsumerMovementId, long StockPostingId);

internal sealed record ProductionReplayDependencyPlan(
    IReadOnlyList<long> OrderedMovementIds,
    IReadOnlyList<ProductionReplayDependencyEdge> Edges);

/// <summary>
/// Reads the sealed, active V2 movement graph downstream of corrected movements.
/// The order is stable across retries and a cycle or broken owner fails the plan.
/// </summary>
internal static class ProductionReplayDependencyPlanner
{
    public static async Task<ProductionReplayDependencyPlan> BuildAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<long> changedMovementIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(changedMovementIds);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || changedMovementIds.Any(id => id <= 0))
            throw Invalid("Production dependency planning requires a tenant and positive movement IDs.");
        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var roots = changedMovementIds.Distinct().Order().ToArray();
        if (roots.Length == 0)
            return new([], []);

        var seen = roots.ToHashSet();
        var frontier = roots;
        var edges = new Dictionary<long, ProductionReplayDependencyEdge>();
        var edgeRows = new Dictionary<long, ProductionPoolDependency>();
        while (frontier.Length > 0)
        {
            var rows = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
                .Where(edge => frontier.Contains(edge.ContributorMovementId))
                .ToArrayAsync(cancellationToken);
            var next = new SortedSet<long>();
            foreach (var row in rows)
            {
                if (row.Id <= 0 || row.StockPostingId <= 0
                    || row.ContributorMovementId <= 0 || row.ConsumerMovementId <= 0)
                    throw Invalid("Production dependency has an invalid identity.");
                if (row.ContributorMovementId == row.ConsumerMovementId)
                    throw Invalid($"Production dependency cycle includes movement {row.ConsumerMovementId}.");
                edges[row.Id] = new(row.Id, row.ContributorMovementId,
                    row.ConsumerMovementId, row.StockPostingId);
                edgeRows[row.Id] = row;
                if (seen.Add(row.ConsumerMovementId))
                    next.Add(row.ConsumerMovementId);
            }
            frontier = next.ToArray();
        }

        var ids = seen.Order().ToArray();
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(movement => ids.Contains(movement.Uid))
            .ToDictionaryAsync(movement => movement.Uid, cancellationToken);
        if (movements.Count != ids.Length)
            throw Invalid("Production dependency references a missing movement.");
        var postingIds = movements.Values.Select(m => m.StockPostingId ?? 0)
            .Concat(edges.Values.Select(e => e.StockPostingId)).Distinct().ToArray();
        if (postingIds.Contains(0))
            throw Invalid("Production dependency movement has no posting owner.");
        var postings = await db.StockPostings.AsNoTracking()
            .Where(posting => postingIds.Contains(posting.Id))
            .ToDictionaryAsync(posting => posting.Id, cancellationToken);
        if (postings.Count != postingIds.Length)
            throw Invalid("Production dependency has a missing posting owner.");
        var epochs = movements.Values.Select(m => m.LedgerEpochId).Distinct().ToArray();
        if (epochs.Length != 1 || epochs[0] is null)
            throw Invalid("Production dependency crosses ledger epochs.");
        foreach (var movement in movements.Values)
        {
            var owner = postings[movement.StockPostingId!.Value];
            if (movement.CompanyCode != company || movement.BranchCode != branch
                || movement.LedgerVersion != 2 || movement.LedgerEpochId != epochs[0]
                || movement.OriginalMovementId is not null
                || movement.ValuationStatus != ProductionPoolValuationService.Verified
                || movement.MovementDate != owner.EffectiveAt)
                throw Invalid($"Production dependency movement {movement.Uid} is not a verified V2 original in this tenant.");
        }
        foreach (var posting in postings.Values)
            if (posting.CompanyCode != company || posting.BranchCode != branch
                || posting.LedgerEpochId != epochs[0] || posting.SealedAtUtc is null
                || posting.PostingSequence <= 0)
                throw Invalid($"Production dependency posting {posting.Id} is not a sealed tenant owner.");
        if (await db.ProductionBalLotMovements.AsNoTracking()
                .AnyAsync(m => m.OriginalMovementId != null
                    && ids.Contains(m.OriginalMovementId.Value), cancellationToken)
            || await db.StockPostings.AsNoTracking()
                .AnyAsync(p => p.SealedAtUtc != null && p.ReversesPostingId != null
                    && postingIds.Contains(p.ReversesPostingId.Value), cancellationToken))
            throw Invalid("Production dependency contains a reversed movement or owner.");

        var orderedEdges = edges.Values.OrderBy(e => e.ContributorMovementId)
            .ThenBy(e => e.ConsumerMovementId).ThenBy(e => e.Id).ToArray();
        var indegree = ids.ToDictionary(id => id, _ => 0);
        var consumersByContributor = ids.ToDictionary(id => id, _ => new SortedSet<long>());
        foreach (var edge in orderedEdges)
            if (consumersByContributor[edge.ContributorMovementId].Add(edge.ConsumerMovementId))
                indegree[edge.ConsumerMovementId]++;
        var ready = new SortedSet<long>(indegree.Where(entry => entry.Value == 0)
            .Select(entry => entry.Key));
        var ordered = new List<long>(ids.Length);
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            ordered.Add(id);
            foreach (var consumer in consumersByContributor[id])
                if (--indegree[consumer] == 0)
                    ready.Add(consumer);
        }
        if (ordered.Count != ids.Length)
        {
            var remaining = indegree.Where(entry => entry.Value > 0)
                .Select(entry => entry.Key).Order().ToArray();
            throw Invalid($"Production dependency cycle includes movement IDs {string.Join(", ", remaining)}.");
        }
        foreach (var group in edgeRows.Values.GroupBy(edge => edge.ConsumerMovementId))
        {
            var consumer = movements[group.Key];
            if (await ProductionDependencyOwnerReader.ValidateForConsumerAsync(
                    db, company, branch, consumer, group.ToArray(), cancellationToken)
                is string ownerError)
                throw Invalid(ownerError);
        }
        return new(ordered, orderedEdges);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
