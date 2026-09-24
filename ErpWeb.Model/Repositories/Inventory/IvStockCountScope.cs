namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Scope criteria for a stock-count Generate. Every field is optional; the query always keeps the
/// company/branch tenant filter and always excludes non-stock-controlled items.
///
/// Scope rules (deliberately decided, not inherited from the legacy cycle-count query):
///  • <see cref="IncludeZeroQty"/> = false keeps only positive balances; true keeps positive AND zero
///    rows. The query is never unfiltered by tenant.
///  • <see cref="IncludeInactive"/> = false drops items whose <c>IvStockMaster</c> is not active.
///    <c>StockControl = false</c> items are excluded unconditionally.
///  • Status 'SCRAPS' is excluded by default (legacy parity); an explicit status scope adds it back.
///  • The legacy "TransDate &lt;= StartCount" as-at filter is deliberately NOT ported: TransDate is
///    mutable (every stock move overwrites it), so the scope is "the piles that exist now".
/// </summary>
public sealed class IvStockCountScope
{
    /// <summary>Excluded by default when the caller supplies no status list.</summary>
    public const string ScrapStatus = "SCRAPS";

    public string? WHCode { get; init; }
    public string? LocCode { get; init; }
    public string? IClassCode { get; init; }
    public string? ISubClassCode { get; init; }
    public string? IType { get; init; }

    /// <summary>Explicit item-status scope. Empty means "everything except SCRAPS".</summary>
    public IReadOnlyList<string> Statuses { get; init; } = [];

    /// <summary>Optional item IN-list. Empty means no item filter.</summary>
    public IReadOnlyList<string> ICodes { get; init; } = [];

    public bool IncludeZeroQty { get; init; } = true;
    public bool IncludeInactive { get; init; }
}
