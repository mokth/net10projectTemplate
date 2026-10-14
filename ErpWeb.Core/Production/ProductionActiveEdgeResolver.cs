using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Resolves the active append-only Production quantity and value edges.
/// A reversal is evidence of removal, not an active edge; its original is
/// inactive once the reversal child exists.
/// </summary>
internal static class ProductionActiveEdgeResolver
{
    public static IQueryable<ProductionMovementAllocation> Allocations(AppDbContext db) =>
        db.ProductionMovementAllocations.Where(allocation =>
            allocation.ReversesAllocationId == null
            && !db.ProductionMovementAllocations.Any(reversal =>
                reversal.ReversesAllocationId == allocation.Id));

    public static IQueryable<ProductionPoolDependency> Dependencies(AppDbContext db) =>
        db.ProductionPoolDependencyRows.Where(dependency =>
            dependency.ReversesDependencyId == null
            && !db.ProductionPoolDependencyRows.Any(reversal =>
                reversal.ReversesDependencyId == dependency.Id));
}
