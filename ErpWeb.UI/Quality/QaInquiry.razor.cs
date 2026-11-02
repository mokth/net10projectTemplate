using System.Timers;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Quality;

public partial class QaInquiry : PageBase, IDisposable
{
    [Inject] private IQaCaseService Cases { get; set; } = default!;

    private Timer? _searchDebounce;
    private int _searchVersion;
    private bool _disposed;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string SearchText = string.Empty;
    protected string? AppliedStatus;
    protected string? AppliedSource;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected string DraftSourceKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected List<QaInquiryRow> Rows { get; set; } = [];
    protected QaInquiryPage Page { get; set; } = new();

    protected string CaseCountLabel => Page.CaseCount == 1 ? "1 case" : $"{Page.CaseCount:N0} cases";
    protected string PendingReviewLabel => $"{Page.PendingReviewCount:N0} pending review";
    protected string OpenDestinationLabel => $"{Page.OpenDestinationCount:N0} open destinations";
    protected string FirstPassYieldDisplay => $"{Page.FirstPassYield:P0}";
    protected string FirstPassYieldNote => string.IsNullOrWhiteSpace(Page.FirstPassYieldNote)
        ? "First-pass yield is approved cases with zero rejected qty, divided by cases that reached inspection."
        : Page.FirstPassYieldNote;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaCaseStatuses.All.Select(x => new StatusFilterOption(x, x))
    ];

    protected IReadOnlyList<StatusFilterOption> SourceFilterOptions { get; } =
    [
        new("all", "All"),
        ..QaSourceTypes.All.Select(x => new StatusFilterOption(x, x))
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected async Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty;
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

    protected Task ApplySearchAsync() => ReloadAsync();

    protected void OpenFilterPopup()
    {
        DraftStatusKey = string.IsNullOrWhiteSpace(AppliedStatus) ? "all" : AppliedStatus;
        DraftSourceKey = string.IsNullOrWhiteSpace(AppliedSource) ? "all" : AppliedSource;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedSource = string.Equals(DraftSourceKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftSourceKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusKey = "all";
        DraftSourceKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        AppliedStatus = null;
        AppliedSource = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected void DismissError() => ErrorMessage = null;

    protected static string CaseHref(string caseNo) => $"/quality/cases/view/{Uri.EscapeDataString(caseNo)}";

    protected static string AgingLabel(int days) => days <= 0 ? "—" : $"{days}d";

    private async Task ReloadAsync()
    {
        var result = await Cases.GetInquiryAsync(new QaInquiryQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            SourceType = AppliedSource,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            Skip = 0,
            Take = 100
        });
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

    public void Dispose()
    {
        _disposed = true;
        _searchDebounce?.Dispose();
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}
