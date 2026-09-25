namespace ErpWeb.Core.Inventory;

/// <summary>
/// Result envelope for every <see cref="IIvPeriodCloseService"/> member, in the shape of
/// <c>IvStockCountOperationResult</c>.
/// </summary>
public sealed class IvPeriodCloseResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }

    public int Id { get; init; }

    public IvPeriodCloseListPage? ListPage { get; init; }
    public IvPeriodCloseDocument? Document { get; init; }
    public IvPeriodCloseInquiryPage? InquiryPage { get; init; }

    public static IvPeriodCloseResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };

    public static IvPeriodCloseResult OkList(IvPeriodCloseListPage page) =>
        new() { Succeeded = true, ListPage = page };

    public static IvPeriodCloseResult OkClosed(int id) =>
        new() { Succeeded = true, Id = id };

    public static IvPeriodCloseResult OkDocument(IvPeriodCloseDocument document) =>
        new() { Succeeded = true, Document = document, Id = document.Id };

    public static IvPeriodCloseResult OkInquiry(IvPeriodCloseInquiryPage page) =>
        new() { Succeeded = true, InquiryPage = page };
}

/// <summary>Close / reopen request. <c>Id</c> + <c>RowVersion</c> + <c>ReopenReason</c> are for reopen.</summary>
public sealed class IvPeriodCloseRequest
{
    public int Id { get; set; }
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public string? Remark { get; set; }
    public string? RowVersion { get; set; }
    public string? ReopenReason { get; set; }
}

/// <summary>One closed-period header row for the action-page grid.</summary>
public sealed class IvPeriodCloseHeaderRow
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ClosedBy { get; set; }
    public DateTime? ClosedOn { get; set; }
    public int ReopenCount { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedOn { get; set; }
    public string? ReopenReason { get; set; }
    public int LineCount { get; set; }
    public int SkippedZeroSlices { get; set; }
    public int OpeningAdjustSlices { get; set; }
    public int CarryForwardMismatchSlices { get; set; }
    public bool CurrentBalanceCheckApplies { get; set; }
    public int CurrentBalanceMismatchSlices { get; set; }
    public decimal TotalOpeningValue { get; set; }
    public decimal TotalInValue { get; set; }
    public decimal TotalOutValue { get; set; }
    public decimal TotalClosingValue { get; set; }
    public int LastReopenLineCount { get; set; }
    public decimal LastReopenClosingValue { get; set; }
    public int UnpostedBatchCount { get; set; }
    public int ReconcileFindingCount { get; set; }
    public string? Remark { get; set; }
    public string? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>The next closable period suggested to the operator (pre-filled from the last close).</summary>
public sealed class IvPeriodCloseNextPeriod
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public bool HasPriorClose { get; set; }
}

/// <summary>The action-page list: the closed-period grid plus the next closable period.</summary>
public sealed class IvPeriodCloseListPage
{
    public IReadOnlyList<IvPeriodCloseHeaderRow> Headers { get; init; } = [];
    public IvPeriodCloseNextPeriod Next { get; init; } = new();
}

/// <summary>A blocking or advisory precondition (a NEW batch or a reconciliation finding).</summary>
public sealed class IvPeriodClosePrecondition
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>One stored closing-balance row (the 7-part slice with its quantities).</summary>
public sealed class IvPeriodCloseBalRow
{
    public int Id { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string WhCode { get; set; } = string.Empty;
    public string LocCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public decimal OpeningQty { get; set; }
    public decimal OpeningAdjustQty { get; set; }
    public decimal InQty { get; set; }
    public decimal OutQty { get; set; }
    public decimal AdjustNetQty { get; set; }
    public decimal ClosingQty { get; set; }
    public string? StdUom { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ClosingValue { get; set; }
    public int LegCount { get; set; }
    public bool CarryForwardOk { get; set; }
    public decimal? CurrentBalanceDelta { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>The close-page document: header, preconditions and the stored lines.</summary>
public sealed class IvPeriodCloseDocument
{
    public int Id { get; set; }
    public IvPeriodCloseHeaderRow Header { get; set; } = new();
    public IReadOnlyList<IvPeriodClosePrecondition> BlockingNewBatches { get; init; } = [];
    public IReadOnlyList<IvPeriodClosePrecondition> BlockingFindings { get; init; } = [];
    public IReadOnlyList<IvPeriodClosePrecondition> AdvisoryFindings { get; init; } = [];
    public IReadOnlyList<IvPeriodCloseBalRow> Lines { get; init; } = [];
}

/// <summary>The read-only inquiry over a closed period's stored balances.</summary>
public sealed class IvPeriodCloseInquiryPage
{
    public IvPeriodCloseHeaderRow Header { get; set; } = new();
    public IReadOnlyList<IvPeriodCloseBalRow> Lines { get; init; } = [];
}

public interface IIvPeriodCloseService
{
    /// <summary>The action page: closed-period grid + the next closable period.</summary>
    Task<IvPeriodCloseResult> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One closed period with its preconditions and lines.</summary>
    Task<IvPeriodCloseResult> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Close (or, after a reopen, re-close) a period, writing the stored snapshot.</summary>
    Task<IvPeriodCloseResult> CloseAsync(IvPeriodCloseRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Reopen a closed period, deleting the derived snapshot lines.</summary>
    Task<IvPeriodCloseResult> ReopenAsync(IvPeriodCloseRequest? request, CancellationToken cancellationToken = default);

    /// <summary>The read-only inquiry page (menu <c>INV_PERIOD_CLOSE_INQ</c>).</summary>
    Task<IvPeriodCloseResult> InquiryAsync(int periodCloseId, CancellationToken cancellationToken = default);

    /// <summary>Closed periods for the inquiry page's picker (its OWN menu, not the action menu's).</summary>
    Task<IvPeriodCloseResult> ListInquiryPeriodsAsync(CancellationToken cancellationToken = default);
}
