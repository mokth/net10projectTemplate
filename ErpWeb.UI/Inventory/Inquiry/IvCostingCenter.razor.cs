using ErpWeb.Core.Costing;
using ErpWeb.Core.Inventory;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Inquiry;

public partial class IvCostingCenter : PageBase
{
    private const string PageRoute = "/inventory/costing-center";

    [Inject] private ICostingDiagnosticService Diagnostics { get; set; } = default!;
    [Inject] private ICostingTraceService Trace { get; set; } = default!;
    [Inject] private ICostingRepairPlanner Planner { get; set; } = default!;
    [Inject] private ICostingRepairService RepairService { get; set; } = default!;
    [Inject] private ICostingRepairOwnershipResolver Ownership { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    protected string? ItemCode;
    protected string? WarehouseCode;
    protected string? DocumentNo;
    protected string? DocumentType;
    protected DateTime? DateFrom;
    protected DateTime? DateTo;
    protected string? FindingCode;
    protected string? SeverityText;
    protected string? PostingIdText;
    protected CostingRepairPlan? Preview;
    protected long? PreviewPostingId;
    protected string? PreviewItemCode;
    protected string? PreviewCostMethod;
    protected CostingRepairTargetKind PreviewKind;
    protected string? RepairReason;
    protected string? RepairOutcome;
    protected string EpochCaption = "Coverage not loaded";
    protected bool MonetaryVisible;
    protected bool HealthRan;
    protected bool TraceRan;
    protected bool BackdateRan;
    protected bool HealthRunning;
    protected bool TraceRunning;
    protected bool BackdateRunning;
    protected bool RepairRunning;
    protected bool WarehousesLoading;
    protected bool ShowAdvanced;
    protected bool ShowPreviewTechnical;
    protected bool ConfirmVisible;
    protected long? TechnicalTracePostingId;
    protected decimal OpeningQty;
    protected decimal OpeningValue;
    protected decimal OpeningAverage;
    protected string BackdateGuidance = string.Empty;
    protected IReadOnlyList<CostingFinding> Findings { get; set; } = [];
    protected IReadOnlyList<CostingTraceLine> TraceLines { get; set; } = [];
    protected IReadOnlyList<CostingBackdateBlocker> BackdateBlockers { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];

    protected sealed record DocumentTypeOption(string Value, string Label);

    protected static readonly DocumentTypeOption[] DocumentTypes =
    [
        new(CostingDocumentTypes.GoodsReceipt, "Goods Receipt"),
        new(CostingDocumentTypes.SalesDeliveryOrder, "Delivery Order"),
        new(CostingDocumentTypes.SalesInvoice, "Sales Invoice"),
        new(CostingDocumentTypes.SalesCreditNote, "Sales Credit Note"),
        new(CostingDocumentTypes.PurchaseCreditNote, "Purchase Credit Note"),
        new(CostingDocumentTypes.MiscReceipt, "Misc Receipt"),
        new(CostingDocumentTypes.MiscIssue, "Misc Issue"),
        new(CostingDocumentTypes.Scrap, "Scrap"),
        new(CostingDocumentTypes.VendorReturn, "Vendor Return"),
        new(CostingDocumentTypes.Transfer, "Stock Transfer"),
        new(CostingDocumentTypes.Adjustment, "Stock Adjustment"),
        new(CostingDocumentTypes.CustomerReturn, "Customer Return"),
        new(CostingDocumentTypes.MaterialIssue, "Material Issue"),
        new(CostingDocumentTypes.FinishedGoodReceipt, "Finished Good Receipt"),
        new(CostingDocumentTypes.DailyProduction, "Daily Production")
    ];

    protected static readonly string[] FindingCodes =
    [
        CostingFindingCodes.UnsealedPosting,
        CostingFindingCodes.HistoryMissingValuation,
        CostingFindingCodes.FactWithoutSealedPosting,
        CostingFindingCodes.CostStateQuantityMismatch,
        CostingFindingCodes.CostStateValueMismatch,
        CostingFindingCodes.CostStateAverageMismatch,
        CostingFindingCodes.ReversalLineageBroken,
        CostingFindingCodes.ZeroQuantityResidue,
        CostingFindingCodes.EpochCoverage,
        CostingFindingCodes.SalesCogsLineageIncomplete,
        CostingFindingCodes.CostStateMissing
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadWarehousesAsync();
    }

    protected void DismissError() => ErrorMessage = null;

    protected void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    protected void TogglePreviewTechnical() => ShowPreviewTechnical = !ShowPreviewTechnical;

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

    private CostingHealthQuery CurrentQuery() => new(
        ItemCode,
        WarehouseCode,
        DateFrom,
        DateTo,
        string.IsNullOrWhiteSpace(FindingCode) ? null : FindingCode,
        string.IsNullOrWhiteSpace(DocumentNo) ? null : DocumentNo.Trim(),
        string.IsNullOrWhiteSpace(DocumentType) ? null : DocumentType,
        Enum.TryParse<CostingFindingSeverity>(SeverityText, out var severity) ? severity : null);

    protected async Task RunHealthAsync()
    {
        ErrorMessage = null;
        HealthRunning = true;
        try
        {
            var page = await Diagnostics.SearchAsync(CurrentQuery());
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

            var page = await Trace.GetItemTimelineAsync(new CostingTraceQuery(
                ItemCode.Trim(), DateFrom, DateTo, WarehouseCode));
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

    protected async Task RunBackdateImpactAsync()
    {
        ErrorMessage = null;
        BackdateRan = true;
        BackdateRunning = true;
        try
        {
            if (string.IsNullOrWhiteSpace(ItemCode))
            {
                ErrorMessage = "Select an item, then set From to the earlier document date you want to post.";
                BackdateBlockers = [];
                BackdateGuidance = string.Empty;
                return;
            }

            if (DateFrom is null)
            {
                ErrorMessage = "Set From to the target (earlier) posting date, then click Later movements to roll back.";
                BackdateBlockers = [];
                BackdateGuidance = string.Empty;
                return;
            }

            var page = await Trace.GetBackdateImpactAsync(ItemCode.Trim(), DateFrom.Value);
            if (page.Denied || page.Error is not null)
            {
                ErrorMessage = page.Error ?? "Not authorized.";
                BackdateBlockers = [];
                BackdateGuidance = string.Empty;
                return;
            }

            BackdateGuidance = page.Guidance;
            BackdateBlockers = page.Blockers;
            EpochCaption = "V2";
        }
        finally
        {
            BackdateRunning = false;
        }
    }

    protected Task PreviewFindingAsync(CostingFinding finding)
    {
        if (finding.RepairTargetKind == CostingRepairTargetKind.CostState)
            return PreviewStateAsync(finding.ItemCode, finding.CostMethod, finding.Code);
        if (finding.RepairTargetKind == CostingRepairTargetKind.Posting && finding.StockPostingId is long postingId)
            return PreviewPostingAsync(postingId);
        return ExplainDiagnosticAsync(finding);
    }

    protected Task PreviewTraceAsync(CostingTraceLine line) => PreviewPostingAsync(line.StockPostingId);

    protected async Task PreviewPostingAsync(long postingId)
    {
        ErrorMessage = null;
        RepairOutcome = null;
        ShowPreviewTechnical = false;
        PreviewKind = CostingRepairTargetKind.Posting;
        PreviewPostingId = postingId;
        PreviewItemCode = null;
        PreviewCostMethod = null;
        Preview = await Planner.PlanAsync(new CostingRepairTarget(CostingRepairTargetKind.Posting, postingId, null, null, null));
    }

    private async Task PreviewStateAsync(string? itemCode, string? costMethod, string? findingCode)
    {
        ErrorMessage = null;
        RepairOutcome = null;
        ShowPreviewTechnical = false;
        PreviewKind = CostingRepairTargetKind.CostState;
        PreviewPostingId = null;
        PreviewItemCode = itemCode;
        PreviewCostMethod = costMethod;
        Preview = await Planner.PlanAsync(new CostingRepairTarget(
            CostingRepairTargetKind.CostState, null, itemCode, costMethod, findingCode));
    }

    private Task ExplainDiagnosticAsync(CostingFinding finding)
    {
        PreviewKind = CostingRepairTargetKind.DiagnosticOnly;
        PreviewPostingId = finding.StockPostingId;
        PreviewItemCode = finding.ItemCode;
        PreviewCostMethod = finding.CostMethod;
        Preview = new CostingRepairPlan(
            false, "PREVIEW_ONLY", finding.Explanation, [], [], [], null, null, 0, 0, [], string.Empty);
        return Task.CompletedTask;
    }

    protected Task RunPreviewAsync()
    {
        if (!long.TryParse(PostingIdText, out var postingId))
        {
            ErrorMessage = "Enter a stock posting id in Technical / Support Tools.";
            Preview = null;
            return Task.CompletedTask;
        }

        return PreviewPostingAsync(postingId);
    }

    protected void AskRollback()
    {
        if (Preview is not { CanRepair: true } || PreviewKind != CostingRepairTargetKind.Posting)
            return;
        ConfirmVisible = true;
    }

    protected void AskRebuild()
    {
        if (Preview is not { CanRepair: true } || PreviewKind != CostingRepairTargetKind.CostState)
            return;
        ConfirmVisible = true;
    }

    protected void CancelConfirm() => ConfirmVisible = false;

    protected async Task ConfirmRepairAsync()
    {
        ConfirmVisible = false;
        if (string.IsNullOrWhiteSpace(RepairReason))
        {
            ErrorMessage = "Enter a repair reason before continuing.";
            return;
        }

        RepairRunning = true;
        ErrorMessage = null;
        try
        {
            CostingRepairExecutionResult result;
            if (PreviewKind == CostingRepairTargetKind.CostState)
            {
                result = await RepairService.ExecuteStateRebuildAsync(
                    PreviewItemCode ?? string.Empty,
                    PreviewCostMethod ?? string.Empty,
                    Preview?.PreviewHash ?? string.Empty,
                    RepairReason);
            }
            else
            {
                result = await RepairService.ExecuteReverseAsync(
                    PreviewPostingId ?? 0,
                    Preview?.PreviewHash ?? string.Empty,
                    RepairReason);
            }

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message ?? "The repair did not complete.";
                RepairOutcome = null;
                return;
            }

            await RecheckAsync(result);
        }
        finally
        {
            RepairRunning = false;
        }
    }

    private async Task RecheckAsync(CostingRepairExecutionResult result)
    {
        var code = PreviewKind == CostingRepairTargetKind.CostState
            ? Findings.FirstOrDefault(x => x.RepairTargetKind == CostingRepairTargetKind.CostState
                && string.Equals(x.ItemCode, PreviewItemCode, StringComparison.OrdinalIgnoreCase))?.Code
            : Findings.FirstOrDefault(x => x.StockPostingId == PreviewPostingId)?.Code;
        await RunHealthAsync();
        if (!string.IsNullOrWhiteSpace(PreviewItemCode) || !string.IsNullOrWhiteSpace(ItemCode))
        {
            if (string.IsNullOrWhiteSpace(ItemCode))
                ItemCode = PreviewItemCode;
            await RunTraceAsync();
        }

        var still = string.IsNullOrWhiteSpace(code)
            ? Findings.Count(x => x.IsBlocking)
            : Findings.Count(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)
                && (PreviewKind != CostingRepairTargetKind.CostState
                    || string.Equals(x.ItemCode, PreviewItemCode, StringComparison.OrdinalIgnoreCase)));
        var document = Preview?.AffectedDocuments.FirstOrDefault() ?? Preview?.Steps.FirstOrDefault()?.OwnerDocumentNo;
        if (PreviewKind == CostingRepairTargetKind.Posting)
        {
            RepairOutcome = still == 0
                ? $"Source posting successfully rolled back. Costing health has been rechecked. Original finding {code ?? "the finding"} is cleared. Next action: correct and repost {document}."
                : $"Rolled back — correct and repost {document}. Costing health still has {still} matching blocking finding(s).";
        }
        else
        {
            RepairOutcome = still == 0
                ? $"Derived cost state rebuilt. Costing health has been rechecked. Original finding {code ?? "the finding"} is cleared."
                : $"Repair action completed, but costing still has {still} matching blocking finding(s). Review them before treating the item as clean.";
        }

        if (result.RepairRequestId is not null)
            RepairOutcome += $" Audit request {result.RepairRequestId}.";
        Preview = null;
    }

    protected async Task OpenPostingAsync(long postingId)
    {
        var ownership = await Ownership.ResolveAsync(new CostingRepairNode(postingId, string.Empty, string.Empty, string.Empty));
        var link = CostingSourceNavigationResolver.Resolve(ownership);
        if (link is null && ownership.Owner is not null)
            link = CostingSourceNavigationResolver.PhysicalLink(
                ownership.Owner.PhysicalSourceDocumentType,
                ownership.Owner.PhysicalSourceDocumentNo,
                ownership.Owner.PhysicalSourceDocumentId,
                ownership.Owner.OwnerDocumentNo);
        if (link is null)
        {
            ErrorMessage = ownership.BlockingReason ?? "No page is registered for this document.";
            return;
        }

        Navigation.NavigateTo(DocumentReturnNavigation.WithReturnUrl(link.Route, PageRoute));
    }

    protected string PreviewDocumentLabel()
    {
        var step = Preview?.Steps.FirstOrDefault();
        if (step is null)
            return Preview?.AffectedDocuments.FirstOrDefault() ?? "—";
        return $"{step.OwnerType} {step.OwnerDocumentNo}";
    }
}
