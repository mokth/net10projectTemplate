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
    protected string? StatusMessage;

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(Mode) && OutputId is null);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected string ModeChip => IsViewMode ? "VIEW" : IsEditMode ? "EDIT" : "NEW";
    protected string PageHeading => IsNewMode ? "New Daily Production" : IsEditMode ? "Edit Daily Production" : "Daily Production";

    protected long? SelectedOperationId;
    protected long? CurrentOutputId;
    protected string DocumentNo = string.Empty;
    protected string WorkOrderNo = string.Empty;
    protected string HeaderStatus = ProductionOutputStatuses.New;
    protected string OutputItemCode = string.Empty;
    protected string OutputUom = string.Empty;
    protected string PostingRequestId = string.Empty;
    protected byte[] RowVersion = [];

    protected DateTime ProductionDate = DateTime.Today;
    protected string? ShiftCode;
    protected string? ActualMachineCode;
    protected string? OperatorCode;
    protected string OutputLotNo = string.Empty;
    protected decimal GoodQty;
    protected decimal ScrapQty;
    protected decimal RejectQty;
    protected decimal HoldQty;

    protected List<ProductionOutputMaterialLine> Materials { get; set; } = [];
    protected List<ProductionEligibleOperationRow> EligibleRows { get; set; } = [];
    protected string EligibleWorkOrderNo = string.Empty;
    protected string EligibleProductCode = string.Empty;
    protected string EligibleWorkCentre = string.Empty;

    protected bool CanAccessAdd;
    protected bool CanAccessEdit;
    protected bool CanAccessPost;
    protected bool CanAccessRollback;
    protected bool CanAccessDelete;

    protected bool CanEditFields => (IsNewMode || IsEditMode) && HeaderStatus == ProductionOutputStatuses.New;
    protected bool CanSave => CanEditFields && SelectedOperationId is > 0 && (IsNewMode ? CanAccessAdd : CanAccessEdit);
    protected bool CanPost => CurrentOutputId is > 0 && HeaderStatus == ProductionOutputStatuses.New && CanAccessPost;
    protected bool CanRollback => CurrentOutputId is > 0 && HeaderStatus == ProductionOutputStatuses.Posted && CanAccessRollback;
    protected bool CanDelete => CurrentOutputId is > 0 && HeaderStatus == ProductionOutputStatuses.New && CanAccessDelete && !IsNewMode;

    protected bool RollbackConfirmVisible;
    protected string RollbackReason = string.Empty;
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

        SelectedOperationId = null;
        CurrentOutputId = null;
        Materials = [];
        EligibleRows = [];
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
            await SearchEligibleAsync();
        }

        IsBootstrapping = false;
    }

    protected async Task SearchEligibleAsync()
    {
        var result = await Outputs.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
        {
            WorkOrderNo = EligibleWorkOrderNo,
            ProductCode = EligibleProductCode,
            WorkCentreCode = EligibleWorkCentre,
            Take = 100,
        });
        if (!result.Succeeded)
        {
            ErrorMessage = result.Message;
            EligibleRows = [];
            return;
        }

        EligibleRows = result.Data?.ToList() ?? [];
    }

    protected async Task SelectOperationAsync(long operationId)
    {
        ErrorMessage = null;
        var workspace = await Outputs.GetWorkspaceAsync(operationId);
        if (!workspace.Succeeded || workspace.Data is null)
        {
            ErrorMessage = workspace.Message ?? "Unable to load operation workspace.";
            return;
        }

        SelectedOperationId = operationId;
        WorkOrderNo = workspace.Data.Operation.WorkOrderNo;
        OutputItemCode = workspace.Data.Operation.OutputItemCode;
        OutputUom = workspace.Data.Operation.OutputUom ?? string.Empty;
        Materials = workspace.Data.Materials.ToList();
        HeaderStatus = ProductionOutputStatuses.New;
        ProductionDate = DateTime.Now;
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
        CurrentOutputId = d.Uid;
        DocumentNo = d.DocumentNo;
        WorkOrderNo = d.WorkOrderNo;
        HeaderStatus = d.Status;
        SelectedOperationId = d.WorkOrderOperationId;
        ProductionDate = d.ProductionDate;
        ShiftCode = d.ShiftCode;
        ActualMachineCode = d.ActualMachineCode;
        OperatorCode = d.OperatorCode;
        OutputLotNo = d.OutputLotNo;
        GoodQty = d.GoodQty;
        ScrapQty = d.ScrapQty;
        RejectQty = d.RejectQty;
        HoldQty = d.HoldQty;
        OutputItemCode = d.OutputItemCode;
        OutputUom = d.OutputUom;
        PostingRequestId = d.PostingRequestId;
        RowVersion = d.RowVersion;

        var workspace = await Outputs.GetWorkspaceAsync(d.WorkOrderOperationId);
        if (workspace.Succeeded && workspace.Data is not null)
            Materials = workspace.Data.Materials.ToList();
    }

    protected async Task SaveAsync()
    {
        if (SelectedOperationId is null or <= 0) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            if (CurrentOutputId is null or <= 0)
            {
                var created = await Outputs.CreateAsync(new ProductionOutputCreateRequest
                {
                    WorkOrderOperationId = SelectedOperationId.Value,
                    ProductionDate = ProductionDate,
                    ShiftCode = ShiftCode,
                    ActualMachineCode = ActualMachineCode,
                    OperatorCode = OperatorCode,
                    GoodQty = GoodQty,
                    ScrapQty = ScrapQty,
                    RejectQty = RejectQty,
                    HoldQty = HoldQty,
                    OutputLotNo = OutputLotNo,
                });
                if (!created.Succeeded || created.Data is null)
                {
                    ErrorMessage = created.Message;
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
            });
            if (!updated.Succeeded || updated.Data is null)
            {
                ErrorMessage = updated.Message;
                return;
            }

            Navigation.NavigateTo($"/planning/daily-production/view/{updated.Data.Uid}", replace: true);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task PostAsync()
    {
        if (CurrentOutputId is null or <= 0) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Outputs.PostAsync(CurrentOutputId.Value);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message;
                return;
            }

            await LoadDocumentAsync(result.Data.Uid);
            StatusMessage = $"Posted {DocumentNo}.";
            Navigation.NavigateTo($"/planning/daily-production/view/{result.Data.Uid}", replace: true);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenRollbackConfirm()
    {
        RollbackReason = string.Empty;
        RollbackConfirmVisible = true;
    }

    protected async Task RollbackAsync()
    {
        if (CurrentOutputId is null or <= 0) return;
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
                OutputId = CurrentOutputId.Value,
                PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = RollbackReason.Trim(),
            });
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message;
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

    protected async Task DeleteAsync()
    {
        if (CurrentOutputId is null or <= 0) return;
        IsSubmitting = true;
        try
        {
            var result = await Outputs.DeleteAsync(CurrentOutputId.Value);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            Navigation.NavigateTo("/planning/daily-production");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void BackToList() => Navigation.NavigateTo("/planning/daily-production");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private void ResetFormFields()
    {
        DocumentNo = WorkOrderNo = OutputItemCode = OutputUom = PostingRequestId = OutputLotNo = string.Empty;
        ShiftCode = ActualMachineCode = OperatorCode = null;
        GoodQty = ScrapQty = RejectQty = HoldQty = 0m;
        ProductionDate = DateTime.Today;
        HeaderStatus = ProductionOutputStatuses.New;
        RowVersion = [];
    }
}
