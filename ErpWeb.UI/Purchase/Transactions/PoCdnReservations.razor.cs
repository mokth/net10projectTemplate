using System.Timers;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Read-only diagnostic listing posted purchase invoices whose remaining balance is held by
/// credit notes. Nothing on this screen mutates a document — the equivalent of the sales
/// <c>SaCdnReservations</c> page for the purchase side.
/// </summary>
public partial class PoCdnReservations : PageBase, IDisposable
{
    [Inject] private IPoCdnService Cdns { get; set; } = default!;

    private Timer? _searchDebounce;
    private CancellationTokenSource _cts = new();

    protected bool IsLoading = true;

    /// <summary>Local to this page — <see cref="PageBase.ErrorMessage"/> is deliberately not reused.</summary>
    protected string? LoadError;
    protected string InvNo = string.Empty;
    protected string VendorCode = string.Empty;
    protected bool OverReservedOnly;

    protected List<PoCdnReservationReportRow> Rows { get; set; } = [];
    protected int TotalCount { get; set; }

    protected string MenuCode => MenuCodes.PurchaseCreditNoteReservations;

    protected string TotalCountLabel => TotalCount == 1 ? "1 invoice" : $"{TotalCount} invoices";
    protected int OverReservedCount => Rows.Count(x => x.OverReserved);

    protected override Task OnPageInitializedAsync() => LoadAsync();

    protected async Task ReloadAsync() => await LoadAsync();

    protected void OnInvNoChanged(string? value)
    {
        InvNo = value ?? string.Empty;
        Debounce();
    }

    protected void OnVendorCodeChanged(string? value)
    {
        VendorCode = value ?? string.Empty;
        Debounce();
    }

    protected Task OnFilterChanged() => LoadAsync();

    /// <summary>
    /// This screen is diagnostic, so a keystroke must not fan out into a query per character.
    /// </summary>
    private void Debounce()
    {
        _searchDebounce ??= new Timer(300) { AutoReset = false };
        _searchDebounce.Stop();
        _searchDebounce.Elapsed -= OnDebounceElapsed;
        _searchDebounce.Elapsed += OnDebounceElapsed;
        _searchDebounce.Start();
    }

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
                new PoCdnReservationReportQuery
                {
                    InvNo = string.IsNullOrWhiteSpace(InvNo) ? null : InvNo.Trim(),
                    VendorCode = string.IsNullOrWhiteSpace(VendorCode) ? null : VendorCode.Trim(),
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
