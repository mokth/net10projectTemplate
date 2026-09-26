using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>
/// Shared plumbing for the read-only Sales Inquiry screens (plan-salesReportsAndInquiries Phase 1).
///
/// Every screen defaults to the current calendar month, keeps its filter state in a
/// <see cref="SaInquiryQuery"/> and exports through a server endpoint that calls the <b>same</b>
/// <see cref="ISaSalesInquiryService"/> method as the grid — so the exported file and the screen can
/// never disagree. ACCESS is checked inside the service, per menu.
/// </summary>
public abstract class SaInquiryPageBase : PageBase
{
    [Inject] protected ISaSalesInquiryService Inquiry { get; set; } = default!;
    [Inject] protected ISaCustLookupService CustLookups { get; set; } = default!;
    [Inject] protected ICurrentDateService Dates { get; set; } = default!;

    protected DateTime? DateFrom { get; set; }
    protected DateTime? DateTo { get; set; }
    protected string? CustCode { get; set; }
    protected string? SalesmanCode { get; set; }
    protected string? Status { get; set; }
    protected string? Type { get; set; }
    protected string? SearchText { get; set; }
    protected string? ItemCode { get; set; }

    // ---- Sales Monitor filters (plan-salesDecisionSupport.prompt.md, Phase A) ---------------------
    // The Phase-1 inquiry pages never set these, so the shared query builder keeps producing exactly
    // the predicate it produced before. The monitor pages use them as ordinary applied filters.

    /// <summary>A1: keep only lines whose expected delivery date has passed.</summary>
    protected bool OverdueOnly { get; set; }

    /// <summary>A1/A3: restrict to one monitoring bucket label.</summary>
    protected string? Bucket { get; set; }

    /// <summary>A2: restrict to one <see cref="SaDoInvoiceStates"/> value.</summary>
    protected string? InvoiceState { get; set; }

    /// <summary>A2: keep only lines that still owe billing.</summary>
    protected bool PendingOnly { get; set; }

    /// <summary>A3: keep only quotations the lazy-expiry sweep is about to expire.</summary>
    protected bool ExpiringSoonOnly { get; set; }

    /// <summary>A3: keep only quotations already past their validity date.</summary>
    protected bool ExpiredOnly { get; set; }

    /// <summary>SO Transaction Inquiry: restrict to <c>IsCurrent</c> revisions when true.</summary>
    protected bool CurrentOnly { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];

    /// <summary>
    /// The branch every query is scoped to — the <b>caller's own</b> branch, resolved by the service's
    /// tenant gate. There is no branch master to pick from and <see cref="SaInquiryQuery.BranchCode"/>
    /// is never read by the service, so the scope is <b>shown</b> (a hero chip), never offered as a
    /// filter.
    /// </summary>
    protected string? ScopeBranchCode => Clean(CurrentUser.BranchCode);

    // ---- Draft (popup) filter state -------------------------------------------------------------
    // The popup edits THESE; only Apply copies them onto the applied properties above. That split is
    // what stops a closed-without-applying popup from changing the grid behind the operator's back.

    protected DateTime? DraftDateFrom { get; set; }
    protected DateTime? DraftDateTo { get; set; }
    protected string? DraftCustCode { get; set; }
    protected string? DraftSalesmanCode { get; set; }
    protected string? DraftStatus { get; set; }
    protected string? DraftType { get; set; }
    protected string? DraftSearchText { get; set; }
    protected string? DraftItemCode { get; set; }

    protected bool DraftOverdueOnly { get; set; }
    protected string? DraftBucket { get; set; }
    protected string? DraftInvoiceState { get; set; }
    protected bool DraftPendingOnly { get; set; }
    protected bool DraftExpiringSoonOnly { get; set; }
    protected bool DraftExpiredOnly { get; set; }
    protected bool DraftCurrentOnly { get; set; }

    /// <summary>The filter popup's visibility (house pattern: filters live in a popup, not inline).</summary>
    protected bool FilterPopupVisible { get; set; }

    /// <summary>One entry of a Status filter combo — the value sent to the service and its display text.</summary>
    protected sealed record StatusFilterOption(string Key, string Name);

    /// <summary>
    /// The screen's own menu code. Optional — the Phase-1 pages pass their code straight to the service;
    /// the monitor pages derive several calls from it, so they expose it once here.
    /// </summary>
    protected virtual string MenuCode => string.Empty;

    protected DxGrid? Grid;
    protected bool IsBootstrapping = true;

    /// <summary>
    /// Warning raised by a drill-down attempt that could not be resolved (e.g. a document row whose
    /// type has no view route). Shown as a non-blocking toast, never as an error.
    /// </summary>
    protected string? NavMessage { get; set; }

    protected override async Task OnPageInitializedAsync()
    {
        var today = Dates.Now.Date;
        DateFrom ??= new DateTime(today.Year, today.Month, 1);
        DateTo ??= today;
        await OnInquiryInitializedAsync();
    }

    protected virtual Task OnInquiryInitializedAsync() => Task.CompletedTask;

    protected async Task LoadCommonLookupsAsync()
    {
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
        CustomerOptions = EnsureCurrentCustomerInOptions(
            await CustLookups.SearchCustomersAsync(string.Empty, 200));
    }

    protected async Task OnCustomerSearchChangedAsync(string? value)
    {
        CustomerOptions = EnsureCurrentCustomerInOptions(
            await CustLookups.SearchCustomersAsync(value ?? string.Empty, 200));
    }

    /// <summary>
    /// House pattern for a value that may not be in the option list: a <c>DxComboBox</c> bound to a
    /// value its data source cannot resolve raises <c>ValueChanged(null)</c>, which would silently
    /// clear the applied filter. The customer picker sits outside the popup and applies on selection,
    /// so the re-searched list must keep carrying the current value.
    /// </summary>
    private IReadOnlyList<IvCodeLookupRow> EnsureCurrentCustomerInOptions(IReadOnlyList<IvCodeLookupRow> options)
    {
        if (string.IsNullOrWhiteSpace(CustCode)
            || options.Any(o => string.Equals(o.Code, CustCode, StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        return [.. options, new IvCodeLookupRow { Code = CustCode!, Desc = "(current filter)" }];
    }

    /// <summary>
    /// The customer picker lives in the grid toolbar (the Inventory inquiry pattern: the primary scope
    /// control sits beside the FILTER button and applies on selection).
    /// </summary>
    protected async Task OnCustomerChangedAsync(string? value)
    {
        CustCode = value;
        await ReloadAsync();
    }

    /// <summary>
    /// Builds the applied filter object — never the grid's current page. There is deliberately no
    /// branch predicate: every query is scoped to the caller's own branch by the service's tenant gate.
    /// </summary>
    protected virtual SaInquiryQuery BuildQuery() => new()
    {
        DateFrom = DateFrom?.Date,
        DateTo = DateTo?.Date,
        CustCode = Clean(CustCode),
        SalesmanCode = Clean(SalesmanCode),
        Status = Clean(Status),
        Type = Clean(Type),
        SearchText = Clean(SearchText),
        ItemCode = Clean(ItemCode),
        AsOfDate = Dates.Now.Date,
        OverdueOnly = OverdueOnly,
        Bucket = Clean(Bucket),
        InvoiceState = Clean(InvoiceState),
        PendingOnly = PendingOnly,
        ExpiringSoonOnly = ExpiringSoonOnly,
        ExpiredOnly = ExpiredOnly,
        CurrentOnly = CurrentOnly
    };

    /// <summary>The filters the grid is actually showing, so the export can never disagree with it.</summary>
    protected SaInquiryQuery AppliedQuery { get; private set; } = new();

    /// <summary>Commits the current filter fields as the applied query. Never the grid's current page.</summary>
    protected void CommitFilters()
    {
        AppliedQuery = BuildQuery();
    }

    // ---- Filter popup (the Inventory inquiry standard) -------------------------------------------

    /// <summary>Opens the filter popup with the applied filters copied into the drafts.</summary>
    protected virtual void OpenFilterPopup()
    {
        DraftDateFrom = DateFrom;
        DraftDateTo = DateTo;
        DraftCustCode = CustCode;
        DraftSalesmanCode = SalesmanCode;
        DraftStatus = Status;
        DraftType = Type;
        DraftSearchText = SearchText;
        DraftItemCode = ItemCode;
        DraftOverdueOnly = OverdueOnly;
        DraftBucket = Bucket;
        DraftInvoiceState = InvoiceState;
        DraftPendingOnly = PendingOnly;
        DraftExpiringSoonOnly = ExpiringSoonOnly;
        DraftExpiredOnly = ExpiredOnly;
        DraftCurrentOnly = CurrentOnly;
        FilterPopupVisible = true;
    }

    /// <summary>
    /// Popup Apply: commits the drafts as the applied filters and reloads. No predicate changes here —
    /// the same <see cref="BuildQuery"/> output feeds the grid and the export (R9 parity).
    /// </summary>
    protected async Task ApplyFiltersAsync()
    {
        DateFrom = DraftDateFrom;
        DateTo = DraftDateTo;
        CustCode = DraftCustCode;
        SalesmanCode = DraftSalesmanCode;
        Status = DraftStatus;
        Type = DraftType;
        SearchText = DraftSearchText;
        ItemCode = DraftItemCode;
        OverdueOnly = DraftOverdueOnly;
        Bucket = DraftBucket;
        InvoiceState = DraftInvoiceState;
        PendingOnly = DraftPendingOnly;
        ExpiringSoonOnly = DraftExpiringSoonOnly;
        ExpiredOnly = DraftExpiredOnly;
        CurrentOnly = DraftCurrentOnly;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    /// <summary>
    /// Popup Clear: resets the drafts <b>and</b> the applied filters to the page defaults and reloads
    /// immediately — the same semantics as <c>IvBalanceLot</c>.
    /// </summary>
    protected async Task ClearFiltersAsync()
    {
        var today = Dates.Now.Date;
        DateFrom = new DateTime(today.Year, today.Month, 1);
        DateTo = today;
        CustCode = null;
        SalesmanCode = null;
        Status = null;
        Type = null;
        SearchText = null;
        ItemCode = null;
        ResetMonitorFilters();

        DraftDateFrom = DateFrom;
        DraftDateTo = DateTo;
        DraftCustCode = null;
        DraftSalesmanCode = null;
        DraftStatus = null;
        DraftType = null;
        DraftSearchText = null;
        DraftItemCode = null;
        DraftOverdueOnly = OverdueOnly;
        DraftBucket = Bucket;
        DraftInvoiceState = InvoiceState;
        DraftPendingOnly = PendingOnly;
        DraftExpiringSoonOnly = ExpiringSoonOnly;
        DraftExpiredOnly = ExpiredOnly;
        DraftCurrentOnly = CurrentOnly;

        FilterPopupVisible = false;
        await ReloadAsync();
    }

    /// <summary>
    /// Resets the monitor-only filters to this screen's default. A1/A3 default to "no restriction"; A2
    /// overrides this because "pending only" is the point of that screen.
    /// </summary>
    protected virtual void ResetMonitorFilters()
    {
        OverdueOnly = false;
        Bucket = null;
        InvoiceState = null;
        PendingOnly = false;
        ExpiringSoonOnly = false;
        ExpiredOnly = false;
        CurrentOnly = false;
    }

    /// <summary>
    /// Reloads from the <b>applied</b> filters only. The grid toolbar's REFRESH and the toolbar customer
    /// picker both land here, so an abandoned draft edit can never leak into the grid.
    /// </summary>
    protected virtual async Task ReloadAsync()
    {
        ErrorMessage = null;
        CommitFilters();
        ApplyFiltersToGrids();
        await ReloadGridsAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Per page: pushes <see cref="AppliedQuery"/> into every grid data source.</summary>
    protected virtual void ApplyFiltersToGrids()
    {
    }

    /// <summary>Per page: reloads every grid. The default covers the single-grid pages.</summary>
    protected virtual Task ReloadGridsAsync()
    {
        Grid?.Reload();
        return Task.CompletedTask;
    }

    protected void OnGridInstance(DxGrid grid) => Grid = grid;

    protected void DismissError() => ErrorMessage = null;

    protected void DismissNavMessage() => NavMessage = null;

    // ---- Inquiry → document drill-down -----------------------------------------------------------

    /// <summary>
    /// Row-action button set every drill-down capable inquiry grid exposes. The grid renders one button
    /// per row; the click lands in <c>OnActionClick</c> of the page.
    /// </summary>
    protected virtual List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "VIEW", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "View document" }
    ];

    /// <summary>
    /// Opens a document view <b>from this inquiry</b>. The current inquiry path is attached as a
    /// <c>returnUrl</c>, so the document's Close button returns here (not to the transaction list) and
    /// the sidebar does not expand the Transactions group while the document is open.
    /// </summary>
    protected void OpenDocument(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        NavMessage = null;
        var returnPath = "/" + Navigation.RelativePath.Split('?', 2)[0].TrimStart('/');
        Navigation.NavigateTo(DocumentReturnNavigation.WithReturnUrl(url, returnPath));
    }

    /// <summary>Resolves a row's document-type / document-number pair and opens it; warns when unknown.</summary>
    protected void TryOpenByDocType(string? docType, string? docNo, short? custRel = null)
    {
        if (!SaInquiryNavigation.TryResolveByDocType(docType, docNo, custRel, out var url))
        {
            NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        OpenDocument(url);
    }

    /// <summary>Resolves a relationship row's SOURCE or TARGET endpoint and opens it.</summary>
    protected void TryOpenByRelationship(
        string? relation,
        string? sourceDocNo,
        string? targetDocNo,
        bool openSource)
    {
        if (!SaInquiryNavigation.TryResolveRelationship(relation, sourceDocNo, targetDocNo, openSource, out var url))
        {
            NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        OpenDocument(url);
    }

    protected static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Builds the export URL from the SAME applied filter object the grid uses, so no filter can be
    /// dropped or re-interpreted on the way to the file.
    /// </summary>
    protected string BuildExportUrl(string path, IEnumerable<KeyValuePair<string, string?>> extras)
    {
        var parameters = new Dictionary<string, string?>();
        AddIfSet(parameters, "dateFrom", AppliedQuery.DateFrom?.ToString("yyyy-MM-dd"));
        AddIfSet(parameters, "dateTo", AppliedQuery.DateTo?.ToString("yyyy-MM-dd"));
        AddIfSet(parameters, "custCode", AppliedQuery.CustCode);
        AddIfSet(parameters, "salesmanCode", AppliedQuery.SalesmanCode);
        AddIfSet(parameters, "branchCode", AppliedQuery.BranchCode);
        AddIfSet(parameters, "status", AppliedQuery.Status);
        AddIfSet(parameters, "type", AppliedQuery.Type);
        AddIfSet(parameters, "searchText", AppliedQuery.SearchText);
        AddIfSet(parameters, "itemCode", AppliedQuery.ItemCode);
        AddIfSet(parameters, "asOf", AppliedQuery.AsOfDate?.ToString("yyyy-MM-dd"));
        AddIfSet(parameters, "bucket", AppliedQuery.Bucket);
        AddIfSet(parameters, "invoiceState", AppliedQuery.InvoiceState);

        if (AppliedQuery.OverdueOnly)
        {
            parameters["overdueOnly"] = "true";
        }

        if (AppliedQuery.PendingOnly)
        {
            parameters["pendingOnly"] = "true";
        }

        if (AppliedQuery.ExpiringSoonOnly)
        {
            parameters["expiringSoonOnly"] = "true";
        }

        if (AppliedQuery.ExpiredOnly)
        {
            parameters["expiredOnly"] = "true";
        }

        if (AppliedQuery.CurrentOnly)
        {
            parameters["currentOnly"] = "true";
        }

        foreach (var pair in extras)
        {
            AddIfSet(parameters, pair.Key, pair.Value);
        }

        return QueryHelpers.AddQueryString(path, parameters);
    }

    private static void AddIfSet(Dictionary<string, string?> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value;
        }
    }

    protected static string MoneyText(decimal value) => value.ToString("N2");

    protected static string QtyText(decimal value) => value.ToString("N4");

    protected static string DateText(DateTime? value) => value is DateTime d ? d.ToString("dd/MM/yyyy") : string.Empty;

    protected static string YesNo(bool value) => value ? "Yes" : "No";
}

/// <summary>
/// Server-side paging source for an inquiry grid. The filters are rebuilt from the applied query on
/// every reload, so the grid can never page with a stale predicate.
/// </summary>
public sealed class SaInquiryGridDataSource<T> : GridCustomDataSource
{
    private readonly Func<SaInquiryQuery, CancellationToken, Task<IvMasterOperationResult<SaInquiryPage<T>>>> _loader;
    private readonly Action<string?> _onError;
    private SaInquiryQuery _filters = new();

    public SaInquiryGridDataSource(
        Func<SaInquiryQuery, CancellationToken, Task<IvMasterOperationResult<SaInquiryPage<T>>>> loader,
        Action<string?> onError)
    {
        _loader = loader;
        _onError = onError;
    }

    public void UpdateFilters(SaInquiryQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;

        var result = await _loader(query, cancellationToken);
        if (!result.Succeeded)
        {
            _onError(result.Message);
            return 0;
        }

        return result.Data?.TotalCount ?? 0;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = options.Count <= 0 ? 50 : options.Count;

        var result = await _loader(query, cancellationToken);
        if (!result.Succeeded)
        {
            _onError(result.Message);
            return new List<T>();
        }

        return (result.Data?.Rows ?? []).ToList();
    }

    private static SaInquiryQuery Clone(SaInquiryQuery source) => new()
    {
        DateFrom = source.DateFrom,
        DateTo = source.DateTo,
        CustCode = source.CustCode,
        SalesmanCode = source.SalesmanCode,
        BranchCode = source.BranchCode,
        Status = source.Status,
        Type = source.Type,
        SearchText = source.SearchText,
        ItemCode = source.ItemCode,
        Skip = source.Skip,
        Take = source.Take,
        AsOfDate = source.AsOfDate,
        OverdueOnly = source.OverdueOnly,
        Bucket = source.Bucket,
        InvoiceState = source.InvoiceState,
        PendingOnly = source.PendingOnly,
        ExpiringSoonOnly = source.ExpiringSoonOnly,
        ExpiredOnly = source.ExpiredOnly,
        CurrentOnly = source.CurrentOnly
    };
}
