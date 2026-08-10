using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvPeriod : PageBase
{
    [Inject] private IInventoryPeriodService PeriodsService { get; set; } = default!;
    [Inject] private IInventoryAsOfService AsOfService { get; set; } = default!;

    protected string? StatusMessage;
    protected PeriodModel Model { get; set; } = new()
    {
        FiscalYear = DateTime.Today.Year,
        FiscalMonth = DateTime.Today.Month,
        AsOfDate = DateTime.Today
    };
    protected List<InventoryPeriod> Periods { get; set; } = [];
    protected List<StockSnapshot> Snapshots { get; set; } = [];
    protected InventoryValuationDto? Valuation { get; set; }

    protected override async Task OnPageInitializedAsync() => await RefreshAsync();

    protected void SelectPeriod(long id) => Model.SelectedPeriodId = id;

    protected async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var list = await PeriodsService.ListPeriodsAsync();
            Periods = list.Succeeded ? list.Periods.ToList() : [];
            if (!list.Succeeded) ErrorMessage = list.ErrorMessage;
            if (Model.SelectedPeriodId == 0 && Periods.Count > 0)
                Model.SelectedPeriodId = Periods[0].Id;
            if (Model.SelectedPeriodId > 0)
            {
                var snaps = await PeriodsService.GetSnapshotsAsync(Model.SelectedPeriodId);
                Snapshots = snaps.Succeeded ? snaps.Snapshots.ToList() : [];
            }
        }
        finally { IsBusy = false; }
    }

    protected async Task EnsureAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await PeriodsService.EnsurePeriodAsync(Model.FiscalYear, Model.FiscalMonth);
            if (!result.Succeeded) { ErrorMessage = $"{result.ErrorCode}: {result.ErrorMessage}"; return; }
            StatusMessage = $"Period {result.Period!.FiscalYear}-{result.Period.FiscalMonth:00} ready.";
            Model.SelectedPeriodId = result.Period.Id;
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    protected async Task CloseAsync()
    {
        if (Model.SelectedPeriodId <= 0) { ErrorMessage = "Select a period first."; return; }
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await PeriodsService.ClosePeriodAsync(Model.SelectedPeriodId, CurrentUser?.UserId ?? "user");
            if (!result.Succeeded) { ErrorMessage = $"{result.ErrorCode}: {result.ErrorMessage}"; return; }
            StatusMessage = $"Closed. Snapshots: {result.Snapshots.Count}";
            Snapshots = result.Snapshots.ToList();
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    protected async Task AsOfAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await AsOfService.GetAsOfValuationAsync(Model.AsOfDate);
            if (!result.Succeeded) { ErrorMessage = $"{result.ErrorCode}: {result.ErrorMessage}"; return; }
            Valuation = result.Valuation;
            StatusMessage = $"As-of {Valuation!.AsOfDate:d}: {Valuation.Lines.Count} lines.";
        }
        finally { IsBusy = false; }
    }

    protected sealed class PeriodModel
    {
        public int FiscalYear { get; set; }
        public int FiscalMonth { get; set; }
        public DateTime AsOfDate { get; set; }
        public long SelectedPeriodId { get; set; }
    }
}
