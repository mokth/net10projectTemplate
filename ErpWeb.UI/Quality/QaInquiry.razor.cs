using System.Timers;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Quality;

public partial class QaInquiry : PageBase, IDisposable
{
    [Inject] private IQaCaseService Cases { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private Timer? _searchDebounce;
    private int _searchVersion;
    private bool _disposed;
    private const int PageSize = 50;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected bool HelpVisible;
    protected bool CanExport;
    protected bool IsExporting;
    protected int Skip;
    protected string SearchText = string.Empty;
    protected string? AppliedStatus;
    protected string? AppliedSource;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string? AppliedItemCode;
    protected string? AppliedLotNo;
    protected string? AppliedPoNo;
    protected string? AppliedWorkOrderNo;
    protected string? AppliedSupplier;
    protected string? AppliedInspector;
    protected string? AppliedReviewer;
    protected string? AppliedActionOwner;
    protected bool AppliedOverdueOnly;
    protected string DraftStatusKey = "all";
    protected string DraftSourceKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected string? DraftItemCode;
    protected string? DraftLotNo;
    protected string? DraftPoNo;
    protected string? DraftWorkOrderNo;
    protected string? DraftSupplier;
    protected string? DraftInspector;
    protected string? DraftReviewer;
    protected string? DraftActionOwner;
    protected bool DraftOverdueOnly;
    protected List<QaInquiryRow> Rows { get; set; } = [];
    protected QaInquiryPage Page { get; set; } = new();

    protected string CaseCountLabel => Page.CaseCount == 1 ? "1 case" : $"{Page.CaseCount:N0} cases";
    protected string PendingReviewLabel => $"{Page.PendingReviewCount:N0} pending review";
    protected string FirstPassYieldDisplay => $"{Page.FirstPassYield:P0}";
    protected string FirstPassYieldNote => string.IsNullOrWhiteSpace(Page.FirstPassYieldNote)
        ? QaCaseMessages.ApprovedWithoutRejectNote
        : Page.FirstPassYieldNote;
    protected bool CanPrev => Skip > 0;
    protected bool CanNext => Skip + Rows.Count < Page.TotalCount;
    protected string PageLabel =>
        Page.TotalCount == 0
            ? "No rows"
            : $"{Skip + 1}–{Skip + Rows.Count} of {Page.TotalCount:N0}";

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaLabels.StatusOptions.Select(x => new StatusFilterOption(x.Key, x.Name))
    ];

    protected IReadOnlyList<StatusFilterOption> SourceFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaLabels.SourceOptions.Select(x => new StatusFilterOption(x.Key, x.Name))
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanExport = await AccessRights.CanAsync(MenuCodes.QualityInquiry, PermissionCodes.Export);
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected async Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty;
        Skip = 0;
        _searchDebounce?.Dispose();
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce = new Timer(350) { AutoReset = false };
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                if (_disposed || version != Volatile.Read(ref _searchVersion))
                {
                    return;
                }

                await ReloadAsync();
            });
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected Task ApplySearchAsync()
    {
        Skip = 0;
        return ReloadAsync();
    }

    protected void OpenFilterPopup()
    {
        DraftStatusKey = string.IsNullOrWhiteSpace(AppliedStatus) ? "all" : AppliedStatus;
        DraftSourceKey = string.IsNullOrWhiteSpace(AppliedSource) ? "all" : AppliedSource;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        DraftItemCode = AppliedItemCode;
        DraftLotNo = AppliedLotNo;
        DraftPoNo = AppliedPoNo;
        DraftWorkOrderNo = AppliedWorkOrderNo;
        DraftSupplier = AppliedSupplier;
        DraftInspector = AppliedInspector;
        DraftReviewer = AppliedReviewer;
        DraftActionOwner = AppliedActionOwner;
        DraftOverdueOnly = AppliedOverdueOnly;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedSource = string.Equals(DraftSourceKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftSourceKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        AppliedItemCode = Normalize(DraftItemCode);
        AppliedLotNo = Normalize(DraftLotNo);
        AppliedPoNo = Normalize(DraftPoNo);
        AppliedWorkOrderNo = Normalize(DraftWorkOrderNo);
        AppliedSupplier = Normalize(DraftSupplier);
        AppliedInspector = Normalize(DraftInspector);
        AppliedReviewer = Normalize(DraftReviewer);
        AppliedActionOwner = Normalize(DraftActionOwner);
        AppliedOverdueOnly = DraftOverdueOnly;
        Skip = 0;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusKey = "all";
        DraftSourceKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        DraftItemCode = null;
        DraftLotNo = null;
        DraftPoNo = null;
        DraftWorkOrderNo = null;
        DraftSupplier = null;
        DraftInspector = null;
        DraftReviewer = null;
        DraftActionOwner = null;
        DraftOverdueOnly = false;
        AppliedStatus = null;
        AppliedSource = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        AppliedItemCode = null;
        AppliedLotNo = null;
        AppliedPoNo = null;
        AppliedWorkOrderNo = null;
        AppliedSupplier = null;
        AppliedInspector = null;
        AppliedReviewer = null;
        AppliedActionOwner = null;
        AppliedOverdueOnly = false;
        Skip = 0;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected Task PreviousPageAsync()
    {
        Skip = Math.Max(0, Skip - PageSize);
        return ReloadAsync();
    }

    protected Task NextPageAsync()
    {
        Skip += PageSize;
        return ReloadAsync();
    }

    protected void OnExport()
    {
        if (!CanExport)
        {
            ErrorMessage = "Not authorized.";
            return;
        }

        IsExporting = true;
        Navigation.NavigateTo(QueryHelpers.AddQueryString("/quality/inquiry/export", BuildExportQuery()), forceLoad: true);
        IsExporting = false;
    }

    protected void DismissError() => ErrorMessage = null;

    protected static string CaseHref(string caseNo) => $"/quality/cases/view/{Uri.EscapeDataString(caseNo)}";

    protected static string AgingLabel(int days) => days <= 0 ? "—" : $"{days}d";

    private async Task ReloadAsync()
    {
        var result = await Cases.GetInquiryAsync(BuildQuery(Skip, PageSize));
        if (!result.Succeeded || result.InquiryPage is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load quality inquiry.";
            Rows = [];
            Page = new();
            return;
        }

        Page = result.InquiryPage;
        Rows = result.InquiryPage.Rows.ToList();
    }

    private QaInquiryQuery BuildQuery(int skip, int take) => new()
    {
        SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        Status = AppliedStatus,
        SourceType = AppliedSource,
        DateFrom = AppliedDateFrom,
        DateTo = AppliedDateTo,
        ItemCode = AppliedItemCode,
        LotNo = AppliedLotNo,
        PoNo = AppliedPoNo,
        WorkOrderNo = AppliedWorkOrderNo,
        Supplier = AppliedSupplier,
        Inspector = AppliedInspector,
        Reviewer = AppliedReviewer,
        ActionOwner = AppliedActionOwner,
        OverdueOnly = AppliedOverdueOnly,
        Skip = skip,
        Take = take
    };

    private Dictionary<string, string?> BuildExportQuery()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Add(dict, "searchText", SearchText);
        Add(dict, "status", AppliedStatus);
        Add(dict, "sourceType", AppliedSource);
        Add(dict, "itemCode", AppliedItemCode);
        Add(dict, "lotNo", AppliedLotNo);
        Add(dict, "poNo", AppliedPoNo);
        Add(dict, "workOrderNo", AppliedWorkOrderNo);
        Add(dict, "supplier", AppliedSupplier);
        Add(dict, "inspector", AppliedInspector);
        Add(dict, "reviewer", AppliedReviewer);
        Add(dict, "actionOwner", AppliedActionOwner);
        if (AppliedDateFrom is { } from)
        {
            dict["dateFrom"] = from.ToString("yyyy-MM-dd");
        }

        if (AppliedDateTo is { } to)
        {
            dict["dateTo"] = to.ToString("yyyy-MM-dd");
        }

        if (AppliedOverdueOnly)
        {
            dict["overdueOnly"] = "true";
        }

        return dict;
    }

    private static void Add(Dictionary<string, string?> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value.Trim();
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _disposed = true;
        _searchDebounce?.Dispose();
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}
