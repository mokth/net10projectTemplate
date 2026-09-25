using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// The stock summary — server-side totals over the balance slice, grouped by Item, Warehouse,
/// Item × Warehouse or Class.
///
/// <para>
/// <b>D16 — unit presentation.</b> A row's quantity is in that item's <em>standard</em> UOM, so it is
/// only a quantity when the group holds one UOM. <c>ITEM</c> and <c>ITEM_WAREHOUSE</c> therefore carry a
/// real UOM and a plain "Total qty"; <c>WAREHOUSE</c> and <c>CLASS</c> carry no UOM at all, and the page
/// hides the quantity column unless the user opts in and relabels it "Total qty (mixed Std UOM)". A
/// management figure like "Warehouse A = 15,382" misleads even with a tooltip.
/// </para>
///
/// <para>
/// <b>D11 Option B.</b> Money visibility is the menu-based <c>VIEW_PRICE</c> permission on this page's
/// own menu. The value is an ESTIMATE (<c>bal.UnitPrice ?? item.PurchasePrice ?? 0</c>) — there is no
/// costing method and no revaluation, so it is not a general-ledger valuation — and when the caller may
/// not see price the columns are omitted entirely, never blanked.
/// </para>
/// </summary>
public interface IIvStockSummaryService
{
    /// <summary>Paged group rows for the selected grouping mode. ACCESS-gated.</summary>
    Task<IvMasterOperationResult<IvStockSummaryPage>> SearchAsync(
        string menuCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate over the SAME predicate as <see cref="SearchAsync"/>, with value masked.</summary>
    Task<IvMasterOperationResult<IvStockSummarySummary>> GetSummaryAsync(
        string menuCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows for the xlsx export. ACCESS-gated; the endpoint additionally checks EXPORT and the row cap.
    /// The value column is omitted entirely when the caller may not see price.
    /// </summary>
    Task<IvMasterOperationResult<IvStockSummaryPage>> ExportRowsAsync(
        string menuCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);
}
