namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// The stock-control alert rules. The token is a filter value only (never persisted), so it lives in
/// the Model layer beside the query that consumes it.
///
/// <para>
/// Splitting <c>DEAD</c> from <c>NEVER_MOVED</c> is deliberate: an item with opening/legacy on-hand and
/// <b>no</b> history at all is not "dead" — nothing ever moved, so nothing can have gone stale. Folding
/// the two together would report a never-used item as a stock-holding that stopped selling.
/// </para>
///
/// <para>
/// <b>D21:</b> every movement/age rule (<c>SLOW</c>, <c>DEAD</c>, <c>NEVER_MOVED</c>) requires
/// <c>onHand &gt; 0</c> — an item with no stock anywhere is not an operational stock alert. <c>LOW</c>
/// and <c>OVER</c> are the deliberate opposite: a never-stocked item IS a low-stock alert.
/// </para>
/// </summary>
public static class IvStockAlertRules
{
    /// <summary>On-hand below <c>IvStockMaster.MinStock</c>.</summary>
    public const string Low = "LOW";

    /// <summary>On-hand above <c>IvStockMaster.MaxStock</c>.</summary>
    public const string Over = "OVER";

    /// <summary>Has stock but has not moved within <c>SlowDays</c>.</summary>
    public const string Slow = "SLOW";

    /// <summary>Has stock but has not moved within <c>DeadDays</c>.</summary>
    public const string Dead = "DEAD";

    /// <summary>Has stock and no posted movement history at all (opening / imported stock).</summary>
    public const string NeverMoved = "NEVER_MOVED";

    /// <summary>Lot expires between today and <c>AsOfDate + ExpiryDays</c> (inclusive).</summary>
    public const string Expiring = "EXPIRING";

    /// <summary>Lot expired strictly before <c>AsOfDate</c>. A lot expiring TODAY is not expired.</summary>
    public const string Expired = "EXPIRED";

    public const string Default = Low;

    public static readonly IReadOnlyList<string> All =
        [Low, Over, Slow, Dead, NeverMoved, Expiring, Expired];

    public static bool IsKnown(string? rule) =>
        rule is not null && All.Contains(rule.Trim().ToUpperInvariant());

    /// <summary>True for the rules driven by <c>IvLot</c> rather than by the item master.</summary>
    public static bool IsLotRule(string? rule) =>
        string.Equals(rule, Expiring, StringComparison.OrdinalIgnoreCase)
        || string.Equals(rule, Expired, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the rule needs a movement history aggregate.</summary>
    public static bool IsMovementRule(string? rule) =>
        string.Equals(rule, Slow, StringComparison.OrdinalIgnoreCase)
        || string.Equals(rule, Dead, StringComparison.OrdinalIgnoreCase)
        || string.Equals(rule, NeverMoved, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalized rule token, falling back to <see cref="Default"/> for anything unknown.</summary>
    public static string Normalize(string? rule) =>
        IsKnown(rule) ? rule!.Trim().ToUpperInvariant() : Default;

    /// <summary>Human caption used by the page's rule chips and the xlsx sheet name.</summary>
    public static string Describe(string? rule) => Normalize(rule) switch
    {
        Low => "Low stock",
        Over => "Overstock",
        Slow => "Slow moving",
        Dead => "Dead stock",
        NeverMoved => "Never moved",
        Expiring => "Expiring lots",
        Expired => "Expired lots",
        _ => "Stock alert"
    };
}

/// <summary>
/// Filter criteria for the stock-alert grid.
///
/// <para>
/// <see cref="AsOfDate"/> is <b>supplied by the service</b> from <c>ICurrentDateService</c>'s company-local
/// clock (D14) — it is deliberately not read here, so all seven rules compare against one date and the
/// Model layer stays free of tenant/time concerns. Never mix the server clock, the SQL clock and the
/// application clock.
/// </para>
/// </summary>
public sealed class IvStockAlertQuery
{
    /// <summary>One of <see cref="IvStockAlertRules"/>; an unknown token falls back to LOW.</summary>
    public string Rule { get; set; } = IvStockAlertRules.Default;

    /// <summary>The company-local date every rule is evaluated against (D14). Set by the service.</summary>
    public DateTime AsOfDate { get; set; } = DateTime.Today;

    public string? ICode { get; set; }

    /// <summary>
    /// Warehouse filter. Empty means "all warehouses of the branch", and that is also the threshold
    /// basis reported on every row ("Threshold Basis" — D3/D15).
    /// </summary>
    public string? WhCode { get; set; }

    public string? IClassCode { get; set; }

    /// <summary>Free text over item code and description.</summary>
    public string? SearchText { get; set; }

    /// <summary>SLOW threshold in days. A filter parameter, never a hard-coded constant.</summary>
    public int SlowDays { get; set; } = 90;

    /// <summary>DEAD threshold in days. Must be greater than <see cref="SlowDays"/>.</summary>
    public int DeadDays { get; set; } = 180;

    /// <summary>EXPIRING horizon in days.</summary>
    public int ExpiryDays { get; set; } = 30;

    /// <summary>
    /// Inclusion toggles, same convention as <see cref="IvBalanceLotQuery"/>: <c>true</c> means
    /// "do not restrict on this category". Both default to <c>false</c> — a deactivated item's stock is
    /// a disposal question rather than a movement alert, and a <c>StockControl = false</c> item is not a
    /// stock item at all, so neither is an operational alert unless the user opts in.
    /// </summary>
    public bool IncludeInactive { get; set; }

    public bool IncludeNonStockControl { get; set; }

    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// One alert line (D15): the value that breached, the threshold it breached, and the basis that
/// threshold was measured over. An alert a user cannot explain is not actionable.
///
/// <para>
/// The columns fall into three groups and each rule fills its own subset, so the page can show the
/// lot columns only for the expiry rules:
/// <list type="bullet">
///   <item><description>always — item, description, class, std UOM, on-hand, threshold basis;</description></item>
///   <item><description><c>LOW</c>/<c>OVER</c> — min stock, max stock, variance;</description></item>
///   <item><description><c>SLOW</c>/<c>DEAD</c>/<c>NEVER_MOVED</c> — last movement, days since movement;</description></item>
///   <item><description><c>EXPIRING</c>/<c>EXPIRED</c> — warehouse, lot no., expiry date, days to expiry.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <see cref="Variance"/>, <see cref="DaysSinceMovement"/> and <see cref="DaysToExpiry"/> are settable
/// and filled by the service: the grid resolves <c>DxGridDataColumn.FieldName</c> by property name and
/// a get-only computed property cannot be bound.
/// </para>
/// </summary>
public sealed class IvStockAlertRow
{
    /// <summary>
    /// Stable, unique row identity for the grid — the item code alone is not unique on the expiry rules,
    /// where one extra row exists per warehouse holding the lot.
    /// </summary>
    public string RowKey { get; set; } = string.Empty;

    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? IClassCode { get; init; }
    public string? StdUom { get; init; }

    public decimal? MinStock { get; init; }
    public decimal? MaxStock { get; init; }
    public decimal OnHand { get; init; }

    /// <summary><c>OnHand − MinStock</c> on LOW, <c>OnHand − MaxStock</c> on OVER (service-computed).</summary>
    public decimal? Variance { get; set; }

    public DateTime? LastMovement { get; init; }

    /// <summary><c>AsOfDate − LastMovement</c> in whole days; <c>null</c> when nothing ever moved.</summary>
    public int? DaysSinceMovement { get; set; }

    public string? WhCode { get; init; }
    public string? LocCode { get; init; }
    public string? LotNo { get; init; }
    public DateTime? ExpiryDate { get; init; }

    /// <summary><c>ExpiryDate − AsOfDate</c> in whole days; negative once expired.</summary>
    public int? DaysToExpiry { get; set; }

    /// <summary>"All warehouses" or the selected warehouse code — what the threshold was measured over.</summary>
    public string ThresholdBasis { get; init; } = string.Empty;

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

/// <summary>Aggregate over the SAME predicate as the grid, computed in SQL.</summary>
public sealed class IvStockAlertSummary
{
    public int TotalRows { get; init; }

    /// <summary>Distinct items behind <see cref="TotalRows"/> (lot rules can return several rows per item).</summary>
    public int ItemCount { get; init; }

    public decimal TotalOnHand { get; init; }
}

/// <summary>Paged alert rows plus the unbounded match count for the same predicate.</summary>
public sealed class IvStockAlertPage
{
    public IReadOnlyList<IvStockAlertRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }

    /// <summary>
    /// The company-local date the service actually evaluated the rules against (D14). Returned so the
    /// screen can display the same instant it was measured at instead of re-deriving one.
    /// </summary>
    public DateTime AsOfDate { get; init; }
}

/// <summary>Server-side sort whitelist. An unknown field falls back to the rule's default order.</summary>
public static class IvStockAlertSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvStockAlertRow.ICode),
        nameof(IvStockAlertRow.IDesc),
        nameof(IvStockAlertRow.IClassCode),
        nameof(IvStockAlertRow.OnHand),
        nameof(IvStockAlertRow.MinStock),
        nameof(IvStockAlertRow.MaxStock),
        nameof(IvStockAlertRow.LastMovement),
        nameof(IvStockAlertRow.ExpiryDate),
        nameof(IvStockAlertRow.LotNo),
        nameof(IvStockAlertRow.WhCode)
    };
}
