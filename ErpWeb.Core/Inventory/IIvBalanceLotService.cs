using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>A page of <see cref="IvBalanceLotRow"/> plus the match count for the same predicate.</summary>
public sealed class IvBalanceLotPage
{
    public IReadOnlyList<IvBalanceLotRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>
/// Read-only, ACCESS-gated pile-level on-hand inquiry over <c>dbo.IvBalLoc</c>. Grid, summary and
/// export all resolve the same tenant scope and the same repository composition; the value column is
/// masked for callers without <c>CanViewPrice</c>.
/// </summary>
public interface IIvBalanceLotService
{
    /// <summary>Paged rows for the grid. Server-side sort + paging; ACCESS-gated (SC1).</summary>
    Task<IvMasterOperationResult<IvBalanceLotPage>> SearchAsync(
        IvBalanceLotQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Summary over the SAME predicate as <see cref="SearchAsync"/> (R4, N5).</summary>
    Task<IvMasterOperationResult<IvBalanceLotSummary>> GetSummaryAsync(
        IvBalanceLotQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows for the xlsx export. ACCESS-gated; the endpoint additionally checks EXPORT and the row cap.
    /// Reuses the same composition, so <c>TotalCount</c> equals the grid's (R10).
    /// </summary>
    Task<IvMasterOperationResult<IvBalanceLotPage>> ExportRowsAsync(
        IvBalanceLotQuery query,
        CancellationToken cancellationToken = default);
}
