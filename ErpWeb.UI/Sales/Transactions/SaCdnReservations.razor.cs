using System.Timers;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// E7 — read-only diagnostic listing posted invoices whose remaining balance is held by credit notes,
/// and naming the drafts that reserve it. Nothing on this screen mutates a document.
/// </summary>
public partial class SaCdnReservations : PageBase, IDisposable
{
    [Inject] private ISaCdnService Cdns { get; set; } = default!;

    private Timer? _searchDebounce;
    private CancellationTokenSource _cts = new();

    protected bool IsLoading = true;
    /// <summary>Local to this page — <see cref="PageBase.ErrorMessage"/> is deliberately not reused.</summary>
    protected string? LoadError;
    protected string CustCode = string.Empty;
    protected bool DraftsOnly;
    protected bool OverReservedOnly;

    protected List<SaCdnReservationReportRow> Rows { get; set; } = [];
    protected int TotalCount { get; set; }

    protected string MenuCode => MenuCodes.SalesCdnReservations;

    protected string TotalCountLabel => TotalCount == 1 ? "1 invoice" : $"{TotalCount} invoices";
    protected int OverReservedCount => Rows.Count(x => x.OverReserved);

    protected override Task OnPageInitializedAsync() => LoadAsync();

    protected async Task ReloadAsync() => await LoadAsync();

    protected void OnCustCodeChanged(string? value)
    {
        CustCode = value ?? string.Empty;

        // Debounce: this screen is diagnostic, so a keystroke must not fan out into a query per char.
        _searchDebounce ??= new Timer(300) { AutoReset = false };
        _searchDebounce.Stop();
        _searchDebounce.Elapsed -= OnDebounceElapsed;
        _searchDebounce.Elapsed += OnDebounceElapsed;
        _searchDebounce.Start();
    }

    protected Task OnFilterChanged() => LoadAsync();

    private async void OnDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        // async void is required by the Timer signature; the body never throws past its own guard.
        try
        {
            await InvokeAsync(LoadAsync);
        }
        catch (Exception)
        {
            // Swallow timer-thread failures — the grid simply keeps its previous contents.
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var result = await Cdns.GetReservationReportAsync(
                new SaCdnReservationReportQuery
                {
                    CustCode = string.IsNullOrWhiteSpace(CustCode) ? null : CustCode.Trim(),
                    DraftsOnly = DraftsOnly,
                    OverReservedOnly = OverReservedOnly
                },
                _cts.Token);

            if (!result.Succeeded || result.ReservationReport is null)
            {
                LoadError = result.ErrorMessage ?? "Unable to load credit-note reservations.";
                Rows = [];
                TotalCount = 0;
                return;
            }

            Rows = result.ReservationReport.Rows.ToList();
            TotalCount = result.ReservationReport.TotalCount;
        }
        catch (OperationCanceledException)
        {
            // navigation away — leave the previous contents in place.
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected void DismissError() => LoadError = null;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _searchDebounce?.Dispose();
    }
}
