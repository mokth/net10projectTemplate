using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// The stock-control alert inquiry: one page whose Rule selector delivers Low, Over, Slow, Dead,
/// Never moved, Expiring and Expired.
///
/// <para>
/// <b>D14.</b> <c>AsOfDate</c> is resolved ONCE here from <see cref="ICurrentDateService"/>'s
/// company-local clock and handed to every rule, so the seven rules cannot disagree about what "today"
/// is and no rule mixes the server clock, the SQL clock and the application clock.
/// </para>
///
/// <para>
/// <b>Threshold convention.</b> A <c>MinStock</c>/<c>MaxStock</c> that is NULL <em>or zero</em> means
/// "not configured" and never alerts — the guard is explicit so NULL can never silently become 0.
/// </para>
///
/// <para>
/// There is no money column on this screen, so no <c>VIEW_PRICE</c> check is needed; ACCESS is the
/// boundary and EXPORT is checked inside the endpoint as well as here.
/// </para>
/// </summary>
public interface IIvStockAlertService
{
    /// <summary>Paged alert rows for the selected rule. ACCESS-gated.</summary>
    Task<IvMasterOperationResult<IvStockAlertPage>> SearchAsync(
        string menuCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate over the SAME predicate as <see cref="SearchAsync"/>.</summary>
    Task<IvMasterOperationResult<IvStockAlertSummary>> GetSummaryAsync(
        string menuCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows for the xlsx export. ACCESS-gated; the endpoint additionally checks EXPORT and the row cap.
    /// Reuses the same composition, so <c>TotalCount</c> equals the grid's.
    /// </summary>
    Task<IvMasterOperationResult<IvStockAlertPage>> ExportRowsAsync(
        string menuCode,
        IvStockAlertQuery query,
        CancellationToken cancellationToken = default);
}
