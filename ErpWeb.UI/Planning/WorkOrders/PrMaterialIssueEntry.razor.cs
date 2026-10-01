using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrMaterialIssueEntry : PageBase
{
    [Parameter] public string? WorkOrderNo { get; set; }
    [Parameter] public int? BatchNo { get; set; }
    [Inject] private IProductionMaterialIssueService MaterialIssues { get; set; } = default!;
    [Inject] private IProductionMaterialAllocationService AllocationService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool AllocationVisible;
    protected bool PostConfirmationVisible;
    protected bool ShowShortageOnly;
    protected bool CanAdd;
    protected bool CanPost;
    protected string? StatusMessage;
    protected string WorkOrderInput = string.Empty;
    protected string Remark = string.Empty;
    protected string SelectedWorkCentre = string.Empty;
    protected long? SelectedOperationId;
    protected DateTime IssueDate;
    protected ProductionMaterialIssueWorkspace? Workspace;
    protected ProductionMaterialIssueDocument? Document;
    protected List<MaterialLineVm> Lines { get; set; } = [];
    protected MaterialLineVm? AllocationLine;
    protected List<string> PostWarnings { get; set; } = [];
    private string? _loadedKey;
    private string? _postingRequestId;

    protected bool IsViewMode => BatchNo is > 0;
    protected string PageHeading => IsViewMode ? "View Issue to Production" : "New Issue to Production";
    protected string HeaderStatus => IsViewMode ? Document?.Status ?? "View" : Workspace?.Status ?? "Draft";
    protected int SelectedLineCount => IsViewMode ? Document?.Lines.Select(x => x.WorkOrderMaterialId).Distinct().Count() ?? 0 : Lines.Count(x => x.IssueQty > 0m);
    protected int AllocationRowCount => Lines.Where(x => x.IssueQty > 0m).Sum(x => x.Allocations.Count);
    protected bool CanPostDocument => Workspace is not null && CanAdd && CanPost && Lines.Any(x => x.IssueQty > 0m);
    protected IEnumerable<string> WorkCentreOptions => Workspace?.Operations.Select(x => x.WorkCentreCode).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x) ?? Enumerable.Empty<string>();
    protected IEnumerable<ProductionMaterialIssueOperation> OperationOptions => Workspace?.Operations.Where(x => string.IsNullOrWhiteSpace(SelectedWorkCentre) || x.WorkCentreCode == SelectedWorkCentre).OrderBy(x => x.ProcessSequence) ?? Enumerable.Empty<ProductionMaterialIssueOperation>();
    protected IEnumerable<MaterialLineVm> VisibleLines => Lines.Where(x => (!SelectedOperationId.HasValue || x.Material.WorkOrderOperationId == SelectedOperationId)
        && (string.IsNullOrWhiteSpace(SelectedWorkCentre) || x.Material.WorkCentreCode == SelectedWorkCentre)
        && (!ShowShortageOnly || x.Material.ShortageQty > 0m));

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{BatchNo}|{WorkOrderNo}";
        if (_loadedKey == key) return;
        _loadedKey = key; IsLoading = true; ErrorMessage = null;
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add);
        CanPost = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Post);
        if (IsViewMode) await LoadDocumentAsync();
        else
        {
            WorkOrderInput = WorkOrderNo ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(WorkOrderInput)) await LoadWorkspaceAsync(WorkOrderInput);
        }
        IsLoading = false;
    }

    protected Task LoadWorkspaceFromInputAsync() => LoadWorkspaceAsync(WorkOrderInput);
    private async Task LoadWorkspaceAsync(string workOrderNo)
    {
        ErrorMessage = null;
        if (!CanAdd) { ErrorMessage = "You do not have permission to create material issues."; return; }
        var result = await MaterialIssues.GetWorkspaceAsync(workOrderNo);
        if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to load Work Order."; return; }
        Workspace = result.Data; WorkOrderInput = result.Data.WorkOrderNo; IssueDate = result.Data.IssueDate.Date;
        Lines = result.Data.Materials.Select(x => new MaterialLineVm(x)).ToList();
        SelectedWorkCentre = string.Empty; SelectedOperationId = null; Remark = string.Empty; _postingRequestId = null;
    }
    private async Task LoadDocumentAsync()
    {
        var result = await MaterialIssues.GetAsync(BatchNo!.Value);
        if (!result.Succeeded || result.Data is null) ErrorMessage = result.Message ?? "Unable to load material issue.";
        else Document = result.Data;
    }
    protected void ClearWorkspace() { Workspace = null; Lines = []; WorkOrderInput = string.Empty; SelectedWorkCentre = string.Empty; SelectedOperationId = null; _postingRequestId = null; }
    protected void AutoFillOutstanding()
    {
        foreach (var line in VisibleLines.Where(x => x.Material.CanManualIssue)) { line.IssueQty = IvQty.Round(Math.Min(line.Material.OutstandingQty, line.Material.AvailableQty)); line.Allocations = []; }
    }
    protected async Task AllocateAllAsync()
    {
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            foreach (var line in Lines.Where(x => x.IssueQty > 0m && x.Material.CanManualIssue))
            {
                var result = await AllocationService.AutoAllocateAsync(new ProductionMaterialAllocationRequest { WorkOrderMaterialId = line.Material.WorkOrderMaterialId, IssueDate = IssueDate, RequestedQty = line.IssueQty });
                if (!result.Succeeded || result.Data is null || result.Data.ShortBaseQty > 0m) { ErrorMessage = result.Message ?? $"Unable to fully allocate {line.Material.ComponentCode}."; return; }
                line.Allocations = result.Data.Allocations.Select(x => new ProductionMaterialIssueAllocationRequest { FromBalLocId = x.FromBalLocId, BaseQty = x.SuggestedBaseQty }).ToList();
            }
        }
        finally { IsSubmitting = false; }
    }
    protected void OpenAllocation(MaterialLineVm line) { AllocationLine = line; AllocationVisible = true; }
    protected Task ApplyAllocations(IReadOnlyList<ProductionMaterialIssueAllocationRequest> rows) { if (AllocationLine is not null) AllocationLine.Allocations = rows.ToList(); return Task.CompletedTask; }
    protected void OpenPostConfirmation()
    {
        ErrorMessage = ValidatePosting();
        if (ErrorMessage is not null) return;
        PostWarnings = [];
        foreach (var line in Lines.Where(x => x.IssueQty > 0m))
        {
            if (line.IssueQty < line.Material.OutstandingQty) PostWarnings.Add($"{line.Material.ComponentCode} remains short by {(line.Material.OutstandingQty - line.IssueQty):n4} {line.Material.RequiredUom}.");
            if (line.Material.NetIssuedQty + line.IssueQty > line.Material.RequiredQty) PostWarnings.Add($"{line.Material.ComponentCode} exceeds standard requirement but remains within tolerance.");
        }
        PostConfirmationVisible = true;
    }
    private string? ValidatePosting()
    {
        if (Workspace is null) return "Load a Work Order first.";
        var selected = Lines.Where(x => x.IssueQty > 0m).ToList();
        if (selected.Count == 0) return "Enter an issue quantity for at least one material.";
        foreach (var line in selected)
        {
            if (!line.Material.CanManualIssue) return $"{line.Material.ComponentCode} cannot be issued manually.";
            if (line.Material.NetIssuedQty + line.IssueQty > line.Material.MaxAllowedNetIssue) return $"{line.Material.ComponentCode} exceeds its permitted tolerance.";
            var expected = IvQty.Round(line.IssueQty * line.Material.ConversionFactorToBase);
            if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expected) > 0.0001m) return $"Allocate exactly {expected:n4} {line.Material.BaseUom} for {line.Material.ComponentCode}.";
        }
        return null;
    }
    protected async Task PostAsync()
    {
        var validation = ValidatePosting(); if (validation is not null) { ErrorMessage = validation; PostConfirmationVisible = false; return; }
        _postingRequestId ??= Guid.NewGuid().ToString("N"); IsSubmitting = true; ErrorMessage = null;
        try
        {
            var result = await MaterialIssues.PostAsync(new ProductionMaterialIssuePostRequest
            {
                PostingRequestId = _postingRequestId, WorkOrderNo = Workspace!.WorkOrderNo, SnapshotRevision = Workspace.SnapshotRevision,
                SnapshotHash = Workspace.SnapshotHash, IssueDate = IssueDate, Remark = Remark,
                Lines = Lines.Where(x => x.IssueQty > 0m).Select(x => new ProductionMaterialIssueLineRequest { WorkOrderMaterialId = x.Material.WorkOrderMaterialId, IssueQty = IvQty.Round(x.IssueQty), Allocations = x.Allocations }).ToList()
            });
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to post material issue."; return; }
            PostConfirmationVisible = false; Navigation.NavigateTo($"/planning/material-issues/{result.Data.BatchNo}");
        }
        finally { IsSubmitting = false; }
    }
    protected void BackToList() => Navigation.NavigateTo("/planning/material-issues");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected sealed class MaterialLineVm
    {
        public MaterialLineVm(ProductionMaterialIssueMaterial material) => Material = material;
        public ProductionMaterialIssueMaterial Material { get; }
        public decimal IssueQty { get; set; }
        public List<ProductionMaterialIssueAllocationRequest> Allocations { get; set; } = [];
    }
}
