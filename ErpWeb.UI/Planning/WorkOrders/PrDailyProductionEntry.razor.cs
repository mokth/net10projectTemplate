using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrDailyProductionEntry : PageBase
{
    [Inject] private IProductionOutputService Outputs { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public long? OutputId { get; set; }
    [Parameter] public long? WorkOrderOperationId { get; set; }

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool MoreFiltersVisible;
    protected bool PostConfirmationVisible;
    protected bool DeleteConfirmationVisible;
    protected string? StatusMessage;

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(Mode) && OutputId is null);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected string ModeChip => IsViewMode ? "VIEW" : IsEditMode ? "EDIT" : "NEW";
    protected string PageHeading => IsNewMode ? "New Daily Production" : IsEditMode ? "Edit Daily Production" : "View Daily Production";

    protected ProductionOutputDetail? Document;
    protected ProductionOutputWorkspace? Workspace;
    protected List<MaterialLineEdit> MaterialEdits { get; set; } = [];
    protected IReadOnlyList<ProductionOutputChoice> VarianceReasonChoices { get; } =
        ProductionMaterialVarianceReasonCodes.UserSelectable
            .Select(code => new ProductionOutputChoice(code, code))
            .ToList();
    protected ProductionOutputEntryLookups Lookups { get; set; } = new();
    protected ProductionEligibleOperationFilterOptions FilterOptions { get; set; } = new();
    protected List<ProductionEligibleOperationRow> OperationRows { get; set; } = [];
    protected int OperationTotalCount;
    protected int OperationPage;
    protected const int OperationPageSize = 20;

    protected long? SelectedOperationId;
    protected long? CurrentOutputId;
    protected string DocumentNo = string.Empty;
    protected string WorkOrderNo = string.Empty;
    protected string HeaderStatus = ProductionOutputStatuses.New;
    protected string OutputItemCode = string.Empty;
    protected string OutputUom = string.Empty;
    protected string OutputType = string.Empty;
    protected string PostingRequestId = string.Empty;
    protected byte[] RowVersion = [];

    protected DateTime ProductionDate = DateTime.Now;
    protected string? ShiftCode;
    protected string? ActualMachineCode;
    protected string? OperatorCode;
    protected string OutputLotNo = string.Empty;
    protected decimal GoodQty;
    protected decimal ScrapQty;
    protected decimal RejectQty;
    protected decimal HoldQty;

    protected string SearchWo = string.Empty;
    protected string SearchProduct = string.Empty;
    protected string SearchWorkCentre = string.Empty;
    protected string SearchProcess = string.Empty;
    protected string SearchOutputItem = string.Empty;
    protected string SearchRawMaterial = string.Empty;
    protected string SearchMachine = string.Empty;

    protected bool CanAccessAdd;
    protected bool CanAccessEdit;
    protected bool CanAccessPost;
    protected bool CanAccessRollback;
    protected bool CanAccessDelete;

    protected bool CanEditFields => (IsNewMode || IsEditMode)
        && HeaderStatus == ProductionOutputStatuses.New;
    protected bool CanSave => CanEditFields
        && SelectedOperationId is > 0
        && (IsNewMode ? CanAccessAdd : CanAccessEdit);
    protected bool CanPost => CurrentOutputId is > 0
        && HeaderStatus == ProductionOutputStatuses.New
        && CanAccessPost;
    protected bool CanRollback => CurrentOutputId is > 0
        && HeaderStatus == ProductionOutputStatuses.Posted
        && CanAccessRollback;
    protected bool CanDelete => CurrentOutputId is > 0
        && HeaderStatus == ProductionOutputStatuses.New
        && CanAccessDelete
        && !IsNewMode;
    protected bool CanOpenEditFromView => IsViewMode
        && HeaderStatus == ProductionOutputStatuses.New
        && CanAccessEdit
        && CurrentOutputId is > 0;

    protected bool IsSelectingOperation => IsNewMode && Workspace is null;
    protected int OperationPageCount => Math.Max(1, (OperationTotalCount + OperationPageSize - 1) / OperationPageSize);
    protected string OperationResultLabel => OperationTotalCount == 1
        ? "1 operation"
        : $"{OperationTotalCount:N0} operations";
    protected string ModeStatus => Workspace?.Operation.WorkOrderStatus ?? HeaderStatus;
    protected string SelectedProductDescription => Workspace?.Operation.ProductDescription ?? Document?.ProductDescription ?? string.Empty;
    // A saved document owns the historical planned-machine snapshot. Do not infer a value from
    // the current Work Order operation when an older document has no saved PlannedMachineCode.
    protected string PlannedMachineCode => Document is not null
        ? Document.PlannedMachineCode ?? string.Empty
        : Workspace?.Operation.SelectedMachineCode ?? string.Empty;
    protected string PlannedMachineDescription => Document is not null
        ? Document.PlannedMachineDescription ?? string.Empty
        : Workspace?.Operation.SelectedMachineDescription ?? string.Empty;

    protected string RollbackReason = string.Empty;
    protected bool RollbackConfirmVisible;
    private string? _loadedKey;

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}|{OutputId}|{WorkOrderOperationId}";
        if (_loadedKey == key)
            return;

        _loadedKey = key;
        IsBootstrapping = true;
        ErrorMessage = null;
        StatusMessage = null;

        CanAccessAdd = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Add);
        CanAccessEdit = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Edit);
        CanAccessPost = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Post);
        CanAccessRollback = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Rollback);
        CanAccessDelete = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Delete);

        Document = null;
        Workspace = null;
        Lookups = new();
        OperationRows = [];
        OperationTotalCount = 0;
        OperationPage = 0;
        SelectedOperationId = null;
        CurrentOutputId = null;
        ResetFormFields();

        if (OutputId is > 0)
        {
            CurrentOutputId = OutputId;
            await LoadDocumentAsync(OutputId.Value);
        }
        else if (WorkOrderOperationId is > 0)
        {
            await SelectOperationAsync(WorkOrderOperationId.Value);
        }
        else if (IsNewMode)
        {
            await LoadFilterOptionsAsync();
            await SearchEligibleAsync();
        }

        IsBootstrapping = false;
    }

    protected async Task LoadFilterOptionsAsync()
    {
        var result = await Outputs.GetEligibleOperationFilterOptionsAsync();
        if (result.Succeeded && result.Data is not null)
        {
            FilterOptions = result.Data;
            return;
        }

        ErrorMessage = result.Message ?? "Unable to load Daily Production filters.";
        FilterOptions = new();
    }

    protected async Task SearchEligibleAsync()
    {
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Outputs.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
            {
                WorkOrderNo = NullIfEmpty(SearchWo),
                ProductCode = NullIfEmpty(SearchProduct),
                WorkCentreCode = NullIfEmpty(SearchWorkCentre),
                OperationCode = NullIfEmpty(SearchProcess),
                OutputItemCode = NullIfEmpty(SearchOutputItem),
                RawMaterialCode = NullIfEmpty(SearchRawMaterial),
                MachineCode = NullIfEmpty(SearchMachine),
                ExactMatch = true,
                Skip = OperationPage * OperationPageSize,
                Take = OperationPageSize,
            });
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to load eligible operations.";
                OperationRows = [];
                OperationTotalCount = 0;
                return;
            }

            OperationRows = result.Data.Rows.ToList();
            OperationTotalCount = result.Data.TotalCount;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ApplyOperationFiltersAsync()
    {
        OperationPage = 0;
        await SearchEligibleAsync();
    }

    protected async Task ChangeOperationPageAsync(int change)
    {
        OperationPage = Math.Clamp(OperationPage + change, 0, OperationPageCount - 1);
        await SearchEligibleAsync();
    }

    protected async Task ClearOperationFilters()
    {
        SearchWo = SearchProduct = SearchWorkCentre = SearchProcess =
            SearchOutputItem = SearchRawMaterial = SearchMachine = string.Empty;
        OperationPage = 0;
        await SearchEligibleAsync();
    }

    protected async Task ApplyOperationAsync(ProductionEligibleOperationRow operation) =>
        await SelectOperationAsync(operation.WorkOrderOperationId);

    protected async Task SelectOperationAsync(long operationId)
    {
        ErrorMessage = null;
        ProductionDate = DateTime.Now;
        var workspace = await Outputs.GetWorkspaceAsync(operationId, ProductionDate);
        if (!workspace.Succeeded || workspace.Data is null)
        {
            ErrorMessage = workspace.Message ?? "Unable to load operation workspace.";
            return;
        }

        SelectedOperationId = operationId;
        Workspace = workspace.Data;
        Document = null;
        CurrentOutputId = null;
        DocumentNo = string.Empty;
        PostingRequestId = Guid.NewGuid().ToString("N");
        WorkOrderNo = workspace.Data.Operation.WorkOrderNo;
        OutputItemCode = workspace.Data.Operation.OutputItemCode;
        OutputUom = workspace.Data.Operation.OutputUom ?? string.Empty;
        OutputType = workspace.Data.Operation.OutputType ?? string.Empty;
        HeaderStatus = ProductionOutputStatuses.New;
        ProductionDate = DateTime.Now;
        ShiftCode = null;
        ActualMachineCode = null;
        OperatorCode = null;
        OutputLotNo = string.Empty;
        GoodQty = ScrapQty = RejectQty = HoldQty = 0m;
        BindMaterialEdits(workspace.Data.Materials, preserveOverrides: false);
        RecalcMaterialStandards();
        await LoadEntryLookupsAsync(operationId, workspace.Data.Operation.SelectedMachineCode);
    }

    private async Task LoadEntryLookupsAsync(long operationId, string? defaultActualMachine = null)
    {
        var result = await Outputs.GetEntryLookupsAsync(operationId);
        if (!result.Succeeded || result.Data is null)
        {
            Lookups = new();
            ErrorMessage ??= result.Message ?? "Unable to load Daily Production entry choices.";
            return;
        }

        Lookups = result.Data;
        if (string.IsNullOrWhiteSpace(ActualMachineCode)
            && !string.IsNullOrWhiteSpace(defaultActualMachine)
            && Lookups.Machines.Any(x => string.Equals(x.Code, defaultActualMachine, StringComparison.OrdinalIgnoreCase)))
        {
            ActualMachineCode = defaultActualMachine;
        }
    }

    private async Task LoadDocumentAsync(long outputId)
    {
        var result = await Outputs.GetAsync(outputId);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Document not found.";
            return;
        }

        var d = result.Data;
        Document = d;
        CurrentOutputId = d.Uid;
        DocumentNo = d.DocumentNo;
        WorkOrderNo = d.WorkOrderNo;
        HeaderStatus = d.Status;
        SelectedOperationId = d.WorkOrderOperationId;
        ProductionDate = d.ProductionDate;
        ShiftCode = d.ShiftCode;
        ActualMachineCode = d.ActualMachineCode;
        OperatorCode = d.OperatorCode;
        GoodQty = d.GoodQty;
        ScrapQty = d.ScrapQty;
        RejectQty = d.RejectQty;
        HoldQty = d.HoldQty;
        OutputLotNo = d.OutputLotNo;
        OutputItemCode = d.OutputItemCode;
        OutputUom = d.OutputUom;
        OutputType = d.OutputType ?? string.Empty;
        PostingRequestId = d.PostingRequestId;
        RowVersion = d.RowVersion;

        var workspace = await Outputs.GetDocumentWorkspaceAsync(outputId);
        if (workspace.Succeeded && workspace.Data is not null)
        {
            Workspace = workspace.Data;
            BindMaterialEdits(workspace.Data.Materials, preserveOverrides: true);
            await LoadEntryLookupsAsync(d.WorkOrderOperationId);
            AddHistoricalChoiceValues();
        }
        else if (!IsViewMode)
        {
            ErrorMessage = workspace.Message ?? "Unable to load the saved operation workspace.";
        }
    }

    private void AddHistoricalChoiceValues()
    {
        Lookups = new ProductionOutputEntryLookups
        {
            Shifts = AddHistoricalChoice(Lookups.Shifts, ShiftCode),
            Machines = AddHistoricalChoice(Lookups.Machines, ActualMachineCode),
            Operators = AddHistoricalChoice(Lookups.Operators, OperatorCode),
        };
    }

    private static IReadOnlyList<ProductionOutputChoice> AddHistoricalChoice(
        IReadOnlyList<ProductionOutputChoice> choices, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)
            || choices.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            return choices;

        return choices
            .Append(new ProductionOutputChoice(code, $"{code} (historical)"))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected async Task SaveAsync()
    {
        if (IsSubmitting || !CanSave || SelectedOperationId is not > 0)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            if (CurrentOutputId is null or <= 0)
            {
                var created = await Outputs.CreateAsync(new ProductionOutputCreateRequest
                {
                    WorkOrderOperationId = SelectedOperationId.Value,
                    PostingRequestId = PostingRequestId,
                    ProductionDate = ProductionDate,
                    ShiftCode = ShiftCode,
                    ActualMachineCode = ActualMachineCode,
                    OperatorCode = OperatorCode,
                    GoodQty = GoodQty,
                    ScrapQty = ScrapQty,
                    RejectQty = RejectQty,
                    HoldQty = HoldQty,
                    OutputLotNo = OutputLotNo,
                    Materials = BuildMaterialInputs(),
                });
                if (!created.Succeeded || created.Data is null)
                {
                    ErrorMessage = created.Message ?? "Unable to save Daily Production.";
                    return;
                }

                StatusMessage = $"Saved {created.Data.DocumentNo}.";
                Navigation.NavigateTo($"/planning/daily-production/view/{created.Data.Uid}", replace: true);
                return;
            }

            var updated = await Outputs.UpdateAsync(new ProductionOutputUpdateRequest
            {
                OutputId = CurrentOutputId.Value,
                WorkOrderOperationId = SelectedOperationId.Value,
                PostingRequestId = PostingRequestId,
                ProductionDate = ProductionDate,
                ShiftCode = ShiftCode,
                ActualMachineCode = ActualMachineCode,
                OperatorCode = OperatorCode,
                GoodQty = GoodQty,
                ScrapQty = ScrapQty,
                RejectQty = RejectQty,
                HoldQty = HoldQty,
                OutputLotNo = OutputLotNo,
                RowVersion = RowVersion,
                Materials = BuildMaterialInputs(),
            });
            if (!updated.Succeeded || updated.Data is null)
            {
                ErrorMessage = updated.Message ?? "Unable to update Daily Production.";
                return;
            }

            Navigation.NavigateTo($"/planning/daily-production/view/{updated.Data.Uid}", replace: true);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenPostConfirm()
    {
        if (CanPost)
            PostConfirmationVisible = true;
    }

    protected async Task PostAsync()
    {
        if (IsSubmitting || !CanPost)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Outputs.PostAsync(CurrentOutputId!.Value);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to post Daily Production.";
                return;
            }

            PostConfirmationVisible = false;
            await LoadDocumentAsync(result.Data.Uid);
            StatusMessage = $"Posted {DocumentNo}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenDeleteConfirm()
    {
        if (CanDelete)
            DeleteConfirmationVisible = true;
    }

    protected async Task DeleteAsync()
    {
        if (IsSubmitting || !CanDelete)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Outputs.DeleteAsync(CurrentOutputId!.Value);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message ?? "Unable to delete Daily Production.";
                return;
            }

            DeleteConfirmationVisible = false;
            Navigation.NavigateTo("/planning/daily-production");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenRollbackConfirm()
    {
        if (!CanRollback)
            return;
        RollbackReason = string.Empty;
        RollbackConfirmVisible = true;
    }

    protected async Task RollbackAsync()
    {
        if (IsSubmitting || !CanRollback)
            return;
        if (string.IsNullOrWhiteSpace(RollbackReason))
        {
            ErrorMessage = "Rollback reason is required.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Outputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = CurrentOutputId!.Value,
                PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = RollbackReason.Trim(),
            });
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to roll back Daily Production.";
                return;
            }

            RollbackConfirmVisible = false;
            await LoadDocumentAsync(result.Data.Uid);
            StatusMessage = $"Rolled back {DocumentNo}.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ClearWorkspace()
    {
        if (IsSubmitting || !IsNewMode)
            return;

        Workspace = null;
        SelectedOperationId = null;
        MaterialEdits = [];
        Lookups = new();
        ResetFormFields();
        await LoadFilterOptionsAsync();
        OperationPage = 0;
        await SearchEligibleAsync();
    }

    protected void BackToList() => Navigation.NavigateTo("/planning/daily-production");
    protected void OpenEdit() => Navigation.NavigateTo($"/planning/daily-production/edit/{CurrentOutputId}");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private void ResetFormFields()
    {
        DocumentNo = WorkOrderNo = OutputItemCode = OutputUom = OutputType = string.Empty;
        PostingRequestId = Guid.NewGuid().ToString("N");
        ShiftCode = ActualMachineCode = OperatorCode = null;
        OutputLotNo = string.Empty;
        GoodQty = ScrapQty = RejectQty = HoldQty = 0m;
        ProductionDate = DateTime.Now;
        HeaderStatus = ProductionOutputStatuses.New;
        RowVersion = [];
        MaterialEdits = [];
    }

    protected Task OnProductionDateChanged(DateTime value)
    {
        ProductionDate = value;
        return CanEditFields && SelectedOperationId is > 0
            ? RefreshAvailabilityAsync()
            : Task.CompletedTask;
    }

    protected void OnOutputQtyChanged(decimal good, decimal scrap, decimal reject, decimal hold)
    {
        GoodQty = good;
        ScrapQty = scrap;
        RejectQty = reject;
        HoldQty = hold;
        RecalcMaterialStandards();
    }

    protected void OnConsumeChanged(MaterialLineEdit line, decimal consume)
    {
        if (!line.IsConsumeEditable)
            return;
        line.IsConsumeOverridden = true;
        line.ConsumeQty = consume;
        ApplyLineDerived(line);
    }

    protected void OnReasonChanged(MaterialLineEdit line, string? code)
    {
        line.VarianceReasonCode = code;
        if (!string.Equals(code, ProductionMaterialVarianceReasonCodes.Other, StringComparison.Ordinal))
            line.VarianceReasonText = null;
        ApplyLineDerived(line);
    }

    protected void ResetConsumesToStandard()
    {
        foreach (var line in MaterialEdits.Where(x => x.IsConsumeEditable))
        {
            line.IsConsumeOverridden = false;
            line.ConsumeQty = line.StandardQty;
            line.VarianceReasonCode = null;
            line.VarianceReasonText = null;
            ApplyLineDerived(line);
        }
        MaterialEdits = MaterialEdits.ToList();
    }

    private async Task RefreshAvailabilityAsync()
    {
        if (SelectedOperationId is not > 0)
            return;

        var workspace = await Outputs.GetWorkspaceAsync(SelectedOperationId.Value, ProductionDate);
        if (!workspace.Succeeded || workspace.Data is null)
        {
            ErrorMessage = workspace.Message ?? "Unable to refresh material availability.";
            return;
        }

        Workspace = workspace.Data;
        BindMaterialEdits(workspace.Data.Materials, preserveOverrides: true);
        RecalcMaterialStandards();
    }

    private void BindMaterialEdits(IReadOnlyList<ProductionOutputMaterialLine> lines, bool preserveOverrides)
    {
        var previous = MaterialEdits.ToDictionary(x => x.WorkOrderMaterialId);
        MaterialEdits = lines.Select(line =>
        {
            previous.TryGetValue(line.WorkOrderMaterialId, out var existing);
            var consume = preserveOverrides && existing is { IsConsumeOverridden: true }
                ? existing.ConsumeQty
                : line.ConsumeQty;
            var reasonCode = preserveOverrides && existing is { IsConsumeOverridden: true }
                ? existing.VarianceReasonCode
                : line.VarianceReasonCode;
            var reasonText = preserveOverrides && existing is { IsConsumeOverridden: true }
                ? existing.VarianceReasonText
                : line.VarianceReasonText;
            var edit = new MaterialLineEdit
            {
                WorkOrderMaterialId = line.WorkOrderMaterialId,
                ComponentCode = line.ComponentCode,
                Description = line.Description,
                SupplySource = line.SupplySource,
                IssueMethod = line.IssueMethod,
                RequiredUom = line.RequiredUom,
                WoBomRequiredQty = line.WoBomRequiredQty,
                TolerancePercent = line.TolerancePercent,
                StandardQty = line.StandardQty,
                ConsumeQty = consume,
                MaxConsumeQty = line.MaxConsumeQty,
                AvailableQty = line.AvailableQty,
                VarianceReasonCode = reasonCode,
                VarianceReasonText = reasonText,
                BlockingReason = line.BlockingReason,
                IsHandoff = line.IsHandoff,
                HandoffFromOperationId = line.HandoffFromOperationId,
                IsConsumeEditable = line.IsConsumeEditable,
                IsConsumeOverridden = preserveOverrides && existing is { IsConsumeOverridden: true },
            };
            ApplyLineDerived(edit);
            if (!edit.IsHandoff && IvQty.Round(edit.ConsumeQty) != IvQty.Round(edit.StandardQty))
                edit.IsConsumeOverridden = true;
            return edit;
        }).ToList();
    }

    private void RecalcMaterialStandards()
    {
        if (Workspace is null)
            return;

        var processed = ProductionMaterialExecutionCalc.ProcessedThisPost(GoodQty, ScrapQty, RejectQty, HoldQty);
        var planned = Workspace.Operation.PlannedOutputQty;
        foreach (var line in MaterialEdits)
        {
            if (line.IsHandoff)
            {
                line.StandardQty = processed;
                line.MaxConsumeQty = processed;
                line.ConsumeQty = processed;
                line.VarianceQty = 0m;
                line.VarianceReasonCode = null;
                line.VarianceReasonText = null;
                line.RemainingAfterConsume = IvQty.Round(line.AvailableQty - line.ConsumeQty);
                line.StatusText = MaterialStatus(line);
                continue;
            }

            line.StandardQty = ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                line.WoBomRequiredQty, planned, processed);
            line.MaxConsumeQty = ProductionMaterialExecutionCalc.DailyProductionMaxQty(
                line.StandardQty, line.TolerancePercent);
            if (!line.IsConsumeOverridden)
            {
                line.ConsumeQty = line.StandardQty;
                line.VarianceReasonCode = null;
                line.VarianceReasonText = null;
            }

            ApplyLineDerived(line);
        }

        MaterialEdits = MaterialEdits.ToList();
    }

    private static void ApplyLineDerived(MaterialLineEdit line)
    {
        line.VarianceQty = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(
            line.ConsumeQty, line.StandardQty);
        if (line.VarianceQty == 0m && !line.IsHandoff)
        {
            line.VarianceReasonCode = null;
            line.VarianceReasonText = null;
        }

        line.RemainingAfterConsume = IvQty.Round(line.AvailableQty - line.ConsumeQty);
        line.StatusText = MaterialStatus(line);
    }

    private static string MaterialStatus(MaterialLineEdit line)
    {
        if (!string.IsNullOrWhiteSpace(line.BlockingReason))
            return line.BlockingReason;
        if (line.IsHandoff)
            return "Handoff locked";
        if (line.ConsumeQty > line.MaxConsumeQty)
            return "Over tolerance";
        if (line.VarianceQty != 0m && string.IsNullOrWhiteSpace(line.VarianceReasonCode))
            return "Reason required";
        if (string.Equals(line.VarianceReasonCode, ProductionMaterialVarianceReasonCodes.Other, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(line.VarianceReasonText))
            return "Reason detail required";
        if (line.RemainingAfterConsume < 0m)
            return $"Shortage {Math.Abs(line.RemainingAfterConsume):n4}";
        if (line.VarianceQty > 0m)
            return "Over consume";
        if (line.VarianceQty < 0m)
            return "Under consume";
        return "OK";
    }

    private IReadOnlyList<ProductionOutputMaterialInput> BuildMaterialInputs() =>
        MaterialEdits
            .Where(x => !x.IsHandoff)
            .Select(x => new ProductionOutputMaterialInput
            {
                WorkOrderMaterialId = x.WorkOrderMaterialId,
                ConsumeQty = x.ConsumeQty,
                VarianceReasonCode = x.VarianceReasonCode,
                VarianceReasonText = x.VarianceReasonText,
            })
            .ToList();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected sealed class MaterialLineEdit
    {
        public long WorkOrderMaterialId { get; set; }
        public string ComponentCode { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SupplySource { get; set; } = string.Empty;
        public string IssueMethod { get; set; } = string.Empty;
        public string RequiredUom { get; set; } = string.Empty;
        public decimal WoBomRequiredQty { get; set; }
        public decimal TolerancePercent { get; set; }
        public decimal StandardQty { get; set; }
        public decimal ConsumeQty { get; set; }
        public decimal MaxConsumeQty { get; set; }
        public decimal VarianceQty { get; set; }
        public string? VarianceReasonCode { get; set; }
        public string? VarianceReasonText { get; set; }
        public decimal AvailableQty { get; set; }
        public decimal RemainingAfterConsume { get; set; }
        public string? BlockingReason { get; set; }
        public bool IsHandoff { get; set; }
        public long? HandoffFromOperationId { get; set; }
        public bool IsConsumeEditable { get; set; }
        public bool IsConsumeOverridden { get; set; }
        public string StatusText { get; set; } = "OK";
    }
}
