using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Filter/paging criteria for the posted-movement inquiry over <c>dbo.IvTrxHistory</c>.
///
/// <para>
/// <b>There is deliberately no batch-status filter.</b> History rows are written with
/// <c>BatchStatus = POSTED</c> only, and a rollback DELETES that batch's history, so there is
/// nothing cancelled to filter out. <see cref="IStatuses"/> filters the <em>item/stock</em> status
/// (<c>IvTrxHistory.IStatus</c>), which is a different concept from the batch status.
/// </para>
///
/// <para>
/// Like <see cref="IvBalanceLotQuery"/> every filter is optional and the status list is an
/// <em>inclusion</em> list: empty means ALL statuses, never "none".
/// </para>
/// </summary>
public sealed class IvTrxHistoryQuery
{
    public string? ICode { get; set; }

    /// <summary>Warehouse filter — matches EITHER leg (from or to), because a transfer touches two.</summary>
    public string? WhCode { get; set; }

    /// <summary>Bin/location filter — matches EITHER leg.</summary>
    public string? LocCode { get; set; }

    /// <summary>Lot filter — matches EITHER leg.</summary>
    public string? LotNo { get; set; }

    /// <summary>Explicit transaction-type list (<c>IvTrxTypes</c>). Empty means ALL types.</summary>
    public IReadOnlyList<string> TrxTypes { get; set; } = [];

    /// <summary>Exact batch number.</summary>
    public int? BatchNo { get; set; }

    /// <summary>Batch reference (<c>IvTrxBatch.RefNo</c>) — substring match.</summary>
    public string? RefNo { get; set; }

    /// <summary>Matches ANY of <c>DONo</c>, <c>InvNo</c>, <c>SO_No</c>, <c>PO_No</c> — substring match.</summary>
    public string? DocumentNo { get; set; }

    /// <summary>Item-status inclusion list. Empty means ALL statuses (including SCRAPS).</summary>
    public IReadOnlyList<string> IStatuses { get; set; } = [];

    /// <summary>Movement date range, inclusive by day (half-open: <c>&gt;= from</c> and <c>&lt; to+1</c>).</summary>
    public DateTime? TrxDateFrom { get; set; }
    public DateTime? TrxDateTo { get; set; }

    /// <summary>Free text over item code, description, product code, reference and remarks.</summary>
    public string? SearchText { get; set; }

    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// One posted movement line. <see cref="InQty"/>/<see cref="OutQty"/> are <b>scope-aware</b> and are
/// filled by the service after materialisation: a leg only counts when it lands inside the requested
/// stock slice. <see cref="Reason"/> and <see cref="EstValue"/> are likewise service-computed
/// (<c>Remarks</c> parsing and the price-visibility masking), because both live above the Model layer.
/// </summary>
public sealed class IvTrxHistoryRow
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public int TrxLineNo { get; init; }
    public DateTime TrxDtTime { get; init; }
    public string TrxType { get; init; } = string.Empty;
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; init; }

    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? ProdCode { get; init; }

    public string? FrWarehouse { get; init; }
    public string? FrLocation { get; init; }
    public string? FrLotNo { get; init; }
    public decimal? FrStdQty { get; init; }
    public string? FrStdUom { get; init; }

    public string? ToWarehouse { get; init; }
    public string? ToLocation { get; init; }
    public string? ToLotNo { get; init; }
    public decimal? ToStdQty { get; init; }
    public string? ToStdUom { get; init; }

    public string? IStatus { get; init; }
    public string? DoNo { get; init; }
    public string? InvNo { get; init; }
    public string? SoNo { get; init; }
    public string? PoNo { get; init; }
    public string? Remarks { get; init; }

    /// <summary>Qty received INTO the requested slice (0 when this line does not receive into it).</summary>
    public decimal InQty { get; set; }

    /// <summary>Qty issued OUT of the requested slice (0 when this line does not issue from it).</summary>
    public decimal OutQty { get; set; }

    /// <summary><c>InQty - OutQty</c>, populated by the service so the grid column has a real setter.</summary>
    public decimal NetQty { get; set; }

    /// <summary>Standard UOM of the moving quantity, in the direction that has one.</summary>
    public string? StdUom { get; set; }

    /// <summary>Adjustment reason parsed from <c>Remarks</c> (service-computed, <c>D2</c> V1).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Stock Card only: the cumulative quantity after this line (<c>OpeningQty + Σ NetQty</c>).
    /// <c>null</c> on the transaction inquiry, where no running balance is defined.
    /// </summary>
    public decimal? RunningQty { get; set; }

    /// <summary>Effective per-UOM price: <c>IvTrxHistory.UnitPrice ?? IvStockMaster.PurchasePrice</c>.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>
    /// Signed <c>IvQty.Round(NetQty * UnitPrice)</c>, or <c>null</c> when the caller may not view money.
    /// <c>IvTrxHistory.Cost</c>/<c>CostPrice</c>/<c>AsNowCost</c> are deliberately NOT the basis (D13).
    /// </summary>
    public decimal? ExactTransferredValue { get; set; }
    public decimal? EstValue { get; set; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }

    /// <summary>
    /// Always <c>null</c>: <c>IvTrxHistory</c> has <c>Updated</c> (<c>ModifiedDate</c>) but no
    /// <c>UpdatedUID</c>. The property exists only because <c>DxGridDataColumn.FieldName</c> resolves
    /// by property name at render time; no column was invented.
    /// </summary>
    public string? ModifiedBy { get; init; }
}

/// <summary>
/// Period aggregate over the SAME predicate as the grid, computed in SQL as
/// <c>SUM(in-leg) - SUM(out-leg)</c> so no conditional expression ever reaches the expression tree.
/// <see cref="TotalValue"/> is masked by the service exactly like the row <c>EstValue</c>.
/// </summary>
public sealed class IvTrxHistorySummary
{
    public int TotalRows { get; init; }
    public int BatchCount { get; init; }
    public decimal InQty { get; init; }
    public decimal OutQty { get; init; }

    /// <summary>Net effect of stock adjustments inside the period (<c>TrxType = 'ADJ'</c>).</summary>
    public decimal AdjustNetQty { get; init; }

    public decimal NetQty => InQty - OutQty;

    /// <summary>Raw SQL <c>SUM(qty * price)</c>; the service rounds it and masks it.</summary>
    public decimal? TotalValue { get; set; }
}

/// <summary>Paged rows plus the match count for the same predicate.</summary>
public sealed class IvTrxHistoryPage
{
    public IReadOnlyList<IvTrxHistoryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>
/// The stock slice a movement line is judged against — <c>(ICode, WhCode, LocCode, LotNo, IStatus)</c>
/// within company+branch, i.e. one <see cref="IvBalLoc"/> pile (D12).
///
/// <para>
/// A <c>null</c> part is a wildcard. <c>ICode</c>/<c>IStatus</c> live on the movement row itself;
/// warehouse/bin/lot are per-leg, so a leg is matched independently and the two legs may disagree —
/// which is exactly what makes a transfer's "in" and "out" distinguishable.
/// </para>
///
/// <para>
/// <b>Scope key:</b> <see cref="Create"/> is the single definition of "the filter is a slice", shared
/// with <see cref="IvStockSliceKey"/>'s column order so the Stock Card's opening balance and the pile
/// the user is looking at can never drift apart.
/// </para>
/// </summary>
public sealed record IvTrxHistoryScope(
    string? ICode,
    string? WhCode,
    string? LocCode,
    string? LotNo,
    string? IStatus)
{
    public static readonly IvTrxHistoryScope Empty = new(null, null, null, null, null);

    public bool IsEmpty =>
        ICode is null && WhCode is null && LocCode is null && LotNo is null && IStatus is null;

    /// <summary>
    /// The scope implied by a query. The status part is pinned only when exactly ONE status is
    /// selected, because a movement row carries a single <c>IStatus</c> column rather than a status
    /// per leg — a multi-status selection cannot be expressed as one slice.
    /// </summary>
    public static IvTrxHistoryScope FromQuery(IvTrxHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var statuses = (query.IStatuses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new IvTrxHistoryScope(
            Normalize(query.ICode),
            Normalize(query.WhCode),
            Normalize(query.LocCode),
            Normalize(query.LotNo),
            statuses.Count == 1 ? statuses[0] : null);
    }

    /// <summary>The 7-part pile key this scope describes, or <c>null</c> when the scope is not a pile.</summary>
    public IvStockSliceKey? ToSliceKey(string companyCode, string branchCode) =>
        IsEmpty
            ? null
            : IvStockSliceKey.Create(
                companyCode,
                branchCode,
                ICode ?? string.Empty,
                WhCode ?? string.Empty,
                LocCode,
                LotNo,
                IStatus);

    public bool MatchesFromLeg(string iCode, string? frWarehouse, string? frLocation, string? frLotNo, string? iStatus) =>
        MatchesLeg(iCode, frWarehouse, frLocation, frLotNo, iStatus);

    public bool MatchesToLeg(string iCode, string? toWarehouse, string? toLocation, string? toLotNo, string? iStatus) =>
        MatchesLeg(iCode, toWarehouse, toLocation, toLotNo, iStatus);

    private bool MatchesLeg(string iCode, string? warehouse, string? location, string? lotNo, string? iStatus) =>
        (ICode is null || Same(ICode, iCode))
        && (WhCode is null || Same(WhCode, warehouse))
        && (LocCode is null || Same(LocCode, location))
        && (LotNo is null || Same(LotNo, lotNo))
        && (IStatus is null || Same(IStatus, iStatus));

    private static bool Same(string expected, string? actual) =>
        string.Equals(expected, (actual ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Stock Card: the period ledger for one slice, with the opening balance computed over every
/// movement strictly before the period and the running balance over the period's own rows.
/// </summary>
public sealed class IvStockCardPage
{
    public IReadOnlyList<IvTrxHistoryRow> Rows { get; init; } = [];

    /// <summary>Rows the grid can actually serve (equals <see cref="Rows"/>'s count).</summary>
    public int TotalCount { get; init; }

    /// <summary>True match count. Greater than <see cref="TotalCount"/> only when the ledger was capped.</summary>
    public int MatchingCount { get; init; }

    /// <summary>Net qty of every in-scope movement with <c>TrxDtTime &lt; TrxDateFrom</c>.</summary>
    public decimal OpeningQty { get; init; }

    public decimal InQty { get; init; }
    public decimal OutQty { get; init; }
    public decimal AdjustNetQty { get; init; }
    public decimal NetQty => InQty - OutQty;

    /// <summary><c>OpeningQty + NetQty</c> — the ledger's own closing figure for the period.</summary>
    public decimal LedgerClosingQty => OpeningQty + NetQty;

    /// <summary>
    /// Live <c>SUM(IvBalLoc.StdQty)</c> over the same slice, or <c>null</c> when the live figure is not
    /// meaningful for the requested scope (no item pinned... or no pile at all).
    /// </summary>
    public decimal? LiveQty { get; init; }

    /// <summary><c>LiveQty - LedgerClosingQty</c>. Non-zero means legacy/imported on-hand with no
    /// matching history — the documented opening-balance caveat, surfaced rather than hidden.</summary>
    public decimal? DifferenceQty => LiveQty is null ? null : LiveQty.Value - LedgerClosingQty;

    /// <summary>True when the ledger exceeded <c>IvStockHistoryRepository.MaxStockCardRows</c>.</summary>
    public bool Truncated { get; init; }

    public decimal? TotalValue { get; init; }
}

/// <summary>Server-side sort whitelist. An unknown field falls back to the default order.</summary>
public static class IvTrxHistorySortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvTrxHistoryRow.TrxDtTime),
        nameof(IvTrxHistoryRow.TrxType),
        nameof(IvTrxHistoryRow.BatchNo),
        nameof(IvTrxHistoryRow.RefNo),
        nameof(IvTrxHistoryRow.ICode),
        nameof(IvTrxHistoryRow.IDesc),
        nameof(IvTrxHistoryRow.IStatus),
        nameof(IvTrxHistoryRow.NetQty),
        nameof(IvTrxHistoryRow.InQty),
        nameof(IvTrxHistoryRow.OutQty)
    };
}

/// <summary>
/// The transaction-type tokens the history reads need. Mirrors <c>ErpWeb.Core.Inventory.IvTrxTypes</c>,
/// which the Model layer cannot reference; the value is a PERSISTED contract, so the two must never
/// drift — pinned by a test, because both are <c>const</c>.
/// </summary>
public static class IvTrxHistoryTypes
{
    /// <summary>Stock adjustment (<c>ADJ</c>) — also what a posted Stock Count writes.</summary>
    public const string StockAdjustment = "ADJ";
}

/// <summary>Internal query shape produced by the single shared composition.</summary>
public sealed class IvTrxHistorySlice
{
    public IvTrxHistory Trx { get; set; } = null!;

    /// <summary>LEFT-joined item master — used for <c>PurchasePrice</c>, never to filter.</summary>
    public IvStockMaster? Sm { get; set; }
}
