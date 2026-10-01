using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Maintenance;

public partial class PrMachineMaintenanceSummary : PageBase
{
    [Inject] private IPrMachineMaintenanceSummaryService SummaryService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;

    protected DateTime FromDate { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    protected DateTime ToDate { get; set; } = DateTime.Today;
    protected string? MachineCd { get; set; }
    protected List<MachineCdOption> Machines { get; set; } = [];

    protected bool IsLoading;
    protected string? StatusMessage;
    protected MachineMaintenanceSummaryResult? Result;

    protected override async Task OnPageInitializedAsync()
    {
        var m = await MachineService.ListAsync();
        if (m.Succeeded)
        {
            Machines = m.Value!
                .Select(x => x.MachineCd)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(c => new MachineCdOption { MachineCd = c })
                .ToList();
        }

        await RunAsync();
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected async Task ClearFiltersAsync()
    {
        FromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ToDate = DateTime.Today;
        MachineCd = null;
        await RunAsync();
    }

    protected async Task RunAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await SummaryService.GetAsync(new MachineMaintenanceSummaryFilter
            {
                FromDate = FromDate,
                ToDate = ToDate,
                MachineCd = MachineCd
            });

            if (!result.Succeeded)
            {
                Result = null;
                ErrorMessage = result.Message ?? "Unable to load summary.";
            }
            else
            {
                Result = result.Value;
                StatusMessage = null;
            }
        }
        finally { IsLoading = false; }
    }

    protected static string HoursText(decimal value) => value.ToString("n2");
    protected static string MoneyText(decimal value) => value.ToString("n2");
}
