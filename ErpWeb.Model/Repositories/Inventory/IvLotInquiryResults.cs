namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Filter criteria for the Lot / Batch inquiry over <c>dbo.IvLot</c>.
///
/// <para>
/// <b>Terminology.</b> "Lot" here is the inventory traceability lot (<c>IvLot</c>, identified by
/// <c>ICode + LotNo</c>). A "batch" in this ERP is <c>IvTrxBatch</c> — a <em>document</em>, not a
/// manufacturing batch — and it is reachable from the Transaction Inquiry page, never from here. The
/// screen is nonetheless titled "Lot / Batch Inquiry" so a user searching for "batch" finds it, and it
/// carries a legend saying which is which.
/// </para>
///
/// <para>Every filter is optional and the status list is an inclusion list: empty means ALL.</para>
/// </summary>
public sealed class IvLotInquiryQuery
{
    public string? ICode { get; set; }

    /// <summary>Substring match over <c>IvLot.LotNo</c>.</summary>
    public string? LotNo { get; set; }

    /// <summary>Free text over item code, item description, lot no., source document and supplier.</summary>
    public string? SearchText { get; set; }

    /// <summary>Expiry window, inclusive by day (half-open internally: <c>&gt;= from</c>, <c>&lt; to+1</c>).</summary>
    public DateTime? ExpiryFrom { get; set; }
    public DateTime? ExpiryTo { get; set; }

    /// <summary>Exact QC status (<c>IvLot.QcStatus</c>).</summary>
    public string? QcStatus { get; set; }

    /// <summary>Inclusion toggle: <c>true</c> (default) does not restrict on <c>IvLot.IsActive</c>.</summary>
    public bool IncludeInactive { get; set; } = true;

    /// <summary>Supplied by the service from <c>ICurrentDateService</c>; drives age and days-to-expiry.</summary>
    public DateTime AsOfDate { get; set; } = DateTime.Today;

    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

/// <summary>
/// The lot passport: origin, dates and QC status, plus the aggregate its on-hand piles add up to.
///
/// <para>
/// <see cref="AgeDays"/> / <see cref="DaysToExpiry"/> are settable and filled by the service, because
/// the grid binds them by property name. <see cref="OnHandQty"/> / <see cref="PileCount"/> are SQL
/// aggregates over the branch's piles.
/// </para>
/// </summary>
public sealed class IvLotInquiryRow
{
    public int LotId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string LotNo { get; init; } = string.Empty;

    /// <summary>How the lot came into stock (goods receipt, misc. receipt, …).</summary>
    public string? SourceType { get; init; }
    public string? SourceDocNo { get; init; }
    public string? SupplierCode { get; init; }

    public DateTime? ReceiptDate { get; init; }
    public DateTime? MfgDate { get; init; }
    public DateTime? ExpiryDate { get; init; }

    /// <summary>Days from <see cref="ReceiptDate"/> to the as-of date. The lot-level ageing figure.</summary>
    public int? AgeDays { get; set; }

    /// <summary>Days from the as-of date to <see cref="ExpiryDate"/>; negative once expired.</summary>
    public int? DaysToExpiry { get; set; }

    public string? QcStatus { get; init; }
    public string? Remarks { get; init; }
    public bool IsActive { get; init; }

    public decimal OnHandQty { get; init; }
    public int PileCount { get; init; }
    public string? StdUom { get; init; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

/// <summary>
/// One on-hand pile of a lot (<c>IvBalLoc</c>), i.e. one warehouse × bin × status holding of that lot.
/// <see cref="ExpiryDate"/> is carried on the pile row so the child grid does not need the lot header.
/// </summary>
public sealed class IvLotPileRow
{
    public int Id { get; init; }
    public string WhCode { get; init; } = string.Empty;
    public string? WhDesc { get; init; }
    public string LocCode { get; init; } = string.Empty;
    public string IStatus { get; init; } = string.Empty;
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public DateTime? TransDate { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string? RefNo { get; init; }
    public string? PoNo { get; init; }
    public string? Remarks { get; init; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }

    /// <summary>Always <c>null</c> — <c>IvBalLoc</c> has no <c>ModifiedBy</c> column; kept for the grid.</summary>
    public string? ModifiedBy { get; init; }
}

/// <summary>Paged lot rows plus the unbounded match count for the same predicate.</summary>
public sealed class IvLotInquiryPage
{
    public IReadOnlyList<IvLotInquiryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>
/// One lot's child panels: its on-hand piles and its posted movements. Movements reuse
/// <see cref="IvTrxHistoryRow"/> so the child grid shares the transaction inquiry's columns and its
/// scope-aware "in"/"out" semantics — <c>FromLotId</c>/<c>ToLotId</c> are both indexed on
/// <c>IvTrxHistory</c>.
/// </summary>
public sealed class IvLotInquiryDetail
{
    public IvLotInquiryRow? Lot { get; init; }
    public IReadOnlyList<IvLotPileRow> Piles { get; init; } = [];
    public IReadOnlyList<IvTrxHistoryRow> Movements { get; init; } = [];
    public decimal PileQty { get; init; }
    public decimal MovementNetQty { get; init; }
}

/// <summary>Aggregate over the SAME predicate as the grid, computed in SQL.</summary>
public sealed class IvLotInquirySummary
{
    public int TotalRows { get; init; }

    /// <summary>Distinct items behind the matching lots.</summary>
    public int ItemCount { get; init; }

    /// <summary>Lots whose expiry is strictly before the as-of date.</summary>
    public int ExpiredCount { get; init; }

    /// <summary>Lots with no expiry recorded — an ageing report cannot age them.</summary>
    public int NoExpiryCount { get; init; }

    public decimal TotalOnHandQty { get; init; }
}

/// <summary>Server-side sort whitelist. An unknown field falls back to the default order.</summary>
public static class IvLotInquirySortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvLotInquiryRow.ICode),
        nameof(IvLotInquiryRow.IDesc),
        nameof(IvLotInquiryRow.LotNo),
        nameof(IvLotInquiryRow.ReceiptDate),
        nameof(IvLotInquiryRow.ExpiryDate),
        nameof(IvLotInquiryRow.QcStatus),
        nameof(IvLotInquiryRow.SourceDocNo),
        nameof(IvLotInquiryRow.OnHandQty)
    };
}
