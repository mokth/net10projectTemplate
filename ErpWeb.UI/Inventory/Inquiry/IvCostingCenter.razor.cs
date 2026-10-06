using ErpWeb.Core.Costing;
using ErpWeb.Core.Inventory;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Inquiry;

public partial class IvCostingCenter : PageBase
{
    [Inject] private ICostingDiagnosticService Diagnostics { get; set; } = default!;
    [Inject] private ICostingTraceService Trace { get; set; } = default!;
    [Inject] private ICostingRepairPlanner Planner { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    protected string? ItemCode;
    protected string? WarehouseCode;
    protected string? PostingIdText;
    protected CostingRepairPlan? Preview;
    protected long? PreviewPostingId;
    protected string EpochCaption = "Coverage not loaded";
    protected bool MonetaryVisible;
    protected bool HealthRan;
    protected bool TraceRan;
    protected bool HealthRunning;
    protected bool TraceRunning;
    protected bool WarehousesLoading;
    protected bool ShowAdvanced;
    protected decimal OpeningQty;
    protected decimal OpeningValue;
    protected decimal OpeningAverage;
    protected IReadOnlyList<CostingFinding> Findings { get; set; } = [];
    protected IReadOnlyList<CostingTraceLine> TraceLines { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadWarehousesAsync();
    }

    protected void DismissError() => ErrorMessage = null;

    protected void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    protected Task OnItemCodeChangedAsync(string? itemCode)
    {
        ItemCode = string.IsNullOrWhiteSpace(itemCode) ? null : itemCode.Trim();
        return Task.CompletedTask;
    }

    private async Task LoadWarehousesAsync()
    {
        WarehousesLoading = true;
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        WarehousesLoading = false;
        if (!warehouses.Succeeded)
            ErrorMessage = warehouses.ErrorMessage ?? "Unable to load warehouses.";
    }

    protected async Task RunHealthAsync()
    {
        ErrorMessage = null;
        HealthRunning = true;
        try
        {
            var page = await Diagnostics.SearchAsync(new CostingHealthQuery(ItemCode, WarehouseCode));
            if (page.Denied || page.Error is not null)
            {
                ErrorMessage = page.Error ?? "Not authorized.";
                Findings = [];
                HealthRan = false;
                return;
            }

            MonetaryVisible = page.MonetaryValuesVisible;
            EpochCaption = page.EpochCoverage;
            Findings = page.Findings;
            HealthRan = true;
        }
        finally
        {
            HealthRunning = false;
        }
    }

    protected async Task RunTraceAsync()
    {
        ErrorMessage = null;
        TraceRan = true;
        TraceRunning = true;
        try
        {
            if (string.IsNullOrWhiteSpace(ItemCode))
            {
                ErrorMessage = "Select an item before running the cost trace.";
                TraceLines = [];
                return;
            }

            var page = await Trace.GetItemTimelineAsync(new CostingTraceQuery(ItemCode.Trim(), WarehouseCode: WarehouseCode));
            if (page.Denied || page.Error is not null)
            {
                ErrorMessage = page.Error ?? "Not authorized.";
                TraceLines = [];
                return;
            }

            MonetaryVisible = page.MonetaryValuesVisible;
            EpochCaption = page.EpochCoverage;
            OpeningQty = page.Anchor.OpeningQty;
            OpeningValue = page.Anchor.OpeningValue;
            OpeningAverage = page.Anchor.OpeningAverage;
            TraceLines = page.Lines;
        }
        finally
        {
            TraceRunning = false;
        }
    }

    protected Task RunPreviewForFindingAsync(long postingId)
    {
        PostingIdText = postingId.ToString();
        return RunPreviewAsync(postingId);
    }

    protected Task RunPreviewAsync()
    {
        if (!long.TryParse(PostingIdText, out var postingId))
        {
            ErrorMessage = "Enter a stock posting id, or use Preview on a health finding.";
            Preview = null;
            PreviewPostingId = null;
            return Task.CompletedTask;
        }

        return RunPreviewAsync(postingId);
    }

    private async Task RunPreviewAsync(long postingId)
    {
        ErrorMessage = null;
        Preview = null;
        PreviewPostingId = postingId;
        Preview = await Planner.PlanAsync(postingId);
        if (!Preview.CanRepair)
            ErrorMessage = Preview.BlockingReason;
    }
}
