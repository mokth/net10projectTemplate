using ErpWeb.Core.Inventory;
using ErpWeb.Core.Traceability;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Inquiry;

public partial class IvLotGenealogyInquiry : PageBase
{
    private static readonly string[] Tabs = ["Overview", "Backward Trace", "Forward Trace / Customer Impact", "Movements", "Cost Evidence"];
    private Func<Task<IvMasterOperationResult<LotGenealogyResult>>>? _lastTraceLoader;

    [Inject] private ILotGenealogyService Genealogy { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "lotId")]
    public int? InventoryLotId { get; set; }

    [SupplyParameterFromQuery(Name = "productionLotId")]
    public long? ProductionLotId { get; set; }

    [SupplyParameterFromQuery(Name = "workOrderId")]
    public long? WorkOrderId { get; set; }

    protected string? SearchText;
    protected string? ErrorMessage;
    protected string CurrentBranchLabel = "current branch";
    protected bool IsSearching;
    protected bool IsTracing;
    protected bool HasSearched;
    protected string ActiveTab = "Overview";
    protected IReadOnlyList<LotGenealogyCandidateGridRow> CandidateRows = [];
    protected LotGenealogyResult? Result;

    protected List<ButtonInfo> CandidateActions =>
    [
        new() { Text = "TRACE", ToolTip = "Trace this root", IConClass = "fa-solid fa-diagram-project", Style = "info", Enabled = !IsBusy }
    ];

    protected static List<GridColumnData> CandidateColumns =>
    [
        new() { Caption = "Root", FieldName = nameof(LotGenealogyCandidateGridRow.RootKind), Width = "120px", VisibleIndex = 1 },
        new() { Caption = "Item", FieldName = nameof(LotGenealogyCandidateGridRow.ItemCode), Width = "120px", VisibleIndex = 2 },
        new() { Caption = "Lot", FieldName = nameof(LotGenealogyCandidateGridRow.LotNo), Width = "140px", VisibleIndex = 3 },
        new() { Caption = "Work Order", FieldName = nameof(LotGenealogyCandidateGridRow.WorkOrderNo), Width = "140px", VisibleIndex = 4 },
        new() { Caption = "Title", FieldName = nameof(LotGenealogyCandidateGridRow.Title), Width = "220px", VisibleIndex = 5 },
        new() { Caption = "Branch", FieldName = nameof(LotGenealogyCandidateGridRow.BranchCode), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Details", FieldName = nameof(LotGenealogyCandidateGridRow.Detail), VisibleIndex = 7 }
    ];

    protected static List<GridColumnData> MovementColumns =>
    [
        new() { Caption = "Type", FieldName = nameof(LotGenealogyNode.Kind), Width = "150px", VisibleIndex = 1 },
        new() { Caption = "Evidence", FieldName = nameof(LotGenealogyNode.EvidenceState), Width = "170px", VisibleIndex = 2 },
        new() { Caption = "Branch", FieldName = nameof(LotGenealogyNode.BranchCode), Width = "100px", VisibleIndex = 3 },
        new() { Caption = "Date", FieldName = nameof(LotGenealogyNode.EventDate), DataType = "datetime", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "150px", VisibleIndex = 4 },
        new() { Caption = "Base qty", FieldName = nameof(LotGenealogyNode.BaseQty), DataType = "decimal", DisplayFormat = "n4", Width = "120px", VisibleIndex = 5 },
        new() { Caption = "UOM", FieldName = nameof(LotGenealogyNode.BaseUom), Width = "80px", VisibleIndex = 6 },
        new() { Caption = "Details", FieldName = nameof(LotGenealogyNode.Detail), VisibleIndex = 7 }
    ];

    protected static List<GridColumnData> CostEvidenceColumns =>
    [
        new() { Caption = "Movement ID", FieldName = nameof(LotGenealogyCostEvidence.MovementId), DataType = "int", Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Movement type", FieldName = nameof(LotGenealogyCostEvidence.MovementType), Width = "170px", VisibleIndex = 2 },
        new() { Caption = "Valuation status", FieldName = nameof(LotGenealogyCostEvidence.Status), Width = "180px", VisibleIndex = 3 },
        new() { Caption = "Evidence basis", FieldName = nameof(LotGenealogyCostEvidence.Basis), VisibleIndex = 4 }
    ];

    protected bool IsBusy => IsSearching || IsTracing;
    protected string CompletenessClass => Result?.Completeness switch
    {
        LotGenealogyCompleteness.Complete => "lot-trace-state--complete",
        LotGenealogyCompleteness.Truncated => "lot-trace-state--warning",
        _ => "lot-trace-state--incomplete"
    };

    protected IReadOnlyList<LotGenealogyNode> MovementNodes => Result?.Nodes
        .Where(x => x.Kind.Contains("movement", StringComparison.OrdinalIgnoreCase)
            || x.Kind.Contains("material issue", StringComparison.OrdinalIgnoreCase)
            || x.Kind.Contains("history", StringComparison.OrdinalIgnoreCase)
            || x.Kind.Contains("receipt fact", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.EventDate).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    protected override async Task OnInitializedAsync()
    {
        if (InventoryLotId is int lotId)
            await TraceInventoryLotAsync(lotId);
        else if (ProductionLotId is long productionLotId)
            await TraceProductionLotAsync(productionLotId);
        else if (WorkOrderId is long workOrderId)
            await TraceWorkOrderAsync(workOrderId);
    }

    protected async Task OnSearchKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
            await SearchAsync();
    }

    protected async Task SearchAsync()
    {
        ErrorMessage = null;
        HasSearched = true;
        IsSearching = true;
        CandidateRows = [];
        Result = null;
        try
        {
            var result = await Genealogy.SearchAsync(new LotGenealogySearchQuery { SearchText = SearchText, Take = 100 });
            if (result.Succeeded && result.Data is not null)
                CandidateRows = result.Data.Candidates.Select(LotGenealogyCandidateGridRow.From).ToArray();
            else
                ErrorMessage = result.Message ?? "Unable to search traceability evidence.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    protected Task OnSearchTextChanged(string text)
    {
        SearchText = text;
        return Task.CompletedTask;
    }

    protected Task OnCandidateActionAsync(SelectedButtonInfo<LotGenealogyCandidateGridRow> info) =>
        info.SelectedRow is { Candidate: { } candidate }
            ? TraceCandidateAsync(candidate)
            : Task.CompletedTask;

    protected async Task OnCandidateGridButtonAsync(SelectedButtonInfo<LotGenealogyCandidateGridRow> info)
    {
        if (!IsRefresh(info) || IsBusy)
            return;

        var selectedTrace = Result;
        await SearchAsync();
        Result = selectedTrace;
    }

    protected Task OnMovementGridButtonAsync(SelectedButtonInfo<LotGenealogyNode> info) =>
        IsRefresh(info) ? RefreshTraceAsync() : Task.CompletedTask;

    protected Task OnCostEvidenceGridButtonAsync(SelectedButtonInfo<LotGenealogyCostEvidence> info) =>
        IsRefresh(info) ? RefreshTraceAsync() : Task.CompletedTask;

    protected async Task RefreshTraceAsync()
    {
        if (IsBusy || _lastTraceLoader is null)
            return;

        await LoadTraceAsync(_lastTraceLoader, keepSelectedTab: true);
    }

    protected void SelectTab(string tab) => ActiveTab = tab;

    protected void DismissError() => ErrorMessage = null;

    protected Task TraceCandidateAsync(LotGenealogySearchCandidate candidate) => candidate.RootKind switch
    {
        LotGenealogyRootKind.InventoryLot => TraceInventoryLotAsync(checked((int)candidate.RootId)),
        LotGenealogyRootKind.ProductionLot => TraceProductionLotAsync(candidate.RootId),
        LotGenealogyRootKind.WorkOrder => TraceWorkOrderAsync(candidate.RootId),
        _ => Task.CompletedTask
    };

    protected async Task TraceInventoryLotAsync(int lotId) =>
        await LoadTraceAsync(() => Genealogy.TraceInventoryLotAsync(lotId));

    protected async Task TraceProductionLotAsync(long lotId) =>
        await LoadTraceAsync(() => Genealogy.TraceProductionLotAsync(lotId));

    protected async Task TraceWorkOrderAsync(long workOrderId) =>
        await LoadTraceAsync(() => Genealogy.TraceWorkOrderAsync(workOrderId));

    private async Task LoadTraceAsync(
        Func<Task<IvMasterOperationResult<LotGenealogyResult>>> load,
        bool keepSelectedTab = false)
    {
        _lastTraceLoader = load;
        ErrorMessage = null;
        IsTracing = true;
        Result = null;
        if (!keepSelectedTab)
            ActiveTab = "Overview";
        try
        {
            var result = await load();
            if (result.Succeeded && result.Data is not null)
            {
                Result = result.Data;
                CurrentBranchLabel = result.Data.CurrentBranch ?? "current branch";
            }
            else
                ErrorMessage = result.Message ?? "Unable to trace this exact root.";
        }
        finally
        {
            IsTracing = false;
        }
    }

    private static bool IsRefresh<T>(SelectedButtonInfo<T> info) =>
        string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase);
}

public sealed class LotGenealogyCandidateGridRow
{
    public string Id { get; init; } = string.Empty;
    public string RootKind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? ItemCode { get; init; }
    public string? LotNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? BranchCode { get; init; }
    public string? Detail { get; init; }
    public LotGenealogySearchCandidate Candidate { get; init; } = new();

    public static LotGenealogyCandidateGridRow From(LotGenealogySearchCandidate candidate) => new()
    {
        Id = $"{candidate.RootKind}:{candidate.RootId}",
        RootKind = candidate.RootKind.ToString(),
        Title = candidate.Title,
        ItemCode = candidate.ItemCode,
        LotNo = candidate.LotNo,
        WorkOrderNo = candidate.WorkOrderNo,
        BranchCode = candidate.BranchCode,
        Detail = candidate.Detail,
        Candidate = candidate
    };
}
