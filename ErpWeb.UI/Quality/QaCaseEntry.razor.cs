using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Quality;

public partial class QaCaseEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? CaseNo { get; set; }

    [SupplyParameterFromQuery(Name = "source")] public string? QuerySource { get; set; }
    [SupplyParameterFromQuery(Name = "poNo")] public string? QueryPoNo { get; set; }
    [SupplyParameterFromQuery(Name = "poRelNo")] public int? QueryPoRelNo { get; set; }
    [SupplyParameterFromQuery(Name = "deliveryNote")] public string? QueryDeliveryNote { get; set; }
    [SupplyParameterFromQuery(Name = "supplierCode")] public string? QuerySupplierCode { get; set; }
    [SupplyParameterFromQuery(Name = "supplierName")] public string? QuerySupplierName { get; set; }
    [SupplyParameterFromQuery(Name = "returnUrl")] public string? QueryReturnUrl { get; set; }
    [SupplyParameterFromQuery(Name = "workOrderNo")] public string? QueryWorkOrderNo { get; set; }
    [SupplyParameterFromQuery(Name = "lotId")] public long? QueryLotId { get; set; }
    [SupplyParameterFromQuery(Name = "outputId")] public long? QueryOutputId { get; set; }
    [SupplyParameterFromQuery(Name = "sourceHeaderId")] public long? QuerySourceHeaderId { get; set; }
    [SupplyParameterFromQuery(Name = "sourceDocNo")] public string? QuerySourceDocNo { get; set; }

    [Inject] private IQaCaseService Cases { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsDirty;
    protected bool LinePopupVisible;
    protected bool SourcePopupVisible;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCancelCaseVisible;
    protected bool ConfirmPassAllVisible;
    protected bool ConfirmReinspectVisible;
    protected bool HelpVisible;
    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDeletePermission;
    protected bool CanSubmitPermission;
    protected bool CanApprovePermission;
    protected bool CanRejectPermission;
    protected bool CanOpenGrn;
    protected bool CanOpenFg;
    protected bool CanOpenVendorReturn;
    protected bool CanOpenScrap;
    protected bool CanOpenPurchaseCn;
    protected bool CanOpenDailyProduction;
    protected string? StatusMessage;
    protected string? PopupError;
    protected string SourceSearchText = string.Empty;
    protected byte[] RowVersion = [];
    protected CaseEditor Header { get; set; } = CaseEditor.New();
    protected List<LineEditor> Lines { get; set; } = [];
    protected LineEditor PopupLine { get; set; } = new();
    protected List<QaSourceRow> SourceRows { get; set; } = [];
    private bool _editingExistingLine;

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument => !IsViewMode && (IsNewMode ? CanAdd : CanEdit) && QaCaseStatuses.CanEdit(Header.Status);
    protected bool CanSave => CanEditDocument && !IsSubmitting;
    protected bool CanEditFromView => CanEdit && QaCaseStatuses.CanEdit(Header.Status);
    protected bool CanInspect =>
        IsViewMode
        && CanEdit
        && (string.Equals(Header.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.InInspection, StringComparison.OrdinalIgnoreCase));
    protected bool CanSubmit => IsViewMode && string.Equals(Header.Status, QaCaseStatuses.Draft, StringComparison.OrdinalIgnoreCase) && CanSubmitPermission;
    protected bool CanStartInspection => IsViewMode && string.Equals(Header.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase) && CanEdit;
    protected bool CanSubmitReview => IsViewMode && string.Equals(Header.Status, QaCaseStatuses.InInspection, StringComparison.OrdinalIgnoreCase) && CanSubmitPermission;
    protected bool CanApprove => IsViewMode && string.Equals(Header.Status, QaCaseStatuses.PendingReview, StringComparison.OrdinalIgnoreCase) && CanApprovePermission;
    protected bool CanReject =>
        IsViewMode
        && CanRejectPermission
        && (string.Equals(Header.Status, QaCaseStatuses.PendingReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase));
    protected bool CanCancelCase =>
        IsViewMode
        && CanDeletePermission
        && (string.Equals(Header.Status, QaCaseStatuses.Draft, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase));
    protected bool CanCloseCase =>
        IsViewMode
        && Header.HeldQty <= 0m
        && Header.PhysicalHeldQty <= 0m
        && Header.OpenActionCount == 0
        && (string.Equals(Header.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        && CanEdit;
    protected bool CanManageActions =>
        IsViewMode
        && CanEdit
        && Header.FollowUpReady
        && (string.Equals(Header.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.InInspection, StringComparison.OrdinalIgnoreCase));
    protected bool CanReinspect =>
        IsViewMode
        && CanEdit
        && Header.FollowUpReady
        && Header.HeldQty <= 0m
        && Header.PhysicalHeldQty <= 0m
        && (string.Equals(Header.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Header.Status, QaCaseStatuses.Rejected, StringComparison.OrdinalIgnoreCase));
    protected bool CanReleaseHold =>
        IsViewMode
        && (Header.HeldQty > 0m || Header.PhysicalHeldQty > 0m)
        && string.Equals(Header.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase)
        && CanApprovePermission;

    protected bool IsIncoming =>
        string.Equals(Header.SourceType, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase);
    protected bool IsFgStaging =>
        string.Equals(Header.SourceType, QaSourceTypes.FgStagingLot, StringComparison.OrdinalIgnoreCase);
    protected bool IsWipLot =>
        string.Equals(Header.SourceType, QaSourceTypes.ProductionWipLot, StringComparison.OrdinalIgnoreCase);
    protected string PhaseChip =>
        Header.HeldQty > 0m || Header.PhysicalHeldQty > 0m
            ? "QA reserved"
            : Header.PostedQty > 0m && IsIncoming
                ? "GRN posted"
                : Header.PostedQty > 0m && IsFgStaging
                    ? "FG posted"
                    : string.Equals(Header.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase)
                        ? "Inspection approved"
                        : "Record-only";
    protected string PageHeading => IsNewMode ? "New quality case" : $"Quality case {CaseNoDisplay}";
    protected string ModeChip => IsNewMode ? "New" : IsViewMode ? "View" : "Edit";
    protected string CaseNoDisplay => string.IsNullOrWhiteSpace(Header.CaseNo) ? "New" : Header.CaseNo;
    protected string StatusDisplay => QaLabels.Status(Header.Status);
    protected string NextActionHint => QaLabels.NextAction(Header.Status);
    protected string WorkflowStep => QaLabels.WorkflowStep(Header.Status);
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected IReadOnlyList<QaNamedOption> SourceTypeOptions => QaLabels.SourceOptions;
    protected IReadOnlyList<QaNamedOption> ResultOptions => QaLabels.ResultOptions;
    protected IReadOnlyList<QaNamedOption> ActionTypeOptions => QaLabels.ActionTypeOptions;
    protected int DraftActionLineNo { get; set; } = 1;
    protected string DraftActionType { get; set; } = QaActionTypes.FollowUp;
    protected decimal DraftActionQty { get; set; }
    protected string? DraftActionAssignedTo { get; set; }
    protected string? DraftActionReason { get; set; }
    protected DateTime? DraftActionDueDate { get; set; }
    protected string? ReinspectReason { get; set; }
    protected string SupplierDisplay => string.Join(" · ", new[] { Header.SupplierCode, Header.SupplierName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    protected string LinePopupTitle => CanInspect && !CanEditDocument ? "Inspect line" : "Case line";

    protected bool CanOpenDestination(QaDestinationDto destination)
    {
        if (!destination.CanNavigate || string.IsNullOrWhiteSpace(destination.Href))
        {
            return false;
        }

        return destination.Kind switch
        {
            QaDestinationKinds.GoodsReceipt => CanOpenGrn,
            QaDestinationKinds.FinishedGoodReceipt => CanOpenFg,
            QaDestinationKinds.VendorReturn => CanOpenVendorReturn,
            QaDestinationKinds.InventoryScrap => CanOpenScrap,
            QaDestinationKinds.PurchaseCreditNote => CanOpenPurchaseCn,
            QaDestinationKinds.DailyProduction => CanOpenDailyProduction,
            _ => false
        };
    }

    protected bool CanChangeSourceType =>
        CanEditDocument
        && string.IsNullOrWhiteSpace(QuerySource)
        && string.IsNullOrWhiteSpace(Header.SourceDocNo)
        && Header.SourceHeaderId is null
        && Header.ProductionBalLotId is null;

    protected bool CanEditDeliveryNote => CanEditDocument && IsIncoming;

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Edit);
        CanDeletePermission = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Delete);
        CanSubmitPermission = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Submit);
        CanApprovePermission = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Approve);
        CanRejectPermission = await AccessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Reject);
        CanOpenGrn = await AccessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Access);
        CanOpenFg = await AccessRights.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Access);
        CanOpenVendorReturn = await AccessRights.CanAsync(MenuCodes.InventoryVendorReturn, PermissionCodes.Access);
        CanOpenScrap = await AccessRights.CanAsync(MenuCodes.InventoryScrap, PermissionCodes.Access);
        CanOpenPurchaseCn = await AccessRights.CanAsync(MenuCodes.PurchaseCreditNote, PermissionCodes.Access);
        CanOpenDailyProduction = await AccessRights.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Access);

        if (IsNewMode)
        {
            Header = CaseEditor.New();
            ApplyQueryPrefill();
            await TryLoadSourceLinesAsync();
            IsLoading = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(CaseNo))
        {
            ErrorMessage = "Quality case was not found.";
            IsLoading = false;
            return;
        }

        var result = await Cases.GetAsync(CaseNo);
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Quality case was not found.";
            IsLoading = false;
            return;
        }

        LoadCase(result.Case);
        IsLoading = false;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        var result = await Cases.SaveDraftAsync(BuildSaveCommand());
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        IsDirty = false;
        Navigation.NavigateTo($"/quality/cases/view/{result.Case.CaseNo}");
    }

    protected async Task OnSubmitAsync() => await RunReviewAsync(Cases.SubmitAsync);
    protected async Task OnStartInspectionAsync() => await RunReviewAsync(Cases.StartInspectionAsync);
    protected async Task OnSubmitReviewAsync() => await RunReviewAsync(Cases.SubmitForReviewAsync);
    protected async Task OnApproveAsync() => await RunReviewAsync(Cases.ApproveAsync);
    protected async Task OnRejectAsync() => await RunReviewAsync(Cases.RejectAsync);
    protected async Task OnCloseCaseAsync() => await RunReviewAsync(Cases.CloseAsync);
    protected async Task OnReleaseHoldAsync() => await RunReviewAsync(Cases.ReleaseHoldAsync);
    protected void OnReinspect() => ConfirmReinspectVisible = true;

    protected async Task ConfirmReinspectAsync()
    {
        IsSubmitting = true;
        var result = await Cases.ReinspectAsync(new QaReviewCommand
        {
            CaseNo = Header.CaseNo,
            RowVersion = RowVersion,
            Remarks = ReinspectReason
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        ConfirmReinspectVisible = false;
        LoadCase(result.Case);
        ReinspectReason = null;
        StatusMessage = "Reinspection started on the same source. Stock was not changed.";
    }

    protected async Task SaveFollowUpAsync()
    {
        IsSubmitting = true;
        var result = await Cases.SaveActionAsync(new QaActionSaveCommand
        {
            CaseNo = Header.CaseNo,
            RowVersion = RowVersion,
            LineNo = DraftActionLineNo,
            ActionType = DraftActionType,
            Qty = DraftActionQty,
            AssignedTo = DraftActionAssignedTo,
            Reason = DraftActionReason,
            DueDate = DraftActionDueDate
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        DraftActionReason = null;
        DraftActionQty = 0m;
        StatusMessage = "Follow-up task saved. This is not a stock posting.";
    }

    protected async Task CompleteFollowUpAsync(QaActionDto action)
    {
        await MutateFollowUpAsync(action, Cases.CompleteActionAsync);
    }

    protected async Task StartFollowUpAsync(QaActionDto action)
    {
        await MutateFollowUpAsync(action, Cases.StartActionAsync);
    }

    protected async Task CancelFollowUpAsync(QaActionDto action)
    {
        await MutateFollowUpAsync(action, Cases.CancelActionAsync, "Cancelled from the quality case.");
    }

    private async Task MutateFollowUpAsync(
        QaActionDto action,
        Func<QaActionUpdateCommand, CancellationToken, Task<QaResult>> mutate,
        string? reason = null)
    {
        IsSubmitting = true;
        var result = await mutate(new QaActionUpdateCommand
        {
            CaseNo = Header.CaseNo,
            RowVersion = RowVersion,
            ActionNo = action.ActionNo,
            Reason = reason,
            CompletionEvidence = reason is null ? "Recorded in Quality. No stock was posted." : null
        }, CancellationToken.None);
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        StatusMessage = "Follow-up updated. Stock was not changed.";
    }

    protected void OnCancelCase() => ConfirmCancelCaseVisible = true;

    protected async Task ConfirmCancelCaseAsync()
    {
        ConfirmCancelCaseVisible = false;
        await RunReviewAsync(Cases.CancelAsync);
    }

    protected string StepClass(string step)
    {
        var current = WorkflowStep;
        if (string.Equals(step, current, StringComparison.OrdinalIgnoreCase))
        {
            return "qa-steps__item is-current";
        }

        var order = new[] { "source", "inspect", "review", "followup" };
        var stepIndex = Array.IndexOf(order, step);
        var currentIndex = Array.IndexOf(order, current);
        return stepIndex >= 0 && currentIndex > stepIndex ? "qa-steps__item is-done" : "qa-steps__item";
    }

    protected static decimal PendingQty(LineEditor line) => Math.Max(0m, line.Qty - line.InspectedQty);

    protected static string? LineInspectError(LineEditor line)
    {
        if (line.InspectedQty <= 0m)
        {
            return null;
        }

        if (line.InspectedQty > line.Qty)
        {
            return "Inspected qty cannot exceed case qty.";
        }

        var sum = line.AcceptQty + line.RejectQty + line.ReworkQty + line.HoldQty;
        return sum == line.InspectedQty
            ? null
            : "Accept + reject + rework + hold must equal inspected qty.";
    }

    protected void BeginPassAll()
    {
        if (Lines.Count == 0)
        {
            ErrorMessage = "Add lines before recording inspection.";
            return;
        }

        ConfirmPassAllVisible = true;
    }

    protected async Task ConfirmPassAllAsync()
    {
        ConfirmPassAllVisible = false;
        foreach (var line in Lines)
        {
            PassFullAmount(line);
        }

        await SaveAllInspectionsAsync();
    }

    protected static void PassFullAmount(LineEditor line)
    {
        line.InspectedQty = line.Qty;
        line.AcceptQty = line.Qty;
        line.RejectQty = 0m;
        line.ReworkQty = 0m;
        line.HoldQty = 0m;
        line.Result = QaInspectionResults.Pass;
    }

    protected async Task SaveAllInspectionsAsync()
    {
        var lines = Lines.Where(x => x.InspectedQty > 0m).ToList();
        if (lines.Count == 0)
        {
            ErrorMessage = "Enter at least one inspection result.";
            return;
        }

        var firstError = lines.Select(LineInspectError).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (firstError is not null)
        {
            ErrorMessage = firstError;
            return;
        }

        IsSubmitting = true;
        var result = await Cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = Header.CaseNo,
            RowVersion = RowVersion,
            Lines = lines.Select(x => new QaInspectionLineCommand
            {
                LineNo = x.LineNo,
                Result = x.Result,
                InspectedQty = x.InspectedQty,
                Notes = x.Notes,
                Dispositions = x.ToDispositions()
            }).ToList()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        StatusMessage = "Inspection saved. Stock was not changed.";
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo(SafeReturnUrl());
        return Task.CompletedTask;
    }

    protected void OnEditFromView() => Navigation.NavigateTo($"/quality/cases/edit/{Header.CaseNo}");

    protected Task OnCancelAsync()
    {
        if (IsDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        return ConfirmDiscardAsync();
    }

    protected Task ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        Navigation.NavigateTo(SafeReturnUrl());
        return Task.CompletedTask;
    }

    private string SafeReturnUrl()
    {
        var url = (QueryReturnUrl ?? Header.SourceHref ?? string.Empty).Trim();
        if (url.StartsWith("/inventory/goods-receipts", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("/inventory/misc-receipt", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("/planning/finished-good-receipts", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("/planning/daily-production", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return "/quality/cases";
    }

    protected Task OpenNewLine() => OpenSourceLookupAsync();

    protected void OpenEditLine(LineEditor line)
    {
        _editingExistingLine = true;
        PopupLine = line.Clone();
        PopupError = null;
        LinePopupVisible = true;
    }

    protected void OpenInspectLine(LineEditor line)
    {
        _editingExistingLine = true;
        PopupLine = line.Clone();
        if (PopupLine.InspectedQty <= 0m)
        {
            PopupLine.InspectedQty = PopupLine.Qty;
        }

        PopupError = null;
        LinePopupVisible = true;
    }

    protected void RemoveLine(LineEditor line)
    {
        Lines.Remove(line);
        IsDirty = true;
    }

    protected async Task CommitLineAsync()
    {
        if (CanInspect && !CanEditDocument)
        {
            await SaveInspectionLineAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(PopupLine.ItemCode) || string.IsNullOrWhiteSpace(PopupLine.Uom) || PopupLine.Qty <= 0m)
        {
            PopupError = "Item, UOM and quantity are required.";
            return;
        }

        if (_editingExistingLine)
        {
            var target = Lines.FirstOrDefault(x => x.LineNo == PopupLine.LineNo);
            if (target is not null)
            {
                CopyLine(PopupLine, target);
            }
        }
        else
        {
            Lines.Add(PopupLine.Clone());
        }

        IsDirty = true;
        LinePopupVisible = false;
    }

    protected async Task OpenSourceLookupAsync()
    {
        if (string.IsNullOrWhiteSpace(Header.SourceType))
        {
            ErrorMessage = "Select a source type first.";
            return;
        }

        SourcePopupVisible = true;
        await ApplySourceSearchAsync();
    }

    protected Task OnSourceSearchTextChanged(string text)
    {
        SourceSearchText = text ?? string.Empty;
        return Task.CompletedTask;
    }

    protected async Task ApplySourceSearchAsync()
    {
        var result = await Cases.SearchSourcesAsync(new QaSourceQuery
        {
            SourceType = Header.SourceType,
            SearchText = SourceSearchText,
            Skip = 0,
            Take = 50
        });
        SourceRows = result.SourcePage?.Rows.ToList() ?? [];
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
        }
    }

    protected void SelectSource(QaSourceRow row)
    {
        Header.SourceType = row.SourceType;
        Header.SourceDocType = row.SourceDocType;
        Header.SourceDocNo = row.SourceDocNo;
        Header.SourceHeaderId = row.SourceHeaderId;
        Header.SourceDocumentRevision = row.SourceDocumentRevision;
        Header.SupplierCode = FirstNonEmpty(row.SupplierCode, Header.SupplierCode);
        Header.SupplierName = FirstNonEmpty(row.SupplierName, Header.SupplierName);
        Header.DeliveryNote = FirstNonEmpty(row.DeliveryNote, Header.DeliveryNote);
        Header.PoNo = FirstNonEmpty(row.PoNo, Header.PoNo);
        if (row.PoRelNo is not null)
        {
            Header.PoRelNo = row.PoRelNo;
        }

        Header.WorkOrderNo = FirstNonEmpty(row.WorkOrderNo, Header.WorkOrderNo);
        Header.ProductionBalLotId = row.ProductionBalLotId ?? Header.ProductionBalLotId;
        Header.SourceHref = row.SourceHref;
        Header.EligibilityNote = row.EligibilityNote;
        AddSourceLine(row);
        IsDirty = true;
        SourcePopupVisible = false;
    }

    private void AddSourceLine(QaSourceRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ItemCode))
        {
            return;
        }

        if (Lines.Any(x => SameSourceLine(x, row)))
        {
            return;
        }

        Lines.Add(new LineEditor
        {
            LineNo = Lines.Count == 0 ? 1 : Lines.Max(x => x.LineNo) + 1,
            ItemCode = row.ItemCode ?? string.Empty,
            ItemName = row.ItemName,
            Uom = row.Uom ?? string.Empty,
            Qty = row.Qty,
            LotNo = row.LotNo,
            WarehouseCode = row.WarehouseCode,
            LocationCode = row.LocationCode,
            ItemStatus = row.ItemStatus,
            SourceLineId = row.SourceLineId,
            PoLineNo = row.PoLineNo,
            SourceIdentity = row.DisplayText
        });
    }

    private static bool SameSourceLine(LineEditor line, QaSourceRow row)
    {
        if (row.SourceLineId is > 0)
        {
            return line.SourceLineId == row.SourceLineId;
        }

        if (row.PoLineNo is > 0)
        {
            return line.PoLineNo == row.PoLineNo
                && string.Equals(line.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase);
        }

        if (row.ProductionBalLotId is > 0)
        {
            return string.Equals(line.LotNo, row.LotNo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(line.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(line.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(line.LotNo ?? string.Empty, row.LotNo ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(line.Uom, row.Uom, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private QaSourcePrefillQuery PrefillQuery() => new()
    {
        LotId = QueryLotId,
        OutputId = QueryOutputId,
        SourceHeaderId = QuerySourceHeaderId,
        PoNo = QueryPoNo,
        PoRelNo = QueryPoRelNo
    };

    private async Task TryLoadSourceLinesAsync()
    {
        var query = PrefillQuery();
        if (string.IsNullOrWhiteSpace(Header.SourceType) || !QaSourcePrefill.HasIdentity(query))
        {
            return;
        }

        var search = QaSourcePrefill.SearchText(query);
        var result = await Cases.SearchSourcesAsync(new QaSourceQuery
        {
            SourceType = Header.SourceType,
            SearchText = query.SourceHeaderId is > 0 || query.LotId is > 0 || query.OutputId is > 0
                ? null
                : search,
            ExactSourceHeaderId = QuerySourceHeaderId,
            ExactLotId = QueryLotId,
            ExactOutputId = QueryOutputId,
            Skip = 0,
            Take = 500
        });
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        var matches = QaSourcePrefill.Matching(result.SourcePage?.Rows ?? [], query);
        if (matches.Count == 0 && QuerySourceHeaderId is > 0)
        {
            var byDisplay = await Cases.SearchSourcesAsync(new QaSourceQuery
            {
                SourceType = Header.SourceType,
                SearchText = QuerySourceDocNo ?? QuerySourceHeaderId.Value.ToString(),
                Skip = 0,
                Take = 100
            });
            var displayKey = (QuerySourceDocNo ?? QuerySourceHeaderId.Value.ToString()).Trim();
            matches = (byDisplay.SourcePage?.Rows ?? [])
                .Where(x => string.Equals(x.SourceDocNo, displayKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matches.Count == 0)
        {
            StatusMessage = "The source item was not loaded automatically. Use Lookup to pick it from the source document.";
            return;
        }

        foreach (var row in matches)
        {
            SelectSource(row);
        }

        StatusMessage = matches.Count == 1
            ? $"Loaded {matches[0].ItemCode} from the source document."
            : $"Loaded {matches.Count} lines from the source document.";
    }

    protected string DispositionSummary(LineEditor line)
    {
        var parts = new List<string>();
        if (line.AcceptQty > 0) parts.Add($"Accept {line.AcceptQty:n4}");
        if (line.RejectQty > 0) parts.Add($"Reject {line.RejectQty:n4}");
        if (line.ReworkQty > 0) parts.Add($"Rework {line.ReworkQty:n4}");
        if (line.HoldQty > 0) parts.Add($"Hold {line.HoldQty:n4}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task RunReviewAsync(Func<QaReviewCommand, CancellationToken, Task<QaResult>> action)
    {
        IsSubmitting = true;
        var result = await action(new QaReviewCommand { CaseNo = Header.CaseNo, RowVersion = RowVersion }, CancellationToken.None);
        IsSubmitting = false;
        if (!result.Succeeded || result.Case is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        StatusMessage = $"Case is now {QaLabels.Status(result.Case.Status)}. Inspection record only — stock was not changed.";
    }

    private async Task SaveInspectionLineAsync()
    {
        var result = await Cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = Header.CaseNo,
            RowVersion = RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = PopupLine.LineNo,
                    Result = PopupLine.Result,
                    InspectedQty = PopupLine.InspectedQty,
                    Notes = PopupLine.Notes,
                    Dispositions = PopupLine.ToDispositions()
                }
            ]
        });
        if (!result.Succeeded || result.Case is null)
        {
            PopupError = result.ErrorMessage;
            return;
        }

        LoadCase(result.Case);
        LinePopupVisible = false;
        StatusMessage = "Inspection saved. Stock was not changed.";
    }

    private QaCaseSaveCommand BuildSaveCommand() => new()
    {
        CaseNo = Header.CaseNo,
        CaseDate = Header.CaseDate,
        SourceType = Header.SourceType,
        SourceDocType = Header.SourceDocType,
        SourceDocNo = Header.SourceDocNo,
        SourceHeaderId = Header.SourceHeaderId,
        SourceDocumentRevision = Header.SourceDocumentRevision,
        SupplierCode = Header.SupplierCode,
        SupplierName = Header.SupplierName,
        DeliveryNote = Header.DeliveryNote,
        PoNo = Header.PoNo,
        PoRelNo = Header.PoRelNo,
        WorkOrderNo = Header.WorkOrderNo,
        ProductionBalLotId = Header.ProductionBalLotId,
        SourceHref = Header.SourceHref,
        Remarks = Header.Remarks,
        RowVersion = RowVersion,
        Lines = Lines.Select(x => x.ToSave()).ToList()
    };

    private void ApplyQueryPrefill()
    {
        if (!string.IsNullOrWhiteSpace(QuerySource))
        {
            Header.SourceType = QuerySource.Trim();
        }

        if (!string.IsNullOrWhiteSpace(QueryPoNo))
        {
            Header.PoNo = QueryPoNo.Trim();
            Header.SourceDocType = "PO";
            Header.SourceDocNo = Header.PoNo;
        }

        Header.PoRelNo = QueryPoRelNo is >= short.MinValue and <= short.MaxValue
            ? (short)QueryPoRelNo.Value
            : null;
        Header.DeliveryNote = string.IsNullOrWhiteSpace(QueryDeliveryNote) ? Header.DeliveryNote : QueryDeliveryNote.Trim();
        Header.SupplierCode = string.IsNullOrWhiteSpace(QuerySupplierCode) ? Header.SupplierCode : QuerySupplierCode.Trim();
        Header.SupplierName = string.IsNullOrWhiteSpace(QuerySupplierName) ? Header.SupplierName : QuerySupplierName.Trim();
        if (!string.IsNullOrWhiteSpace(QueryWorkOrderNo))
        {
            Header.WorkOrderNo = QueryWorkOrderNo.Trim();
            Header.SourceDocType ??= "WO";
            Header.SourceDocNo ??= Header.WorkOrderNo;
        }

        if (!string.IsNullOrWhiteSpace(QuerySourceDocNo))
        {
            Header.SourceDocNo = QuerySourceDocNo.Trim();
        }

        if (string.Equals(Header.SourceType, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase))
        {
            Header.SourceDocType = "GR";
        }
        else if (string.Equals(Header.SourceType, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase))
        {
            Header.SourceDocType = "MR";
        }

        if (QueryOutputId is > 0)
        {
            Header.SourceHeaderId = QueryOutputId;
            Header.SourceDocType = "WI";
            Header.SourceDocNo ??= QueryOutputId.Value.ToString();
        }

        if (QueryLotId is > 0)
        {
            Header.ProductionBalLotId = QueryLotId;
        }

        if (IsIncoming)
        {
            Header.EligibilityNote = QaEligibilityNotes.IncomingPreGrn;
            if (!string.IsNullOrWhiteSpace(QueryReturnUrl) && QueryReturnUrl.StartsWith("/inventory/goods-receipts", StringComparison.OrdinalIgnoreCase))
            {
                Header.SourceHref = QueryReturnUrl;
            }
        }
        else if (IsFgStaging)
        {
            Header.EligibilityNote = QaEligibilityNotes.FgStaging;
            if (!string.IsNullOrWhiteSpace(QueryReturnUrl) && QueryReturnUrl.StartsWith("/planning/finished-good-receipts", StringComparison.OrdinalIgnoreCase))
            {
                Header.SourceHref = QueryReturnUrl;
            }
        }
        else if (!string.IsNullOrWhiteSpace(QueryReturnUrl) && QueryReturnUrl.StartsWith("/planning/daily-production", StringComparison.OrdinalIgnoreCase))
        {
            Header.SourceHref = QueryReturnUrl;
        }
    }

    private void LoadCase(QaCaseDto value)
    {
        Header = CaseEditor.From(value);
        RowVersion = value.RowVersion;
        Lines = value.Lines.Select(LineEditor.From).ToList();
        if (Lines.Count > 0 && Lines.All(x => x.LineNo != DraftActionLineNo))
        {
            DraftActionLineNo = Lines[0].LineNo;
        }
    }

    private static void CopyLine(LineEditor from, LineEditor to)
    {
        to.ItemCode = from.ItemCode;
        to.ItemName = from.ItemName;
        to.Uom = from.Uom;
        to.Qty = from.Qty;
        to.LotNo = from.LotNo;
        to.WarehouseCode = from.WarehouseCode;
        to.LocationCode = from.LocationCode;
        to.ItemStatus = from.ItemStatus;
        to.Remarks = from.Remarks;
    }

    protected sealed class CaseEditor
    {
        public string CaseNo { get; set; } = string.Empty;
        public DateTime CaseDate { get; set; } = DateTime.Today;
        public string Status { get; set; } = QaCaseStatuses.Draft;
        public string SourceType { get; set; } = string.Empty;
        public string? SourceDocType { get; set; }
        public string? SourceDocNo { get; set; }
        public long? SourceHeaderId { get; set; }
        public int? SourceDocumentRevision { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? DeliveryNote { get; set; }
        public string? PoNo { get; set; }
        public short? PoRelNo { get; set; }
        public string? WorkOrderNo { get; set; }
        public long? ProductionBalLotId { get; set; }
        public string? SourceHref { get; set; }
        public string EligibilityNote { get; set; } = QaEligibilityNotes.RecordOnly;
        public string? Remarks { get; set; }
        public decimal DeliveredQty { get; set; }
        public decimal AcceptedQty { get; set; }
        public decimal RejectedQty { get; set; }
        public decimal PostedQty { get; set; }
        public decimal RemainingQty { get; set; }
        public decimal HeldQty { get; set; }
        public decimal PhysicalHeldQty { get; set; }
        public IReadOnlyList<QaAuditEventDto> Audit { get; set; } = [];
        public IReadOnlyList<QaDestinationDto> Destinations { get; set; } = [];
        public IReadOnlyList<QaActionDto> Actions { get; set; } = [];
        public IReadOnlyList<QaAttemptDto> Attempts { get; set; } = [];
        public string? SourceCheckNote { get; set; }
        public bool FollowUpReady { get; set; }
        public int OpenActionCount { get; set; }
        public string AttachmentsNote { get; set; } = QaCaseMessages.AttachmentsDeferred;

        public static CaseEditor New() => new();

        public static CaseEditor From(QaCaseDto value) => new()
        {
            CaseNo = value.CaseNo,
            CaseDate = value.CaseDate,
            Status = value.Status,
            SourceType = value.SourceType,
            SourceDocType = value.SourceDocType,
            SourceDocNo = value.SourceDocNo,
            SourceHeaderId = value.SourceHeaderId,
            SourceDocumentRevision = value.SourceDocumentRevision,
            SupplierCode = value.SupplierCode,
            SupplierName = value.SupplierName,
            DeliveryNote = value.DeliveryNote,
            PoNo = value.PoNo,
            PoRelNo = value.PoRelNo,
            WorkOrderNo = value.WorkOrderNo,
            ProductionBalLotId = value.ProductionBalLotId,
            SourceHref = value.SourceHref,
            EligibilityNote = value.EligibilityNote,
            Remarks = value.Remarks,
            DeliveredQty = value.DeliveredQty,
            AcceptedQty = value.AcceptedQty,
            RejectedQty = value.RejectedQty,
            PostedQty = value.PostedQty,
            RemainingQty = value.RemainingQty,
            HeldQty = value.HeldQty,
            PhysicalHeldQty = value.PhysicalHeldQty,
            Audit = value.Audit,
            Destinations = value.Destinations,
            Actions = value.Actions,
            Attempts = value.Attempts,
            SourceCheckNote = value.SourceCheckNote,
            FollowUpReady = value.FollowUpReady,
            OpenActionCount = value.OpenActionCount,
            AttachmentsNote = value.AttachmentsNote
        };
    }

    protected sealed class LineEditor
    {
        public int LineNo { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string? ItemName { get; set; }
        public string Uom { get; set; } = string.Empty;
        public decimal Qty { get; set; }
        public decimal InspectedQty { get; set; }
        public string? LotNo { get; set; }
        public string? WarehouseCode { get; set; }
        public string? LocationCode { get; set; }
        public string? ItemStatus { get; set; }
        public long? SourceLineId { get; set; }
        public short? PoLineNo { get; set; }
        public string? SourceIdentity { get; set; }
        public string? Remarks { get; set; }
        public string Result { get; set; } = QaInspectionResults.Pass;
        public string? Notes { get; set; }
        public decimal AcceptQty { get; set; }
        public decimal RejectQty { get; set; }
        public decimal ReworkQty { get; set; }
        public decimal HoldQty { get; set; }
        public QaInspectionDto? Inspection { get; set; }

        public LineEditor Clone() => FromSave(this);

        public QaCaseLineSaveCommand ToSave() => new()
        {
            LineNo = LineNo,
            ItemCode = ItemCode,
            ItemName = ItemName,
            Uom = Uom,
            Qty = Qty,
            LotNo = LotNo,
            WarehouseCode = WarehouseCode,
            LocationCode = LocationCode,
            ItemStatus = ItemStatus,
            SourceLineId = SourceLineId,
            PoLineNo = PoLineNo,
            SourceIdentity = SourceIdentity,
            Remarks = Remarks
        };

        public IReadOnlyList<QaDispositionDto> ToDispositions() =>
        [
            new() { Kind = QaDispositionKinds.Accept, Qty = AcceptQty },
            new() { Kind = QaDispositionKinds.Reject, Qty = RejectQty },
            new() { Kind = QaDispositionKinds.Rework, Qty = ReworkQty },
            new() { Kind = QaDispositionKinds.Hold, Qty = HoldQty }
        ];

        public static LineEditor From(QaCaseLineDto line)
        {
            var editor = new LineEditor
            {
                LineNo = line.LineNo,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                Uom = line.Uom,
                Qty = line.Qty,
                InspectedQty = line.InspectedQty,
                LotNo = line.LotNo,
                WarehouseCode = line.WarehouseCode,
                LocationCode = line.LocationCode,
                ItemStatus = line.ItemStatus,
                SourceLineId = line.SourceLineId,
                PoLineNo = line.PoLineNo,
                SourceIdentity = line.SourceIdentity,
                Remarks = line.Remarks,
                Inspection = line.Inspection,
                Result = line.Inspection?.Result ?? QaInspectionResults.Pass,
                Notes = line.Inspection?.Notes
            };
            foreach (var d in line.Dispositions)
            {
                switch (d.Kind)
                {
                    case QaDispositionKinds.Accept: editor.AcceptQty = d.Qty; break;
                    case QaDispositionKinds.Reject: editor.RejectQty = d.Qty; break;
                    case QaDispositionKinds.Rework: editor.ReworkQty = d.Qty; break;
                    case QaDispositionKinds.Hold: editor.HoldQty = d.Qty; break;
                }
            }

            return editor;
        }

        private static LineEditor FromSave(LineEditor line) => new()
        {
            LineNo = line.LineNo,
            ItemCode = line.ItemCode,
            ItemName = line.ItemName,
            Uom = line.Uom,
            Qty = line.Qty,
            InspectedQty = line.InspectedQty,
            LotNo = line.LotNo,
            WarehouseCode = line.WarehouseCode,
            LocationCode = line.LocationCode,
            ItemStatus = line.ItemStatus,
            SourceLineId = line.SourceLineId,
            PoLineNo = line.PoLineNo,
            SourceIdentity = line.SourceIdentity,
            Remarks = line.Remarks,
            Result = line.Result,
            Notes = line.Notes,
            AcceptQty = line.AcceptQty,
            RejectQty = line.RejectQty,
            ReworkQty = line.ReworkQty,
            HoldQty = line.HoldQty,
            Inspection = line.Inspection
        };
    }
}
