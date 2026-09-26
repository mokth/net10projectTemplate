using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Dashboard;

/// <summary>
/// Sales Dashboard (plan-salesReportsAndInquiries.prompt.md Phase 3) — read-only KPI chips and charts
/// over the current company. AR tiles are deliberately omitted (AR dependency).
/// </summary>
public partial class SaDashboard : PageBase
{
    [Inject] private ISaSalesDashboardService DashboardService { get; set; } = default!;

    protected SaDashboardResult? Result { get; set; }
    protected bool IsLoading { get; set; }
    protected bool HasLoaded { get; set; }

    protected IReadOnlyList<SaSalesSummaryRow> TopCustomers =>
        (Result?.ByCustomer ?? []).Take(10).ToList();

    protected IReadOnlyList<SaSalesSummaryRow> TopSalespeople =>
        (Result?.BySalesperson ?? []).Take(10).ToList();

    protected IReadOnlyList<SaSalesDetailRow> Categories =>
        (Result?.ByCategory ?? []).Take(10).ToList();

    protected IReadOnlyList<SaSalesDetailRow> TopItems =>
        Result?.TopItems ?? [];

    protected override async Task OnPageInitializedAsync() => await LoadAsync();

    protected async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await DashboardService.GetDashboardAsync();
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message ?? "Unable to load the dashboard.";
                Result = null;
                return;
            }

            Result = result.Data;
            HasLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected void DismissError() => ErrorMessage = null;

    protected static string MoneyText(decimal value) => value.ToString("N2");

    protected static string QtyText(decimal value) => value.ToString("N4");
}
