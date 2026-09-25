using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Posted-movement inquiries over <c>dbo.IvTrxHistory</c> — the transaction inquiry (and, through its
/// TrxType selector, adjustment inquiry / analysis) and the stock card.
///
/// <para>
/// One service serves two screens, so every member takes the <paramref name="menuCode"/> the caller is
/// serving. That is not decoration: ACCESS and the money-visibility permission are both checked against
/// the <em>screen's own</em> menu, and only the page knows which screen it is.
/// </para>
///
/// <para>
/// Tenant scope is resolved FIRST from <see cref="IInventoryTenantContext.TryBranchScope"/>
/// (fail-closed), then ACCESS, then — for value-bearing results — <c>VIEW_PRICE</c>, before any data is
/// read. History rows are written on POSTED batches only and a rollback DELETES them, so cancelled or
/// rolled-back documents can never appear; the pages state that rather than pretending to filter it.
/// </para>
/// </summary>
public interface IIvTrxHistoryService
{
    /// <summary>Paged movement rows for the grid. ACCESS-gated.</summary>
    Task<IvMasterOperationResult<IvTrxHistoryPage>> SearchAsync(
        string menuCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Period summary over the SAME predicate as <see cref="SearchAsync"/>.</summary>
    Task<IvMasterOperationResult<IvTrxHistorySummary>> GetSummaryAsync(
        string menuCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows for the xlsx export. ACCESS-gated; the endpoint additionally checks EXPORT and the row cap.
    /// Reuses the same composition, so <c>TotalCount</c> equals the grid's.
    /// </summary>
    Task<IvMasterOperationResult<IvTrxHistoryPage>> ExportRowsAsync(
        string menuCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The stock card: the period ledger in chronological order with an opening and a running balance,
    /// the period summary, and the live pile quantity for comparison (D12).
    ///
    /// <para>
    /// Requires an item and a from-date: without an item the "card" degenerates into the transaction
    /// inquiry, and without a period start the opening balance is undefined.
    /// </para>
    /// </summary>
    Task<IvMasterOperationResult<IvStockCardPage>> GetStockCardAsync(
        string menuCode,
        IvTrxHistoryQuery query,
        CancellationToken cancellationToken = default);
}
