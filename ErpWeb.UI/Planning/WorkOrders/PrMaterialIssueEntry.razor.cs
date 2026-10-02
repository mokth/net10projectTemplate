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
    protected bool BomVisible;
    protected bool PostConfirmationVisible;
    protected bool ShowShortageOnly;
    protected bool MoreFiltersVisible;
    protected bool CanAdd;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected string WorkOrderInput = string.Empty;
    protected string Remark = string.Empty;
    protected string SelectedWorkCentre = string.Empty;
    protected long? SelectedOperationId;
    protected DateTime IssueDate;
    protected ProductionMaterialIssueWorkspace? Workspace;
    protected ProductionMaterialIssueDocument? Document;
    protected List<ProductionMaterialIssueOperationRow> OperationRows { get; set; } = [];
    protected ProductionMaterialIssueFilterOptions FilterOptions { get; set; } = new();
    protected int OperationTotalCount;
    protected int OperationPage;
    protected const int OperationPageSize = 20;
    protected ProductionMaterialIssueOperationRow? SelectedOperationRow;
    protected string SearchWo = string.Empty;
    protected string SearchProduct = string.Empty;
    protected string SearchWorkCentre = string.Empty;
    protected string SearchProcess = string.Empty;
    protected string SearchOutputItem = string.Empty;
    protected string SearchRawMaterial = string.Empty;
    protected string SearchMachine = string.Empty;
    protected List<MaterialLineVm> Lines { get; set; } = [];
    protected MaterialLineVm? AllocationLine;
    protected List<string> PostWarnings { get; set; } = [];
    private string? _loadedKey;
    private string? _postingRequestId;

    protected bool IsEditMode => BatchNo is > 0 && Navigation.Uri.Contains("/edit/", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => BatchNo is > 0 && !IsEditMode;
    protected string PageHeading => IsViewMode ? "View Issue to Production" : IsEditMode ? "Edit Issue to Production" : "New Issue to Production";
    protected string ModeChip => IsViewMode ? "VIEW" : IsEditMode ? "EDIT" : "NEW";
    protected string HeaderStatus => IsViewMode ? Document?.Status ?? "View" : Workspace?.Status ?? "Draft";
    protected string? ActiveWorkOrderNo => IsViewMode ? Document?.WorkOrderNo : Workspace?.WorkOrderNo;
    protected string? ActiveProductCode => IsViewMode ? Document?.ProductCode : Workspace?.ProductCode;
    protected int SelectedLineCount => IsViewMode ? Document?.Lines.Select(x => x.WorkOrderMaterialId).Distinct().Count() ?? 0 : Lines.Count(x => x.IssueQty > 0m);
    protected int AllocationRowCount => Lines.Where(x => x.IssueQty > 0m).Sum(x => x.Allocations.Count);
    protected bool CanSaveDocument => Workspace is not null && (IsEditMode ? CanEdit : CanAdd) && Lines.Any(x => x.IssueQty > 0m);
    protected IEnumerable<string> WorkCentreOptions => Workspace?.Operations.Select(x => x.WorkCentreCode).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x) ?? Enumerable.Empty<string>();
    protected IEnumerable<ProductionMaterialIssueOperation> OperationOptions => Workspace?.Operations.Where(x => string.IsNullOrWhiteSpace(SelectedWorkCentre) || x.WorkCentreCode == SelectedWorkCentre).OrderBy(x => x.ProcessSequence) ?? Enumerable.Empty<ProductionMaterialIssueOperation>();
    protected IEnumerable<MaterialLineVm> VisibleLines => Lines.Where(x => (!SelectedOperationId.HasValue || x.Material.WorkOrderOperationId == SelectedOperationId)
        && (string.IsNullOrWhiteSpace(SelectedWorkCentre) || x.Material.WorkCentreCode == SelectedWorkCentre)
        && (!ShowShortageOnly || x.Material.ShortageQty > 0m));
    protected List<MaterialLineVm> VisibleLineList => VisibleLines.ToList();
    protected int OperationPageCount => Math.Max(1, (OperationTotalCount + OperationPageSize - 1) / OperationPageSize);
    protected string OperationResultLabel => OperationTotalCount == 1 ? "1 operation" : $"{OperationTotalCount:N0} operations";
    protected decimal TotalIssueQty => Lines.Where(x => x.IssueQty > 0m).Sum(x => x.IssueQty);
    protected int UnallocatedLineCount => Lines.Count(x => x.IssueQty > 0m && !IsFullyAllocated(x));
    protected bool IsFullyAllocated(MaterialLineVm line) => Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty))
        - IvQty.Round(line.IssueQty * line.Material.ConversionFactorToBase)) <= 0.0001m;
    protected string AllocationHint(MaterialLineVm line) => IsFullyAllocated(line)
        ? $"Ready · {line.Allocations.Count}"
        : "Allocation needed";
    protected void ToggleMoreFilters() => MoreFiltersVisible = !MoreFiltersVisible;

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{BatchNo}|{WorkOrderNo}";
        if (_loadedKey == key) return;
        _loadedKey = key; IsLoading = true; ErrorMessage = null;
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Edit);
        if (IsViewMode) await LoadDocumentAsync();
        else if (IsEditMode) await LoadEditAsync();
        else
        {
            WorkOrderInput = WorkOrderNo ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(WorkOrderInput)) await LoadWorkspaceAsync(WorkOrderInput);
            else { await LoadFilterOptionsAsync(); await SearchOperationsAsync(); }
        }
        IsLoading = false;
    }

    protected Task LoadWorkspaceFromInputAsync() => LoadWorkspaceAsync(WorkOrderInput);
    private async Task LoadFilterOptionsAsync()
    {
        var result = await MaterialIssues.GetEligibleOperationFilterOptionsAsync();
        if (result.Succeeded && result.Data is not null) FilterOptions = result.Data;
        else ErrorMessage = result.Message ?? "Unable to load operation choices.";
    }
    protected async Task SearchOperationsAsync()
    {
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var result = await MaterialIssues.SearchEligibleOperationsAsync(new ProductionMaterialIssueOperationQuery
            {
                WorkOrderNo = SearchWo, Product = SearchProduct, WorkCentre = SearchWorkCentre,
                Process = SearchProcess, OutputItem = SearchOutputItem, RawMaterial = SearchRawMaterial,
                Machine = SearchMachine, ExactMatch = true, Skip = OperationPage * OperationPageSize, Take = OperationPageSize
            });
            if (!result.Succeeded || result.Data is null) ErrorMessage = result.Message ?? "Unable to search eligible operations.";
            else { OperationRows = result.Data.Rows.ToList(); OperationTotalCount = result.Data.TotalCount; }
        }
        finally { IsSubmitting = false; }
    }
    protected async Task ApplyOperationFiltersAsync() { OperationPage = 0; await SearchOperationsAsync(); }
    protected async Task ChangeOperationPageAsync(int change)
    {
        OperationPage = Math.Clamp(OperationPage + change, 0, OperationPageCount - 1);
        await SearchOperationsAsync();
    }
    protected async Task ClearOperationFilters()
    {
        SearchWo = SearchProduct = SearchWorkCentre = SearchProcess = SearchOutputItem = SearchRawMaterial = SearchMachine = string.Empty;
        OperationPage = 0;
        await SearchOperationsAsync();
    }
    protected async Task ApplyOperationAsync(ProductionMaterialIssueOperationRow operation)
    {
        var result = await MaterialIssues.GetWorkspaceAsync(operation.WorkOrderNo, operation.WorkOrderOperationId);
        if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to load operation."; return; }
        SelectedOperationRow = operation; Workspace = result.Data; WorkOrderInput = result.Data.WorkOrderNo;
        IssueDate = result.Data.IssueDate; SelectedOperationId = operation.WorkOrderOperationId;
        SelectedWorkCentre = operation.WorkCentreCode ?? string.Empty; Lines = result.Data.Materials.Select(x => new MaterialLineVm(x)).ToList();
        Remark = string.Empty;
    }
    protected void OpenBom() { if (SelectedOperationRow is not null) BomVisible = true; }
    protected Task ApplyBomSuggestions(IReadOnlyDictionary<long, decimal> suggestions)
    {
        foreach (var line in Lines)
        {
            line.IssueQty = suggestions.GetValueOrDefault(line.Material.WorkOrderMaterialId);
            line.Allocations = [];
        }
        return Task.CompletedTask;
    }
    private async Task LoadWorkspaceAsync(string workOrderNo)
    {
        ErrorMessage = null;
        if (!CanAdd) { ErrorMessage = "You do not have permission to create material issues."; return; }
        var result = await MaterialIssues.GetWorkspaceAsync(workOrderNo);
        if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to load Work Order."; return; }
        Workspace = result.Data; WorkOrderInput = result.Data.WorkOrderNo; IssueDate = result.Data.IssueDate;
        Lines = result.Data.Materials.Select(x => new MaterialLineVm(x)).ToList();
        SelectedWorkCentre = string.Empty; SelectedOperationId = result.Data.Operations.OrderBy(x => x.ProcessSequence).Select(x => (long?)x.WorkOrderOperationId).FirstOrDefault(); Remark = string.Empty; _postingRequestId = null;
        if (SelectedOperationId.HasValue) SelectedOperationRow = ToOperationRow(result.Data, result.Data.Operations.Single(x => x.WorkOrderOperationId == SelectedOperationId.Value));
    }
    private async Task LoadDocumentAsync()
    {
        var result = await MaterialIssues.GetAsync(BatchNo!.Value);
        if (!result.Succeeded || result.Data is null) ErrorMessage = result.Message ?? "Unable to load material issue.";
        else Document = result.Data;
    }
    private async Task LoadEditAsync()
    {
        if (!CanEdit) { ErrorMessage = "You do not have permission to edit material issues."; return; }
        var documentResult = await MaterialIssues.GetAsync(BatchNo!.Value);
        if (!documentResult.Succeeded || documentResult.Data is null) { ErrorMessage = documentResult.Message ?? "Unable to load draft."; return; }
        if (documentResult.Data.Status != "NEW") { ErrorMessage = "Only NEW drafts can be edited."; return; }
        Document = documentResult.Data;
        var workspaceResult = await MaterialIssues.GetWorkspaceAsync(Document.WorkOrderNo, Document.WorkOrderOperationId);
        if (!workspaceResult.Succeeded || workspaceResult.Data is null) { ErrorMessage = workspaceResult.Message ?? "Unable to load Work Order."; return; }
        Workspace = workspaceResult.Data; WorkOrderInput = Workspace.WorkOrderNo; IssueDate = Document.IssueDate; Remark = Document.Remark ?? string.Empty;
        SelectedOperationId = Document.WorkOrderOperationId;
        SelectedOperationRow = ToOperationRow(Workspace, Workspace.Operations.Single(x => x.WorkOrderOperationId == SelectedOperationId));
        Lines = Workspace.Materials.Select(x => new MaterialLineVm(x)).ToList();
        foreach (var group in Document.Lines.GroupBy(x => x.WorkOrderMaterialId))
        {
            var line = Lines.Single(x => x.Material.WorkOrderMaterialId == group.Key);
            line.IssueQty = IvQty.Round(group.Sum(x => x.IssueQty));
            line.OriginalIssueQty = line.IssueQty;
            line.Allocations = group.Select(x => new ProductionMaterialIssueAllocationRequest { FromBalLocId = x.FromBalLocId, BaseQty = x.BaseQty }).ToList();
        }
    }
    protected async Task ClearWorkspace()
    {
        Workspace = null; Lines = []; WorkOrderInput = string.Empty; SelectedWorkCentre = string.Empty;
        SelectedOperationId = null; SelectedOperationRow = null; _postingRequestId = null;
        await LoadFilterOptionsAsync();
        OperationPage = 0;
        await SearchOperationsAsync();
    }
    protected void AutoFillOutstanding()
    {
        foreach (var line in VisibleLines.Where(x => x.Material.CanManualIssue)) { line.IssueQty = IvQty.Round(Math.Min(line.Material.OutstandingQty, Math.Min(line.Material.AvailableToDraft + line.OriginalIssueQty, line.Material.AvailableQty))); line.Allocations = []; }
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
        if (!SelectedOperationId.HasValue) return "Select one operation for this material issue.";
        var selected = Lines.Where(x => x.IssueQty > 0m && x.Material.WorkOrderOperationId == SelectedOperationId.Value).ToList();
        if (selected.Count == 0) return "Enter an issue quantity for at least one material.";
        foreach (var line in selected)
        {
            if (!line.Material.CanManualIssue) return $"{line.Material.ComponentCode} cannot be issued manually.";
            if (line.IssueQty > line.Material.AvailableToDraft + line.OriginalIssueQty) return $"{line.Material.ComponentCode} exceeds its available draft allowance.";
            var expected = IvQty.Round(line.IssueQty * line.Material.ConversionFactorToBase);
            if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expected) > 0.0001m) return $"Allocate exactly {expected:n4} {line.Material.BaseUom} for {line.Material.ComponentCode}.";
        }
        return null;
    }
    protected async Task SaveAsync()
    {
        var validation = ValidatePosting(); if (validation is not null) { ErrorMessage = validation; PostConfirmationVisible = false; return; }
        _postingRequestId ??= Guid.NewGuid().ToString("N"); IsSubmitting = true; ErrorMessage = null;
        try
        {
            var request = new ProductionMaterialIssueSaveRequest
            {
                WorkOrderNo = Workspace!.WorkOrderNo, WorkOrderOperationId = SelectedOperationId!.Value,
                SnapshotRevision = Workspace.SnapshotRevision, SnapshotHash = Workspace.SnapshotHash,
                TrxDateTime = IssueDate, RefNo = "AUTO", Remark = Remark,
                Lines = Lines.Where(x => x.IssueQty > 0m && x.Material.WorkOrderOperationId == SelectedOperationId.Value)
                    .Select(x => new ProductionMaterialIssueLineRequest { WorkOrderMaterialId = x.Material.WorkOrderMaterialId, IssueQty = IvQty.Round(x.IssueQty), Allocations = x.Allocations }).ToList()
            };
            var result = IsEditMode
                ? await MaterialIssues.UpdateAsync(BatchNo!.Value, request)
                : await MaterialIssues.CreateAsync(request);
            if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to save material issue draft."; return; }
            Navigation.NavigateTo($"/planning/material-issues/view/{result.Data.BatchNo}");
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
        public long Key => Material.WorkOrderMaterialId;
        public string ComponentCode => Material.ComponentCode;
        public decimal RequiredQty => Material.RequiredQty;
        public decimal NetIssuedQty => Material.NetIssuedQty;
        public decimal AvailableToDraft => Material.AvailableToDraft;
        private decimal _issueQty;
        public decimal IssueQty { get => _issueQty; set { if (_issueQty != value) Allocations = []; _issueQty = value; } }
        public decimal OriginalIssueQty { get; set; }
        public List<ProductionMaterialIssueAllocationRequest> Allocations { get; set; } = [];
    }

    private static ProductionMaterialIssueOperationRow ToOperationRow(ProductionMaterialIssueWorkspace workspace, ProductionMaterialIssueOperation operation) => new()
    {
        WorkOrderOperationId = operation.WorkOrderOperationId, WorkOrderNo = workspace.WorkOrderNo,
        ProductCode = workspace.ProductCode, ProductDescription = workspace.ProductDescription,
        WorkCentreCode = operation.WorkCentreCode, OperationCode = operation.OperationCode,
        OperationDescription = operation.OperationDescription, OutputItemCode = workspace.ProductCode,
        PlannedOutputQty = operation.PlannedOutputQty, PlannedOutputUom = operation.PlannedOutputUom
    };
}
