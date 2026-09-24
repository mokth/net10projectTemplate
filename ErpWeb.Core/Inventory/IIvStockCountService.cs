namespace ErpWeb.Core.Inventory;

/// <summary>
/// Result envelope for every <see cref="IIvStockCountService"/> member, in the shape of
/// <c>IvStockAdjustmentOperationResult</c>.
/// </summary>
public sealed class IvStockCountOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }

    public int Id { get; init; }
    public string? CountNo { get; init; }

    /// <summary>Next free running-number sequence (Peek).</summary>
    public int PeekSequence { get; init; }

    /// <summary>How many lines a Generate produced.</summary>
    public int GeneratedLines { get; init; }

    /// <summary>The ADJ batch created by a post; NULL for the legitimate all-zero-variance post.</summary>
    public int? PostedBatchNo { get; init; }

    /// <summary>How many lines were stale when the sheet was posted.</summary>
    public int PostedStaleLines { get; init; }

    public IvStockCountDocument? Document { get; init; }
    public IvStockCountListPage? ListPage { get; init; }
    public IvStockCountPostPreview? Preview { get; init; }

    public static IvStockCountOperationResult Ok() => new() { Succeeded = true };

    public static IvStockCountOperationResult OkSaved(int id, string countNo) =>
        new() { Succeeded = true, Id = id, CountNo = countNo };

    public static IvStockCountOperationResult OkPeek(int sequence) =>
        new() { Succeeded = true, PeekSequence = sequence };

    public static IvStockCountOperationResult OkGenerated(int id, string countNo, int lines) =>
        new() { Succeeded = true, Id = id, CountNo = countNo, GeneratedLines = lines };

    public static IvStockCountOperationResult OkDocument(IvStockCountDocument document) =>
        new() { Succeeded = true, Document = document, Id = document.Id, CountNo = document.CountNo };

    public static IvStockCountOperationResult OkList(IvStockCountListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvStockCountOperationResult OkPreview(IvStockCountPostPreview preview) =>
        new() { Succeeded = true, Preview = preview, Id = preview.Id, CountNo = preview.CountNo };

    public static IvStockCountOperationResult OkPosted(int id, string countNo, int? batchNo, int staleLines) =>
        new()
        {
            Succeeded = true,
            Id = id,
            CountNo = countNo,
            PostedBatchNo = batchNo,
            PostedStaleLines = staleLines
        };

    public static IvStockCountOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvStockCountListQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class IvStockCountListRow
{
    public int Id { get; init; }
    public string CountNo { get; init; } = string.Empty;
    public DateTime CountDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? WHCode { get; init; }
    public string? IClassCode { get; init; }
    public int LineCount { get; init; }
    public int CountedLines { get; init; }
    public int? PostedBatchNo { get; init; }
    public int? PostedStaleLines { get; init; }
    public string? CountedBy { get; init; }
    public string? Remark { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

public sealed class IvStockCountListPage
{
    public IReadOnlyList<IvStockCountListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvStockCountDocument
{
    public int Id { get; init; }
    public string CountNo { get; init; } = string.Empty;
    public DateTime CountDate { get; set; }
    public string Status { get; init; } = string.Empty;

    // Scope stamps.
    public string? WHCode { get; set; }
    public string? LocCode { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? IType { get; set; }
    public IReadOnlyList<string> Statuses { get; set; } = [];
    public IReadOnlyList<string> ICodes { get; set; } = [];
    public bool IncludeZeroQty { get; set; }

    public string? CountedBy { get; set; }
    public string? Remark { get; set; }

    public int? PostedBatchNo { get; init; }
    public string? PostedBy { get; init; }
    public DateTime? PostedOn { get; init; }
    public int? PostedStaleLines { get; init; }

    public string? RolledBackBy { get; init; }
    public DateTime? RolledBackOn { get; init; }
    public string? RollbackReason { get; init; }

    /// <summary>Base64 concurrency token; the browser round-trips it verbatim on Update / SaveCounts.</summary>
    public string? RowVersion { get; init; }

    public IReadOnlyList<IvStockCountLineDto> Lines { get; init; } = [];

    public bool CanEdit => Status == IvStockCountStatuses.Draft;
    /// <summary>
    /// Count entry is open until the sheet is POSTED (roll it back to re-count) or CANCELLED. A COUNTED
    /// sheet is "counted so far", not frozen evidence — a long count is normally keyed in over days.
    /// </summary>
    public bool CanCount => Status is IvStockCountStatuses.Draft
        or IvStockCountStatuses.Counted
        or IvStockCountStatuses.RolledBack;
    public bool CanPost => Status is IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack;
    public bool CanRollback => Status == IvStockCountStatuses.Posted;
    public bool CanCancel => Status is IvStockCountStatuses.Draft
        or IvStockCountStatuses.Counted
        or IvStockCountStatuses.RolledBack;
    public bool CanDelete => Status == IvStockCountStatuses.Draft;
}

public sealed class IvStockCountLineDto
{
    public int Id { get; init; }
    public short LineNumber { get; init; }
    public int BalLocId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? WHCode { get; init; }
    public string? LocCode { get; init; }
    public string? LotNo { get; init; }
    public string IStatus { get; init; } = string.Empty;
    public string? IClassCode { get; init; }
    public string? StdUom { get; init; }
    public decimal SystemQty { get; init; }
    public decimal? PhysicalQty { get; init; }
    public decimal? SnapshotUnitPrice { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public short RecountCount { get; init; }
    public string? CountedBy { get; init; }
    public DateTime? CountedOn { get; init; }

    /// <summary>Live quantity for the count / preview screens (not part of the stored evidence).</summary>
    public decimal? LiveQty { get; init; }

    /// <summary>Live − physical, rounded through <c>IvQty.Round</c>. Positive = write-down.</summary>
    public decimal? Variance { get; init; }

    /// <summary>"INCREASE" / "DECREASE" / "NONE" for a counted line; null when not counted.</summary>
    public string? Direction { get; init; }

    /// <summary>True when the live quantity no longer matches the Generate-time SystemQty.</summary>
    public bool IsStale { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvStockCountSaveRequest
{
    public DateTime CountDate { get; set; }
    public string? WHCode { get; set; }
    public string? LocCode { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? IType { get; set; }
    public IReadOnlyList<string>? Statuses { get; set; }
    public IReadOnlyList<string>? ICodes { get; set; }
    public bool IncludeZeroQty { get; set; } = true;
    public string? CountedBy { get; set; }
    public string? Remark { get; set; }

    /// <summary>Required by Update; base64 token from the loaded document.</summary>
    public string? RowVersion { get; set; }
}

public sealed class IvStockCountLineCountRequest
{
    public int BalLocId { get; set; }
    public decimal? PhysicalQty { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class IvStockCountPreviewLine
{
    public short LineNumber { get; init; }
    public int BalLocId { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? WHCode { get; init; }
    public string? LocCode { get; init; }
    public string? LotNo { get; init; }
    public string? Uom { get; init; }
    public decimal SystemQty { get; init; }
    public decimal? LiveQty { get; init; }
    public decimal? PhysicalQty { get; init; }
    public decimal? Variance { get; init; }
    public string? Direction { get; init; }
    public bool IsStale { get; init; }
    public decimal? SnapshotUnitPrice { get; init; }

    /// <summary>Per-line blocker (missing balance, slice mismatch, negative count). Null when fine.</summary>
    public string? Error { get; init; }
}

public sealed class IvStockCountPostPreview
{
    public int Id { get; init; }
    public string CountNo { get; init; } = string.Empty;
    public DateTime CountDate { get; init; }
    public string Status { get; init; } = string.Empty;

    /// <summary>Non-null when the CountDate is not postable (future / beyond MaxBackdateDays).</summary>
    public string? DateError { get; init; }

    public int IncreaseLines { get; init; }
    public int DecreaseLines { get; init; }
    public int ZeroVarianceLines { get; init; }
    public int NotCountedLines { get; init; }
    public int StaleLines { get; init; }
    public int ErrorLines { get; init; }

    public decimal TotalIncrease { get; init; }
    public decimal TotalDecrease { get; init; }

    public IReadOnlyList<IvStockCountPreviewLine> Lines { get; init; } = [];

    public bool RequiresStaleConfirmation => StaleLines > 0;

    public bool CanPost =>
        DateError is null
        && ErrorLines == 0
        && (IncreaseLines + DecreaseLines + ZeroVarianceLines) > 0;
}

public interface IIvStockCountService
{
    /// <summary>Next free <c>CC</c> sequence for the current company, without consuming it.</summary>
    Task<IvStockCountOperationResult> PeekNextCountNoAsync(CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> SearchAsync(
        IvStockCountListQuery? query,
        CancellationToken cancellationToken = default);

    /// <summary>Loads one sheet by its human-readable <c>CountNo</c>.</summary>
    Task<IvStockCountOperationResult> GetAsync(
        string countNo,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a DRAFT sheet (header only). Lines come from <see cref="GenerateAsync"/>.</summary>
    Task<IvStockCountOperationResult> SaveAsync(
        IvStockCountSaveRequest? request,
        CancellationToken cancellationToken = default);

    /// <summary>Edits a DRAFT sheet's header/scope. Requires the sheet's RowVersion.</summary>
    Task<IvStockCountOperationResult> UpdateAsync(
        int id,
        IvStockCountSaveRequest? request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the sheet's lines from its stored scope. Refused for any state other than DRAFT, and
    /// refused while ANY line carries a physical quantity unless <paramref name="discardCounts"/> is
    /// true (the UI only sends that after an explicit "Regenerate and discard N counted lines?").
    /// </summary>
    Task<IvStockCountOperationResult> GenerateAsync(
        int id,
        bool discardCounts,
        CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> DeleteAsync(
        IReadOnlyList<int>? ids,
        CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> CancelAsync(
        IReadOnlyList<int>? ids,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists physical quantities only (no insert, no delete). Writable while the sheet is DRAFT,
    /// COUNTED or ROLLED_BACK — a COUNTED sheet stays editable until it is POSTED. The first quantity
    /// recorded moves DRAFT to COUNTED.
    /// </summary>
    Task<IvStockCountOperationResult> SaveCountsAsync(
        int id,
        IReadOnlyList<IvStockCountLineCountRequest>? lines,
        string? rowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// "Enter by item": writes one physical quantity onto ONE named slice. Refused when the item has
    /// more than one countable line in this sheet and no slice was named.
    /// </summary>
    Task<IvStockCountOperationResult> SetItemCountAsync(
        int id,
        string iCode,
        decimal physicalQty,
        IReadOnlyList<int>? balLocIds,
        CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> PreviewPostAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> PostAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<IvStockCountOperationResult> RollbackAsync(
        int id,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The ONE repair path: when the header says POSTED but its linked batch was rolled back from the
    /// Stock Adjustment list, reset the header to COUNTED. Status only — never the frozen evidence.
    /// </summary>
    Task<IvStockCountOperationResult> RecoverAsync(
        int id,
        CancellationToken cancellationToken = default);
}
