using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrMaterialIssueEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = string.Empty;
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
    private DateTime _issueDate;
    protected DateTime IssueDate
    {
        get => _issueDate;
        set
        {
            if (_issueDate == value) return;
            _issueDate = value;
            if (Workspace is null) return;
            foreach (var line in Lines) line.Allocations = [];
            PreviewStockDateValid = false;
        }
    }
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

    private decimal _desiredOutputInput;
    protected decimal DesiredOutputInput
    {
        get => _desiredOutputInput;
        set
        {
            if (_desiredOutputInput == value) return;
            _desiredOutputInput = value;
            if (AppliedProductionQtyThisIssue is > 0 && value != AppliedProductionQtyThisIssue.Value)
                DesiredOutputDirty = true;
        }
    }
    protected decimal? AppliedProductionQtyThisIssue { get; set; }
    protected bool DesiredOutputDirty { get; set; }
    protected bool PreviewStockDateValid { get; set; }

    // Mode is a route parameter so edit→view after save always reloads (BatchNo alone does not change).
    protected bool IsEditMode => BatchNo is > 0
        && string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => BatchNo is > 0
        && string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsNewMode => !IsEditMode && !IsViewMode;
    protected string PageHeading => IsViewMode ? "View Issue to Production" : IsEditMode ? "Edit Issue to Production" : "New Issue to Production";
    protected string ModeChip => IsViewMode ? "VIEW" : IsEditMode ? "EDIT" : "NEW";
    protected string HeaderStatus => IsViewMode ? Document?.Status ?? "View" : Workspace?.Status ?? "Draft";
    protected string? ActiveWorkOrderNo => IsViewMode ? Document?.WorkOrderNo : Workspace?.WorkOrderNo;
    protected string? ActiveProductCode => IsViewMode ? Document?.ProductCode : Workspace?.ProductCode;
    protected int SelectedLineCount => IsViewMode
        ? Document?.Lines.Select(x => x.WorkOrderMaterialId).Distinct().Count() ?? 0
        : SelectedIssueLines.Count();
    protected IEnumerable<MaterialLineVm> SelectedIssueLines => Lines.Where(x =>
        SelectedOperationId.HasValue
        && x.Material.WorkOrderOperationId == SelectedOperationId.Value
        && x.IssueQty > 0m);
    protected bool CanSaveDocument => !IsViewMode
        && Workspace is not null
        && (IsEditMode ? CanEdit : CanAdd)
        && AppliedProductionQtyThisIssue is > 0
        && !DesiredOutputDirty
        && PreviewStockDateValid
        && SelectedIssueLines.Any()
        && SelectedIssueLines.All(IsFullyAllocated);
    protected bool CanApplyDesiredOutput => Workspace is not null
        && SelectedOperationRow is not null
        && DesiredOutputInput > 0m
        && DesiredOutputInput <= SelectedOperationRow.RemainingBasisQty
        && !IsSubmitting;
    protected bool CanFillOutstanding => CanApplyDesiredOutput
        && AppliedProductionQtyThisIssue is > 0
        && !DesiredOutputDirty
        && PreviewStockDateValid;
    protected IEnumerable<MaterialLineVm> VisibleLines => Lines.Where(x =>
        (!SelectedOperationId.HasValue || x.Material.WorkOrderOperationId == SelectedOperationId)
        && (string.IsNullOrWhiteSpace(SelectedWorkCentre) || x.Material.WorkCentreCode == SelectedWorkCentre)
        && (!ShowShortageOnly || x.Material.ShortageQty > 0m));
    protected List<MaterialLineVm> VisibleLineList => VisibleLines.ToList();
    protected int OperationPageCount => Math.Max(1, (OperationTotalCount + OperationPageSize - 1) / OperationPageSize);
    protected string OperationResultLabel => OperationTotalCount == 1 ? "1 operation" : $"{OperationTotalCount:N0} operations";
    protected int UnallocatedLineCount => SelectedIssueLines.Count(x => !IsFullyAllocated(x));
    protected bool IsFullyAllocated(MaterialLineVm line) => Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty))
        - IvQty.Round(line.IssueQty * line.Material.ConversionFactorToBase)) <= 0.0001m;
    protected string AllocationHint(MaterialLineVm line) => IsFullyAllocated(line)
        ? $"Ready · {line.Allocations.Count}"
        : "Allocation needed";
    protected Dictionary<int, decimal> ReservedByOtherLines =>
        BuildReservationExcluding(AllocationLine?.Material.WorkOrderMaterialId);
    protected void ToggleMoreFilters() => MoreFiltersVisible = !MoreFiltersVisible;

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}|{BatchNo}|{WorkOrderNo}";
        if (_loadedKey == key) return;
        _loadedKey = key; IsLoading = true; ErrorMessage = null; StatusMessage = null;
        Workspace = null; Document = null; Lines = []; SelectedOperationRow = null;
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
        var refreshedOperation = result.Data.Operations.Single(x => x.WorkOrderOperationId == operation.WorkOrderOperationId);
        if (!refreshedOperation.IsSequenceEligible)
        {
            ErrorMessage = refreshedOperation.SequenceBlockingReason
                ?? "The selected operation is blocked by the Work Order execution sequence.";
            return;
        }

        SelectedOperationRow = ToOperationRow(result.Data, refreshedOperation);
        Workspace = result.Data; WorkOrderInput = result.Data.WorkOrderNo;
        _issueDate = result.Data.IssueDate; SelectedOperationId = operation.WorkOrderOperationId;
        SelectedWorkCentre = refreshedOperation.WorkCentreCode ?? string.Empty;
        Lines = result.Data.Materials.Select(x => new MaterialLineVm(x)).ToList();
        Remark = string.Empty;
        ResetDesiredOutputState(defaultDesired: refreshedOperation.RemainingBasisQty);
    }
    protected void OpenBom() { if (SelectedOperationRow is not null) BomVisible = true; }

    protected async Task ApplyDesiredOutputAndAllocateAsync()
    {
        if (SelectedOperationRow is null || Workspace is null) return;
        if (DesiredOutputInput <= 0m || DesiredOutputInput > SelectedOperationRow.RemainingBasisQty)
        {
            ErrorMessage = "Material Requirement Basis Qty must be greater than zero and not exceed the remaining operation planning basis.";
            return;
        }
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var preview = await MaterialIssues.GetBomPreviewAsync(
                SelectedOperationRow.WorkOrderOperationId,
                DesiredOutputInput,
                IssueDate,
                excludeBatchNo: IsEditMode ? BatchNo : null);
            if (!preview.Succeeded || preview.Data is null)
            {
                ErrorMessage = preview.Message ?? "Unable to calculate Desired Output.";
                return;
            }
            var apply = ToApplyResult(preview.Data);
            await ApplyDesiredOutputPreview(apply, overwriteIssueQty: true, autoAllocate: true);
        }
        finally { IsSubmitting = false; }
    }

    protected async Task ApplyBomResult(ProductionMaterialIssueBomApplyResult apply)
    {
        DesiredOutputInput = apply.ProductionQtyThisIssue;
        await ApplyDesiredOutputPreview(apply, overwriteIssueQty: true, autoAllocate: true);
    }

    private async Task ApplyDesiredOutputPreview(
        ProductionMaterialIssueBomApplyResult apply,
        bool overwriteIssueQty,
        bool autoAllocate)
    {
        foreach (var line in Lines.Where(x => !SelectedOperationId.HasValue || x.Material.WorkOrderOperationId == SelectedOperationId))
        {
            var row = apply.Lines.FirstOrDefault(x => x.WorkOrderMaterialId == line.Material.WorkOrderMaterialId);
            if (row is null) continue;
            line.BomRequestedQty = row.RequestedMaterialQty;
            line.MaxIssueQty = row.MaxIssueQty;
            line.AvailableForIssueDateQty = row.AvailableForIssueDateQty;
            if (overwriteIssueQty)
            {
                line.IssueQty = row.SuggestedIssueQty;
                line.Allocations = [];
            }
        }

        AppliedProductionQtyThisIssue = apply.ProductionQtyThisIssue;
        DesiredOutputDirty = false;
        PreviewStockDateValid = true;

        if (autoAllocate)
        {
            var ok = await AllocateSelectedAtomicallyAsync();
            if (!ok) return;
        }
        await Task.CompletedTask;
    }

    private async Task LoadWorkspaceAsync(string workOrderNo)
    {
        ErrorMessage = null;
        if (!CanAdd) { ErrorMessage = "You do not have permission to create material issues."; return; }
        var result = await MaterialIssues.GetWorkspaceAsync(workOrderNo);
        if (!result.Succeeded || result.Data is null) { ErrorMessage = result.Message ?? "Unable to load Work Order."; return; }
        Workspace = result.Data; WorkOrderInput = result.Data.WorkOrderNo; _issueDate = result.Data.IssueDate;
        Lines = result.Data.Materials.Select(x => new MaterialLineVm(x)).ToList();
        SelectedWorkCentre = string.Empty;
        var firstEligibleOperation = result.Data.Operations
            .Where(x => x.IsSequenceEligible)
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.ProcessSequence)
            .FirstOrDefault();
        if (firstEligibleOperation is null)
        {
            ErrorMessage = "No operation is currently eligible for material issue. Complete the required preceding operations first.";
            ResetDesiredOutputState(0m);
            return;
        }

        SelectedOperationId = firstEligibleOperation.WorkOrderOperationId;
        Remark = string.Empty; _postingRequestId = null;
        if (SelectedOperationId.HasValue)
        {
            SelectedOperationRow = ToOperationRow(result.Data, result.Data.Operations.Single(x => x.WorkOrderOperationId == SelectedOperationId.Value));
            ResetDesiredOutputState(defaultDesired: SelectedOperationRow.RemainingBasisQty);
        }
        else ResetDesiredOutputState(0m);
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
        var workspaceResult = await MaterialIssues.GetWorkspaceAsync(
            Document.WorkOrderNo, Document.WorkOrderOperationId, excludeBatchNo: BatchNo);
        if (!workspaceResult.Succeeded || workspaceResult.Data is null) { ErrorMessage = workspaceResult.Message ?? "Unable to load Work Order."; return; }
        Workspace = workspaceResult.Data; WorkOrderInput = Workspace.WorkOrderNo; _issueDate = Document.IssueDate; Remark = Document.Remark ?? string.Empty;
        SelectedOperationId = Document.WorkOrderOperationId;
        SelectedOperationRow = ToOperationRow(Workspace, Workspace.Operations.Single(x => x.WorkOrderOperationId == SelectedOperationId));
        Lines = Workspace.Materials.Select(x => new MaterialLineVm(x)).ToList();
        foreach (var group in Document.Lines.GroupBy(x => x.WorkOrderMaterialId))
        {
            var line = Lines.Single(x => x.Material.WorkOrderMaterialId == group.Key);
            line.IssueQty = IvQty.Round(group.Sum(x => x.IssueQty));
            line.OriginalIssueQty = line.IssueQty;
            line.Allocations = group.Select(x => new ProductionMaterialIssueAllocationRequest { FromBalLocId = x.FromBalLocId, BaseQty = x.BaseQty }).ToList();
            line.ExcessIssueReason = group.Select(x => x.ExcessIssueReason).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        if (Document.ProductionQtyThisIssue is > 0)
        {
            DesiredOutputInput = Document.ProductionQtyThisIssue.Value;
            AppliedProductionQtyThisIssue = Document.ProductionQtyThisIssue.Value;
            DesiredOutputDirty = false;
            var preview = await MaterialIssues.GetBomPreviewAsync(
                SelectedOperationRow.WorkOrderOperationId,
                Document.ProductionQtyThisIssue.Value,
                IssueDate,
                excludeBatchNo: BatchNo);
            if (preview.Succeeded && preview.Data is not null)
                await ApplyDesiredOutputPreview(ToApplyResult(preview.Data), overwriteIssueQty: false, autoAllocate: false);
            PreviewStockDateValid = SelectedIssueLines.All(IsFullyAllocated);
        }
        else
        {
            ResetDesiredOutputState(0m);
            ErrorMessage = "This draft has no Desired Output. Apply Desired Output and save before posting.";
        }
    }
    protected async Task ClearWorkspace()
    {
        Workspace = null; Lines = []; WorkOrderInput = string.Empty; SelectedWorkCentre = string.Empty;
        SelectedOperationId = null; SelectedOperationRow = null; _postingRequestId = null;
        ResetDesiredOutputState(0m);
        await LoadFilterOptionsAsync();
        OperationPage = 0;
        await SearchOperationsAsync();
    }

    protected async Task AutoFillOutstanding()
    {
        if (!CanFillOutstanding) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var preview = await MaterialIssues.GetBomPreviewAsync(
                SelectedOperationRow!.WorkOrderOperationId,
                AppliedProductionQtyThisIssue!.Value,
                IssueDate,
                excludeBatchNo: IsEditMode ? BatchNo : null);
            if (!preview.Succeeded || preview.Data is null)
            {
                ErrorMessage = preview.Message ?? "Unable to refresh stock for FILL OUTSTANDING.";
                return;
            }
            foreach (var line in VisibleLines.Where(x =>
                x.Material.CanManualIssue
                && SelectedOperationId.HasValue
                && x.Material.WorkOrderOperationId == SelectedOperationId.Value))
            {
                var row = preview.Data.Lines.FirstOrDefault(x => x.WorkOrderMaterialId == line.Material.WorkOrderMaterialId);
                var available = row?.AvailableForIssueDateQty ?? 0m;
                line.BomRequestedQty = row?.RequestedMaterialQty ?? line.BomRequestedQty;
                line.MaxIssueQty = row?.MaxIssueQty ?? line.MaxIssueQty;
                line.AvailableForIssueDateQty = available;
                var fill = IvQty.Round(Math.Min(line.Material.OutstandingQty, Math.Min(line.MaxIssueQty, available)));
                line.IssueQty = Math.Max(0m, fill);
                line.Allocations = [];
            }
            PreviewStockDateValid = false;
            var ok = await AllocateSelectedAtomicallyAsync();
            if (ok) PreviewStockDateValid = true;
        }
        finally { IsSubmitting = false; }
    }

    protected async Task AllocateAllAsync()
    {
        if (DesiredOutputDirty || AppliedProductionQtyThisIssue is not > 0)
        {
            ErrorMessage = "Apply Desired Output before allocating stock.";
            return;
        }
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var ok = await AllocateSelectedAtomicallyAsync();
            if (ok) PreviewStockDateValid = true;
        }
        finally { IsSubmitting = false; }
    }

    private async Task<bool> AllocateSelectedAtomicallyAsync()
    {
        var targets = Lines.Where(x =>
            SelectedOperationId.HasValue
            && x.Material.WorkOrderOperationId == SelectedOperationId.Value
            && x.IssueQty > 0m
            && x.Material.CanManualIssue).ToList();
        var reserved = new Dictionary<int, decimal>();
        var proposals = new Dictionary<long, List<ProductionMaterialIssueAllocationRequest>>();
        foreach (var line in targets)
        {
            var result = await AllocationService.AutoAllocateAsync(new ProductionMaterialAllocationRequest
            {
                WorkOrderMaterialId = line.Material.WorkOrderMaterialId,
                IssueDate = IssueDate,
                RequestedQty = line.IssueQty,
                ReservedBaseQtyByBalance = reserved,
                ExcludeInventoryBatchNo = IsEditMode ? BatchNo : null
            });
            if (!result.Succeeded || result.Data is null || result.Data.ShortBaseQty > 0m)
            {
                ErrorMessage = result.Message ?? $"Unable to fully allocate {line.Material.ComponentCode}.";
                return false;
            }
            var alloc = result.Data.Allocations
                .Select(x => new ProductionMaterialIssueAllocationRequest { FromBalLocId = x.FromBalLocId, BaseQty = x.SuggestedBaseQty })
                .ToList();
            proposals[line.Material.WorkOrderMaterialId] = alloc;
            foreach (var row in alloc)
                reserved[row.FromBalLocId] = IvQty.Round(reserved.GetValueOrDefault(row.FromBalLocId) + row.BaseQty);
        }
        foreach (var line in targets)
            line.Allocations = proposals[line.Material.WorkOrderMaterialId];
        return true;
    }

    protected void OpenAllocation(MaterialLineVm line) { AllocationLine = line; AllocationVisible = true; }
    protected Task ApplyAllocations(IReadOnlyList<ProductionMaterialIssueAllocationRequest> rows)
    {
        if (AllocationLine is not null) AllocationLine.Allocations = rows.ToList();
        PreviewStockDateValid = SelectedIssueLines.Any() && SelectedIssueLines.All(IsFullyAllocated);
        return Task.CompletedTask;
    }

    protected void OnIssueQtyEdited()
    {
        PreviewStockDateValid = SelectedIssueLines.Any() && SelectedIssueLines.All(IsFullyAllocated);
    }

    private string? ValidatePosting()
    {
        if (Workspace is null) return "Load a Work Order first.";
        if (!SelectedOperationId.HasValue) return "Select one operation for this material issue.";
        if (AppliedProductionQtyThisIssue is not > 0) return "Apply Desired Output before saving.";
        if (DesiredOutputDirty) return "Desired Output changed after Apply. Run APPLY & AUTO ALLOCATE again.";
        if (!PreviewStockDateValid) return "Issue date or Desired Output is stale. Re-apply and allocate stock.";
        var selected = SelectedIssueLines.ToList();
        if (selected.Count == 0) return "Enter an issue quantity for at least one material.";
        foreach (var line in selected)
        {
            if (!line.Material.CanManualIssue) return $"{line.Material.ComponentCode} cannot be issued manually.";
            if (line.MaxIssueQty > 0m && line.IssueQty > line.MaxIssueQty)
                return $"{line.Material.ComponentCode} exceeds the maximum allowed quantity for the selected desired output.";
            if (line.IssueQty > line.Material.AvailableToDraft + line.OriginalIssueQty)
                return $"{line.Material.ComponentCode} exceeds its available draft allowance.";
            if (line.IssueQty > line.BomRequestedQty && string.IsNullOrWhiteSpace(line.ExcessIssueReason))
                return $"{line.Material.ComponentCode} exceeds the BOM standard; enter an Excess Issue Reason.";
            var expected = IvQty.Round(line.IssueQty * line.Material.ConversionFactorToBase);
            if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expected) > 0.0001m)
                return $"Allocate exactly {expected:n4} {line.Material.BaseUom} for {line.Material.ComponentCode}.";
        }
        return null;
    }
    protected async Task SaveAsync()
    {
        if (IsSubmitting) return;
        if (IsViewMode)
        {
            ErrorMessage = "Open the draft in Edit mode to save changes.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            // Let DxSpinEdit commit the value from the Save click's focus change before we validate.
            await Task.Yield();

            if (AppliedProductionQtyThisIssue is > 0 && !DesiredOutputDirty
                && (!PreviewStockDateValid || SelectedIssueLines.Any(x => !IsFullyAllocated(x))))
            {
                var ok = await AllocateSelectedAtomicallyAsync();
                if (!ok) return;
                PreviewStockDateValid = true;
            }

            var validation = ValidatePosting();
            if (validation is not null)
            {
                ErrorMessage = validation;
                PostConfirmationVisible = false;
                return;
            }

            _postingRequestId ??= Guid.NewGuid().ToString("N");
            var request = new ProductionMaterialIssueSaveRequest
            {
                WorkOrderNo = Workspace!.WorkOrderNo, WorkOrderOperationId = SelectedOperationId!.Value,
                SnapshotRevision = Workspace.SnapshotRevision, SnapshotHash = Workspace.SnapshotHash,
                ProductionQtyThisIssue = AppliedProductionQtyThisIssue!.Value,
                TrxDateTime = IssueDate, RefNo = "AUTO", Remark = Remark,
                Lines = SelectedIssueLines
                    .Select(x => new ProductionMaterialIssueLineRequest
                    {
                        WorkOrderMaterialId = x.Material.WorkOrderMaterialId,
                        IssueQty = IvQty.Round(x.IssueQty),
                        ExcessIssueReason = string.IsNullOrWhiteSpace(x.ExcessIssueReason) ? null : x.ExcessIssueReason.Trim(),
                        Allocations = x.Allocations
                    }).ToList()
            };
            var result = BatchNo is > 0
                ? await MaterialIssues.UpdateAsync(BatchNo.Value, request)
                : await MaterialIssues.CreateAsync(request);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to save material issue draft.";
                return;
            }

            // Mode is a route parameter — changing edit→view always reloads summary once.
            Navigation.NavigateTo($"/planning/material-issues/view/{result.Data.BatchNo}", replace: true);
        }
        finally { IsSubmitting = false; }
    }
    protected void BackToList() => DocumentReturnNavigation.NavigateBack(Navigation, "/planning/material-issues");
    protected void OpenEdit() => Navigation.NavigateTo(
        DocumentReturnNavigation.PreserveReturnUrl(Navigation.Uri, $"/planning/material-issues/edit/{BatchNo!.Value}"));
    protected bool CanOpenEditFromView => IsViewMode
        && Document is not null
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && CanEdit;
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private void ResetDesiredOutputState(decimal defaultDesired)
    {
        _desiredOutputInput = defaultDesired;
        AppliedProductionQtyThisIssue = null;
        DesiredOutputDirty = false;
        PreviewStockDateValid = false;
    }

    private Dictionary<int, decimal> BuildReservationExcluding(long? excludeMaterialId)
    {
        var reserved = new Dictionary<int, decimal>();
        foreach (var line in Lines.Where(x =>
            SelectedOperationId.HasValue
            && x.Material.WorkOrderOperationId == SelectedOperationId.Value
            && x.Material.WorkOrderMaterialId != excludeMaterialId))
        {
            foreach (var alloc in line.Allocations)
                reserved[alloc.FromBalLocId] = IvQty.Round(reserved.GetValueOrDefault(alloc.FromBalLocId) + alloc.BaseQty);
        }
        return reserved;
    }

    private static ProductionMaterialIssueBomApplyResult ToApplyResult(ProductionMaterialIssueBomPreview preview) => new()
    {
        ProductionQtyThisIssue = preview.ProductionQtyThisIssue,
        Lines = preview.Lines.Select(x => new ProductionMaterialIssueBomApplyLine
        {
            WorkOrderMaterialId = x.WorkOrderMaterialId,
            RequestedMaterialQty = x.RequestedMaterialQty,
            SuggestedIssueQty = x.SuggestedIssueQty,
            MaxIssueQty = x.MaxIssueQty,
            AvailableForIssueDateQty = x.AvailableForIssueDateQty
        }).ToList()
    };

    protected sealed class MaterialLineVm
    {
        public MaterialLineVm(ProductionMaterialIssueMaterial material) => Material = material;
        public ProductionMaterialIssueMaterial Material { get; }
        public long Key => Material.WorkOrderMaterialId;
        public string ComponentCode => Material.ComponentCode;
        public decimal RequiredQty => Material.RequiredQty;
        public decimal NetIssuedQty => Material.NetIssuedQty;
        public decimal AvailableToDraft => Material.AvailableToDraft;
        public decimal OtherDraftReservedBaseQty => Material.OtherDraftReservedBaseQty;
        public decimal AvailableAfterDraftReservations => Material.AvailableAfterDraftReservations;
        public decimal BomRequestedQty { get; set; }
        public decimal MaxIssueQty { get; set; }
        public decimal AvailableForIssueDateQty { get; set; }
        public string? ExcessIssueReason { get; set; }
        public bool RequiresExcessReason => IssueQty > BomRequestedQty;
        private decimal _issueQty;
        public decimal IssueQty
        {
            get => _issueQty;
            set
            {
                if (_issueQty == value) return;
                _issueQty = value;
                RescaleOrClearAllocations();
            }
        }
        public decimal OriginalIssueQty { get; set; }
        public List<ProductionMaterialIssueAllocationRequest> Allocations { get; set; } = [];

        private void RescaleOrClearAllocations()
        {
            if (_issueQty <= 0m || Allocations.Count == 0)
            {
                Allocations = [];
                return;
            }

            var expectedBase = IvQty.Round(_issueQty * Material.ConversionFactorToBase);
            var currentBase = IvQty.Round(Allocations.Sum(x => x.BaseQty));
            if (currentBase <= 0m)
            {
                Allocations = [];
                return;
            }

            if (Math.Abs(currentBase - expectedBase) <= 0.0001m)
                return;

            // Keep existing lot/balance picks when the operator only changes qty.
            var factor = expectedBase / currentBase;
            var scaled = Allocations
                .Select(a => new ProductionMaterialIssueAllocationRequest
                {
                    FromBalLocId = a.FromBalLocId,
                    BaseQty = IvQty.Round(a.BaseQty * factor)
                })
                .Where(a => a.BaseQty > 0m)
                .ToList();
            if (scaled.Count == 0)
            {
                Allocations = [];
                return;
            }

            var sum = IvQty.Round(scaled.Sum(x => x.BaseQty));
            var drift = IvQty.Round(expectedBase - sum);
            if (drift != 0m)
            {
                var last = scaled[^1];
                var fixedQty = IvQty.Round(last.BaseQty + drift);
                if (fixedQty <= 0m)
                {
                    Allocations = [];
                    return;
                }
                scaled[^1] = new ProductionMaterialIssueAllocationRequest
                {
                    FromBalLocId = last.FromBalLocId,
                    BaseQty = fixedQty
                };
            }

            if (Math.Abs(IvQty.Round(scaled.Sum(x => x.BaseQty)) - expectedBase) > 0.0001m)
                Allocations = [];
            else
                Allocations = scaled;
        }
    }

    private static ProductionMaterialIssueOperationRow ToOperationRow(ProductionMaterialIssueWorkspace workspace, ProductionMaterialIssueOperation operation) => new()
    {
        WorkOrderOperationId = operation.WorkOrderOperationId, WorkOrderNo = workspace.WorkOrderNo,
        ProductCode = workspace.ProductCode, ProductDescription = workspace.ProductDescription,
        WorkCentreCode = operation.WorkCentreCode, OperationCode = operation.OperationCode,
        OperationDescription = operation.OperationDescription, OutputItemCode = workspace.ProductCode,
        PlannedOutputQty = operation.PlannedOutputQty, PlannedOutputUom = operation.PlannedOutputUom,
        StageSequence = operation.StageSequence, ProcessSequence = operation.ProcessSequence,
        IsSequenceEligible = operation.IsSequenceEligible, SequenceBlockingReason = operation.SequenceBlockingReason,
        ActualOutputQty = operation.ActualOutputQty, PostedBasisQty = operation.PostedBasisQty,
        OpenDraftBasisQty = operation.OpenDraftBasisQty, RemainingBasisQty = operation.RemainingBasisQty
    };
}
