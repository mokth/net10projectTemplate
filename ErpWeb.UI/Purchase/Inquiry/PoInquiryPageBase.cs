using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>
/// Shared plumbing for Purchase Inquiry Phase 1 — mirrors <see cref="Sales.Inquiry.SaInquiryPageBase"/>
/// with supplier scope instead of customer. Inventory filter UX + Sales grid/export conventions.
/// </summary>
public abstract class PoInquiryPageBase : PageBase
{
    [Inject] protected IPoPurchaseInquiryService Inquiry { get; set; } = default!;
    [Inject] protected IPoSupplierLookupService SupplierLookups { get; set; } = default!;
    [Inject] protected ICurrentDateService Dates { get; set; } = default!;

    protected DateTime? DateFrom { get; set; }
    protected DateTime? DateTo { get; set; }
    protected string? SuppCode { get; set; }
    protected string? BuyerCode { get; set; }
    protected string? Status { get; set; }
    protected string? Type { get; set; }
    protected string? SearchText { get; set; }

    /// <summary>Active workbench preset key; null means screen default exception set.</summary>
    protected string? WorkbenchPreset { get; set; }

    protected string? NavMessage { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> SupplierOptions { get; set; } = [];

    protected string? ScopeBranchCode => Clean(CurrentUser.BranchCode);

    protected DateTime? DraftDateFrom { get; set; }
    protected DateTime? DraftDateTo { get; set; }
    protected string? DraftSuppCode { get; set; }
    protected string? DraftBuyerCode { get; set; }
    protected string? DraftStatus { get; set; }
    protected string? DraftType { get; set; }
    protected string? DraftSearchText { get; set; }

    protected bool FilterPopupVisible { get; set; }

    protected sealed record StatusFilterOption(string Key, string Name);

    protected DxGrid? Grid;
    protected bool IsBootstrapping = true;

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
        SupplierOptions = EnsureCurrentSupplierInOptions(
            await SupplierLookups.SearchSuppliersAsync(string.Empty, 200));
    }

    protected async Task OnSupplierSearchChangedAsync(string? value)
    {
        SupplierOptions = EnsureCurrentSupplierInOptions(
            await SupplierLookups.SearchSuppliersAsync(value ?? string.Empty, 200));
    }

    private IReadOnlyList<IvCodeLookupRow> EnsureCurrentSupplierInOptions(IReadOnlyList<IvCodeLookupRow> options)
    {
        if (string.IsNullOrWhiteSpace(SuppCode)
            || options.Any(o => string.Equals(o.Code, SuppCode, StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        return [.. options, new IvCodeLookupRow { Code = SuppCode!, Desc = "(current filter)" }];
    }

    protected async Task OnSupplierChangedAsync(string? value)
    {
        SuppCode = value;
        await ReloadAsync();
    }

    protected virtual PoInquiryQuery BuildQuery() => new()
    {
        DateFrom = DateFrom?.Date,
        DateTo = DateTo?.Date,
        SuppCode = Clean(SuppCode),
        BuyerCode = Clean(BuyerCode),
        Status = Clean(Status),
        Type = Clean(Type),
        SearchText = Clean(SearchText),
        WorkbenchPreset = Clean(WorkbenchPreset),
        AsOfDate = Dates.Now.Date
    };

    protected PoInquiryQuery AppliedQuery { get; private set; } = new();

    protected void CommitFilters() => AppliedQuery = BuildQuery();

    protected virtual void OpenFilterPopup()
    {
        DraftDateFrom = DateFrom;
        DraftDateTo = DateTo;
        DraftSuppCode = SuppCode;
        DraftBuyerCode = BuyerCode;
        DraftStatus = Status;
        DraftType = Type;
        DraftSearchText = SearchText;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        DateFrom = DraftDateFrom;
        DateTo = DraftDateTo;
        SuppCode = DraftSuppCode;
        BuyerCode = DraftBuyerCode;
        Status = DraftStatus;
        Type = DraftType;
        SearchText = DraftSearchText;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        var today = Dates.Now.Date;
        DateFrom = new DateTime(today.Year, today.Month, 1);
        DateTo = today;
        SuppCode = null;
        BuyerCode = null;
        Status = null;
        Type = null;
        SearchText = null;

        DraftDateFrom = DateFrom;
        DraftDateTo = DateTo;
        DraftSuppCode = null;
        DraftBuyerCode = null;
        DraftStatus = null;
        DraftType = null;
        DraftSearchText = null;

        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected virtual async Task ReloadAsync()
    {
        ErrorMessage = null;
        CommitFilters();
        ApplyFiltersToGrids();
        await ReloadGridsAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected virtual void ApplyFiltersToGrids()
    {
    }

    protected virtual Task ReloadGridsAsync()
    {
        Grid?.Reload();
        return Task.CompletedTask;
    }

    protected void OnGridInstance(DxGrid grid) => Grid = grid;

    protected void DismissError() => ErrorMessage = null;

    protected void DismissNavMessage() => NavMessage = null;

    /// <summary>
    /// Toggle preset: click active again clears to screen default; otherwise activates.
    /// </summary>
    protected async Task TogglePresetAsync(string preset, string? screenDefault = null)
    {
        if (string.Equals(WorkbenchPreset, preset, StringComparison.OrdinalIgnoreCase))
        {
            WorkbenchPreset = screenDefault;
        }
        else
        {
            WorkbenchPreset = preset;
        }

        await ReloadAsync();
    }

    protected string ChipClass(string preset) =>
        string.Equals(WorkbenchPreset, preset, StringComparison.OrdinalIgnoreCase)
            ? "iv-chip iv-chip--active"
            : "iv-chip";

    protected void OpenDocument(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        NavMessage = null;
        // Return to this inquiry page on Close (not the transaction list).
        var returnPath = "/" + Navigation.RelativePath.Split('?', 2)[0].TrimStart('/');
        Navigation.NavigateTo(DocumentReturnNavigation.WithReturnUrl(url, returnPath));
    }

    protected void TryOpenByDocType(string? docType, string? docNo, short? poRelNo = null)
    {
        if (!PoInquiryNavigation.TryResolveByDocType(docType, docNo, poRelNo, out var url))
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            return;
        }

        OpenDocument(url);
    }

    protected static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected string BuildExportUrl(string path, IEnumerable<KeyValuePair<string, string?>> extras)
    {
        var parameters = new Dictionary<string, string?>();
        AddIfSet(parameters, "dateFrom", AppliedQuery.DateFrom?.ToString("yyyy-MM-dd"));
        AddIfSet(parameters, "dateTo", AppliedQuery.DateTo?.ToString("yyyy-MM-dd"));
        AddIfSet(parameters, "suppCode", AppliedQuery.SuppCode);
        AddIfSet(parameters, "buyerCode", AppliedQuery.BuyerCode);
        AddIfSet(parameters, "branchCode", AppliedQuery.BranchCode);
        AddIfSet(parameters, "status", AppliedQuery.Status);
        AddIfSet(parameters, "type", AppliedQuery.Type);
        AddIfSet(parameters, "searchText", AppliedQuery.SearchText);
        AddIfSet(parameters, "workbenchPreset", AppliedQuery.WorkbenchPreset);
        if (AppliedQuery.AsOfDate is DateTime asOf)
        {
            AddIfSet(parameters, "asOfDate", asOf.ToString("yyyy-MM-dd"));
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
}

public sealed class PoInquiryGridDataSource<T> : GridCustomDataSource
{
    private readonly Func<PoInquiryQuery, CancellationToken, Task<IvMasterOperationResult<PoInquiryPage<T>>>> _loader;
    private readonly Action<string?> _onError;
    private PoInquiryQuery _filters = new();

    public PoInquiryGridDataSource(
        Func<PoInquiryQuery, CancellationToken, Task<IvMasterOperationResult<PoInquiryPage<T>>>> loader,
        Action<string?> onError)
    {
        _loader = loader;
        _onError = onError;
    }

    public void UpdateFilters(PoInquiryQuery query) => _filters = Clone(query);

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

    private static PoInquiryQuery Clone(PoInquiryQuery source) => new()
    {
        DateFrom = source.DateFrom,
        DateTo = source.DateTo,
        SuppCode = source.SuppCode,
        BuyerCode = source.BuyerCode,
        BranchCode = source.BranchCode,
        Status = source.Status,
        Type = source.Type,
        SearchText = source.SearchText,
        WorkbenchPreset = source.WorkbenchPreset,
        AsOfDate = source.AsOfDate,
        Skip = source.Skip,
        Take = source.Take
    };
}
