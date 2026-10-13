using System.Text.Json;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderTransferEntry : IAsyncDisposable
{
    [Parameter] public long Id { get; set; }
    [Parameter] public string Mode { get; set; } = "";

    [Inject] private IProductionWorkOrderTransferService Transfers { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    protected ProductionWorkOrderTransferDocument Document { get; set; } = new() { Status = "NEW" };
    protected List<ProductionWorkOrderTransferSource> Sources { get; set; } = [];
    protected List<ProductionWorkOrderTransferTarget> Targets { get; set; } = [];
    protected ProductionStockWorkOrderLookupRow? SelectedSourceWorkOrder { get; set; }
    protected ProductionStockWorkOrderLookupRow? CandidateTargetWorkOrder { get; set; }
    protected ProductionWorkOrderTransferSource? SelectedSource { get; set; }
    protected ProductionWorkOrderTransferTarget? SelectedTarget { get; set; }

    protected string SourceSearch { get; set; } = "";
    protected string? TargetWorkOrderNo { get; set; }
    protected string Error { get; set; } = "";
    protected string Message { get; set; } = "";
    protected string RollbackReason { get; set; } = "";

    protected bool Loading { get; set; } = true;
    protected bool Busy { get; set; }
    protected bool SourceLoading { get; set; }
    protected bool TargetLoading { get; set; }
    protected bool CanAdd { get; set; }
    protected bool CanEdit { get; set; }
    protected bool CanDeleteAccess { get; set; }
    protected bool CanPostAccess { get; set; }
    protected bool CanRollbackAccess { get; set; }
    protected bool CanViewCostAccess { get; set; }
    protected bool TargetPickerVisible { get; set; }
    protected bool ChangeSourceConfirmVisible { get; set; }
    protected bool PostConfirmVisible { get; set; }
    protected bool RollbackConfirmVisible { get; set; }
    protected bool DeleteConfirmVisible { get; set; }
    protected int SourceSkip { get; set; }
    protected int SourceTotal { get; set; }
    protected int TargetSkip { get; set; }
    protected int TargetTotal { get; set; }
    protected const int LookupPageSize = 20;

    private string? _loadedKey;
    private string _cleanSnapshot = "";
    private DateTime _baselineTransferDateTime;
    private string? _baselineRemark;
    private string _pendingSourceChange = "";
    private long _targetPickerGeneration;
    private CancellationTokenSource? _sourceLoadCts;
    private CancellationTokenSource? _targetLoadCts;
    private long _sourceLoadSequence;
    private long _targetLoadSequence;
    private bool _disposed;
    private ProductionWorkOrderTransferCommand? _pendingPost;
    private ProductionWorkOrderTransferCommand? _pendingRollback;

    protected bool CanEditFields => Document.Status == "NEW"
        && (Id == 0 ? CanAdd : CanEdit && Mode.Equals("edit", StringComparison.OrdinalIgnoreCase));
    protected bool CanSave => CanEditFields && !TargetPickerVisible
        && (Id == 0 ? SelectedSource is not null && SelectedTarget is not null
            : Document.TargetWorkOrderMaterialId > 0);
    protected bool CanDelete => Id > 0 && Document.Status == "NEW" && CanDeleteAccess;
    protected bool ShowPost => Id > 0 && Document.Status == "NEW" && CanPostAccess;
    protected bool CanRollback => Id > 0 && Document.Status == "POSTED" && CanRollbackAccess;
    protected bool CanEditDraft => Id > 0 && Document.Status == "NEW"
        && !Mode.Equals("edit", StringComparison.OrdinalIgnoreCase) && CanEdit;
    protected bool CanChangeSourceWorkOrder => Id == 0 && CanEditFields && SelectedSourceWorkOrder is not null;
    protected bool Dirty => CanEditFields && _cleanSnapshot.Length > 0
        && !string.Equals(_cleanSnapshot, CurrentSnapshot(), StringComparison.Ordinal);
    protected string Heading => Id == 0 ? "New Work Order Transfer"
        : CanEditFields ? "Edit Work Order Transfer" : "View Work Order Transfer";
    protected string SourceLookupKey => SelectedSourceWorkOrder?.WorkOrderId.ToString() ?? "source-work-order";
    protected string TargetPickerKey => $"{SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId}:{_targetPickerGeneration}";

    protected override async Task OnParametersSetAsync()
    {
        var route = Nav.ToBaseRelativePath(Nav.Uri);
        var key = $"{Id}|{Mode}|{route}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal)) return;
        _loadedKey = key;

        CancelSourceLoad();
        CancelTargetLoad();
        ClearEntryState();
        Loading = true;
        Error = "";
        Message = "";

        CanAdd = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Edit);
        CanDeleteAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Delete);
        CanPostAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Post);
        CanRollbackAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.Rollback);
        CanViewCostAccess = await Access.CanAsync(MenuCodes.PlanningWorkOrderTransfer, PermissionCodes.ViewCost);

        if (Id > 0)
        {
            var result = await Transfers.GetAsync(Id);
            if (!result.Succeeded || result.Data is null)
            {
                Error = result.Message ?? "Work Order Transfer was not found.";
                Loading = false;
                return;
            }

            Document = result.Data;
            if (Mode.Equals("edit", StringComparison.OrdinalIgnoreCase)
                && Document.Status == "NEW" && !CanEdit)
            {
                Nav.NavigateTo($"/planning/work-order-transfer/{Id}/view", replace: true);
                Loading = false;
                return;
            }
        }
        else
        {
            Document = new ProductionWorkOrderTransferDocument
            {
                Status = "NEW",
                TransferDateTime = Clock.Now,
                CanViewCost = CanViewCostAccess
            };
        }

        _baselineTransferDateTime = Document.TransferDateTime;
        _baselineRemark = Document.Remark;
        Loading = false;
        _cleanSnapshot = CurrentSnapshot();
    }

    private void ClearEntryState()
    {
        Document = new() { Status = "NEW" };
        Busy = false;
        Sources = [];
        Targets = [];
        SelectedSourceWorkOrder = null;
        CandidateTargetWorkOrder = null;
        SelectedSource = null;
        SelectedTarget = null;
        SourceSearch = "";
        TargetWorkOrderNo = null;
        SourceSkip = 0;
        SourceTotal = 0;
        TargetSkip = 0;
        TargetTotal = 0;
        SourceLoading = false;
        TargetLoading = false;
        TargetPickerVisible = false;
        ChangeSourceConfirmVisible = false;
        PostConfirmVisible = false;
        RollbackConfirmVisible = false;
        DeleteConfirmVisible = false;
        RollbackReason = "";
        _pendingSourceChange = "";
        _pendingPost = null;
        _pendingRollback = null;
        _targetPickerGeneration++;
        _cleanSnapshot = "";
    }

    private string CurrentSnapshot() => JsonSerializer.Serialize(new
    {
        SourceWorkOrderNo = SelectedSourceWorkOrder?.WorkOrderNo,
        SourceProductionBalLotId = SelectedSource?.ProductionBalLotId
            ?? (Id > 0 ? Document.SourceProductionBalLotId : 0),
        TargetWorkOrderMaterialId = SelectedTarget?.WorkOrderMaterialId ?? Document.TargetWorkOrderMaterialId,
        Document.TransferDateTime,
        Document.Remark
    });

    protected Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchSourceWorkOrdersAsync(
        LargeLookupSearchRequest request, CancellationToken ct) => Transfers.SearchSourceWorkOrdersAsync(request, ct);

    protected Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveSourceWorkOrderAsync(
        string workOrderNo, CancellationToken ct) => Transfers.ResolveSourceWorkOrderAsync(workOrderNo, ct);

    protected Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchTargetWorkOrdersAsync(
        LargeLookupSearchRequest request, CancellationToken ct)
    {
        var sourceId = SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId;
        return sourceId > 0
            ? Transfers.SearchTargetWorkOrdersAsync(sourceId, request, ct)
            : Task.FromResult(LargeLookupPage<ProductionStockWorkOrderLookupRow>.Fail("Choose an exact source WIP balance first."));
    }

    protected Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveTargetWorkOrderAsync(
        string workOrderNo, CancellationToken ct)
    {
        var sourceId = SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId;
        return sourceId > 0
            ? Transfers.ResolveTargetWorkOrderAsync(sourceId, workOrderNo, ct)
            : Task.FromResult(LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail("Choose an exact source WIP balance first."));
    }

    protected async Task OnSourceWorkOrderSelectedAsync(ProductionStockWorkOrderLookupRow row)
    {
        if (!CanEditFields || Id != 0 || Busy) return;
        SelectedSourceWorkOrder = row;
        SourceSearch = "";
        SourceSkip = 0;
        SourceTotal = 0;
        Sources = [];
        SelectedSource = null;
        ResetTargetSelection(clearCurrent: true);
        await LoadSourcesAsync(debounce: false);
    }

    protected async Task OnSourceWorkOrderClearedAsync()
    {
        if (SelectedSourceWorkOrder is not null)
            await RequestSourceChangeAsync("work-order");
    }

    protected async Task SourceSearchChanged(string value)
    {
        SourceSearch = value ?? "";
        SourceSkip = 0;
        await LoadSourcesAsync(debounce: true);
    }

    private async Task LoadSourcesAsync(bool debounce)
    {
        if (SelectedSourceWorkOrder is null || SelectedSource is not null || Id != 0) return;
        CancelSourceLoad();
        var cts = new CancellationTokenSource();
        _sourceLoadCts = cts;
        var sequence = Interlocked.Increment(ref _sourceLoadSequence);
        SourceLoading = true;
        Error = "";
        try
        {
            if (debounce) await Task.Delay(350, cts.Token);
            var result = await Transfers.SearchEligibleSourcesAsync(new ProductionWorkOrderTransferSourceQuery
            {
                WorkOrderNo = SelectedSourceWorkOrder.WorkOrderNo,
                SearchText = string.IsNullOrWhiteSpace(SourceSearch) ? null : SourceSearch.Trim(),
                Skip = SourceSkip,
                Take = LookupPageSize
            }, cts.Token);
            if (cts.IsCancellationRequested || sequence != _sourceLoadSequence) return;
            if (!result.Succeeded || result.Data is null)
            {
                Error = result.Message ?? "Unable to load WIP balances for this Work Order.";
                Sources = [];
                SourceTotal = 0;
            }
            else
            {
                Sources = result.Data.Rows.ToList();
                SourceTotal = result.Data.TotalCount;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && sequence == _sourceLoadSequence)
            {
                Error = ex.Message;
                Sources = [];
                SourceTotal = 0;
            }
        }
        finally
        {
            if (sequence == _sourceLoadSequence) SourceLoading = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    protected Task PreviousSourcesAsync()
    {
        SourceSkip = Math.Max(0, SourceSkip - LookupPageSize);
        return LoadSourcesAsync(debounce: false);
    }

    protected Task NextSourcesAsync()
    {
        if (SourceSkip + LookupPageSize >= SourceTotal) return Task.CompletedTask;
        SourceSkip += LookupPageSize;
        return LoadSourcesAsync(debounce: false);
    }

    protected async Task SelectSourceAsync(ProductionWorkOrderTransferSource source)
    {
        if (!CanEditFields || Id != 0 || Busy) return;
        CancelSourceLoad();
        SourceLoading = false;
        SelectedSource = source;
        Document.CanViewCost = CanViewCostAccess;
        Document.TransferTotalCost = CanViewCostAccess ? source.TotalCost : null;
        ResetTargetSelection(clearCurrent: true);
        TargetPickerVisible = true;
        _targetPickerGeneration++;
        await InvokeAsync(StateHasChanged);
    }

    protected Task RequestChangeSourceWorkOrder() => RequestSourceChangeAsync("work-order");
    protected Task RequestChangeWipBalance() => RequestSourceChangeAsync("wip");

    private async Task RequestSourceChangeAsync(string kind)
    {
        if (!CanEditFields || Id != 0 || Busy) return;
        if (SelectedSource is not null || SelectedTarget is not null
            || Document.TransferDateTime != _baselineTransferDateTime
            || !string.Equals(Document.Remark, _baselineRemark, StringComparison.Ordinal))
        {
            _pendingSourceChange = kind;
            ChangeSourceConfirmVisible = true;
            return;
        }
        await ApplySourceChangeAsync(kind);
    }

    protected void CancelSourceChange()
    {
        ChangeSourceConfirmVisible = false;
        _pendingSourceChange = "";
    }

    protected async Task ConfirmSourceChange()
    {
        if (string.IsNullOrWhiteSpace(_pendingSourceChange)) return;
        await ApplySourceChangeAsync(_pendingSourceChange);
        _pendingSourceChange = "";
        ChangeSourceConfirmVisible = false;
    }

    private async Task ApplySourceChangeAsync(string kind)
    {
        CancelSourceLoad();
        CancelTargetLoad();
        if (string.Equals(kind, "work-order", StringComparison.Ordinal))
        {
            SelectedSourceWorkOrder = null;
            SourceSearch = "";
            SourceSkip = 0;
            SourceTotal = 0;
            Sources = [];
            SelectedSource = null;
        }
        else
        {
            SelectedSource = null;
            SourceSkip = 0;
            SourceTotal = 0;
            Sources = [];
        }
        Document.TransferTotalCost = null;
        ResetTargetSelection(clearCurrent: true);
        if (string.Equals(kind, "wip", StringComparison.Ordinal))
            await LoadSourcesAsync(debounce: false);
    }

    private void ResetTargetSelection(bool clearCurrent)
    {
        CancelTargetLoad();
        CandidateTargetWorkOrder = null;
        TargetWorkOrderNo = null;
        Targets = [];
        TargetSkip = 0;
        TargetTotal = 0;
        if (clearCurrent)
        {
            SelectedTarget = null;
            if (Id == 0)
            {
                Document.TargetWorkOrderId = 0;
                Document.TargetWorkOrderNo = "";
                Document.TargetProductCode = "";
                Document.TargetProductDescription = null;
                Document.TargetWorkOrderStatus = "";
                Document.TargetWorkOrderMaterialId = 0;
                Document.TargetConsumingWorkCentreCode = "";
                Document.TargetConsumingProcessCode = "";
                Document.TargetProducerWorkCentreCode = "";
                Document.TargetProducerProcessCode = "";
                Document.TargetCurrentWipValue = null;
                Document.ProjectedTargetWipValue = null;
                Document.TargetSequenceReady = true;
                Document.TargetSequenceBlockingReason = null;
            }
        }
        TargetPickerVisible = false;
        _targetPickerGeneration++;
    }

    protected async Task OnTargetWorkOrderSelectedAsync(ProductionStockWorkOrderLookupRow row)
    {
        if (!CanEditFields || Busy) return;
        CandidateTargetWorkOrder = row;
        TargetWorkOrderNo = row.WorkOrderNo;
        TargetSkip = 0;
        TargetTotal = 0;
        Targets = [];
        await LoadTargetsAsync(debounce: false);
    }

    protected Task OnTargetWorkOrderClearedAsync()
    {
        CancelTargetLoad();
        CandidateTargetWorkOrder = null;
        TargetWorkOrderNo = null;
        Targets = [];
        TargetSkip = 0;
        TargetTotal = 0;
        TargetLoading = false;
        return Task.CompletedTask;
    }

    private async Task LoadTargetsAsync(bool debounce)
    {
        var sourceId = SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId;
        if (sourceId <= 0 || string.IsNullOrWhiteSpace(TargetWorkOrderNo)) return;
        CancelTargetLoad();
        var cts = new CancellationTokenSource();
        _targetLoadCts = cts;
        var sequence = Interlocked.Increment(ref _targetLoadSequence);
        TargetLoading = true;
        Error = "";
        try
        {
            if (debounce) await Task.Delay(250, cts.Token);
            var requestedWorkOrderNo = TargetWorkOrderNo.Trim();
            var result = await Transfers.SearchEligibleTargetsAsync(sourceId,
                new ProductionWorkOrderTransferTargetQuery
                {
                    WorkOrderNo = requestedWorkOrderNo,
                    Skip = TargetSkip,
                    Take = LookupPageSize
                }, cts.Token);
            if (cts.IsCancellationRequested || sequence != _targetLoadSequence
                || sourceId != (SelectedSource?.ProductionBalLotId ?? Document.SourceProductionBalLotId)
                || !string.Equals(requestedWorkOrderNo, TargetWorkOrderNo, StringComparison.OrdinalIgnoreCase)) return;
            if (!result.Succeeded || result.Data is null)
            {
                Error = result.Message ?? "Unable to load compatible requirements for this Work Order.";
                Targets = [];
                TargetTotal = 0;
            }
            else
            {
                Targets = result.Data.Rows.ToList();
                TargetTotal = result.Data.TotalCount;
                if (TargetTotal == 1 && Targets.Count == 1)
                    SelectTarget(Targets[0]);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && sequence == _targetLoadSequence)
            {
                Error = ex.Message;
                Targets = [];
                TargetTotal = 0;
            }
        }
        finally
        {
            if (sequence == _targetLoadSequence) TargetLoading = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    protected Task PreviousTargetsAsync()
    {
        TargetSkip = Math.Max(0, TargetSkip - LookupPageSize);
        return LoadTargetsAsync(debounce: false);
    }

    protected Task NextTargetsAsync()
    {
        if (TargetSkip + LookupPageSize >= TargetTotal) return Task.CompletedTask;
        TargetSkip += LookupPageSize;
        return LoadTargetsAsync(debounce: false);
    }

    protected void SelectTarget(ProductionWorkOrderTransferTarget target)
    {
        if (!CanEditFields || Busy) return;
        SelectedTarget = target;
        TargetWorkOrderNo = target.WorkOrderNo;
        Document.TargetWorkOrderId = target.WorkOrderId;
        Document.TargetWorkOrderNo = target.WorkOrderNo;
        Document.TargetProductCode = target.ProductCode;
        Document.TargetProductDescription = target.ProductDescription;
        Document.TargetWorkOrderStatus = target.Status;
        Document.TargetWorkOrderMaterialId = target.WorkOrderMaterialId;
        Document.TargetConsumingWorkCentreCode = target.ConsumingWorkCentreCode;
        Document.TargetConsumingProcessCode = target.ConsumingProcessCode;
        Document.TargetProducerWorkCentreCode = target.ProducerWorkCentreCode;
        Document.TargetProducerProcessCode = target.ProducerProcessCode;
        Document.TargetCurrentWipValue = CanViewCostAccess ? target.TargetCurrentWipValue : null;
        Document.ProjectedTargetWipValue = CanViewCostAccess ? target.ProjectedTargetWipValue : null;
        Document.TargetSequenceReady = target.SequenceReady;
        Document.TargetSequenceBlockingReason = target.BlockingReason;
        TargetPickerVisible = false;
        _pendingPost = null;
    }

    protected async Task OpenTargetPickerAsync()
    {
        if (!CanEditFields || Busy || SelectedSource is null && Document.SourceProductionBalLotId <= 0) return;
        CancelTargetLoad();
        TargetPickerVisible = true;
        CandidateTargetWorkOrder = null;
        TargetWorkOrderNo = null;
        Targets = [];
        TargetSkip = 0;
        TargetTotal = 0;
        TargetLoading = false;
        _targetPickerGeneration++;
        await InvokeAsync(StateHasChanged);
    }

    protected void CancelTargetPicker()
    {
        if (Id == 0 || !TargetPickerVisible) return;
        CancelTargetLoad();
        CandidateTargetWorkOrder = null;
        TargetWorkOrderNo = SelectedTarget?.WorkOrderNo;
        Targets = [];
        TargetSkip = 0;
        TargetTotal = 0;
        TargetLoading = false;
        TargetPickerVisible = false;
        _targetPickerGeneration++;
    }

    protected async Task SaveAsync()
    {
        if (Busy || !CanSave) return;
        Busy = true;
        Error = "";
        Message = "";
        try
        {
            if (Id == 0)
            {
                var result = await Transfers.CreateAsync(new ProductionWorkOrderTransferCreateRequest
                {
                    SourceProductionBalLotId = SelectedSource!.ProductionBalLotId,
                    TargetWorkOrderMaterialId = SelectedTarget!.WorkOrderMaterialId,
                    TransferDateTime = Document.TransferDateTime,
                    Remark = Document.Remark
                });
                if (!result.Succeeded || result.Data is null)
                    throw new InvalidOperationException(result.Message ?? "Unable to save the transfer.");
                Document = result.Data;
                Id = Document.Id;
                Mode = "edit";
                _pendingPost = null;
                _pendingRollback = null;
                Message = "Work Order Transfer saved as NEW.";
                SetCleanSnapshot();
                Nav.NavigateTo($"/planning/work-order-transfer/{Id}/edit", replace: true);
            }
            else
            {
                var result = await Transfers.UpdateAsync(new ProductionWorkOrderTransferUpdateRequest
                {
                    Id = Document.Id,
                    ExpectedVersion = Document.RowVersion,
                    TargetWorkOrderMaterialId = Document.TargetWorkOrderMaterialId,
                    TransferDateTime = Document.TransferDateTime,
                    Remark = Document.Remark
                });
                if (!result.Succeeded || result.Data is null)
                    throw new InvalidOperationException(result.Message ?? "Unable to update the transfer.");
                Document = result.Data;
                SelectedTarget = null;
                Message = "Work Order Transfer updated.";
                _pendingPost = null;
                _pendingRollback = null;
                SetCleanSnapshot();
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected async Task RefreshSourceAsync()
    {
        if (Busy || Document.Id <= 0 || !CanEditFields) return;
        Busy = true;
        Error = "";
        try
        {
            var result = await Transfers.RefreshSourceSnapshotAsync(Document.Id, Document.RowVersion);
            if (!result.Succeeded || result.Data is null)
                throw new InvalidOperationException(result.Message ?? "Unable to refresh source WIP.");
            Document = result.Data;
            SelectedTarget = null;
            Message = "Source WIP quantity and value snapshot refreshed.";
            _pendingPost = null;
            SetCleanSnapshot();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected void RequestDelete()
    {
        if (CanDelete && !Busy) DeleteConfirmVisible = true;
    }

    protected async Task DeleteAsync()
    {
        if (!CanDelete || Busy) return;
        Busy = true;
        Error = "";
        try
        {
            var result = await Transfers.DeleteAsync(Document.Id, Document.RowVersion);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Message ?? "Unable to delete the transfer.");
            _cleanSnapshot = CurrentSnapshot();
            Nav.NavigateTo("/planning/work-order-transfer");
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; DeleteConfirmVisible = false; }
    }

    protected void EditDraft() => Nav.NavigateTo($"/planning/work-order-transfer/{Id}/edit");

    protected void RequestPost()
    {
        if (!ShowPost || Busy) return;
        if (Dirty)
        {
            Error = "Save the draft changes before posting.";
            return;
        }
        _pendingPost ??= new ProductionWorkOrderTransferCommand(Id, Document.RowVersion.ToArray(), Guid.NewGuid());
        PostConfirmVisible = true;
    }

    protected async Task PostAsync()
    {
        if (!ShowPost || Busy || Dirty) return;
        Busy = true;
        Error = "";
        try
        {
            _pendingPost ??= new ProductionWorkOrderTransferCommand(Id, Document.RowVersion.ToArray(), Guid.NewGuid());
            var result = await Transfers.PostAsync(_pendingPost);
            if (!result.Succeeded || result.Data is null)
                throw new InvalidOperationException(result.Message ?? "Unable to post the transfer.");
            Document = result.Data;
            Mode = "view";
            Message = "Work Order Transfer posted.";
            PostConfirmVisible = false;
            _pendingPost = null;
            SetCleanSnapshot();
            Nav.NavigateTo($"/planning/work-order-transfer/{Id}/view", replace: true);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected void RequestRollback()
    {
        if (!CanRollback || Busy) return;
        if (_pendingRollback is null) RollbackReason = "";
        _pendingRollback ??= new ProductionWorkOrderTransferCommand(Id, Document.RowVersion.ToArray(), Guid.NewGuid());
        RollbackConfirmVisible = true;
    }

    protected void RollbackReasonChanged(string value)
    {
        RollbackReason = value ?? "";
        if (_pendingRollback is not null) _pendingRollback = null;
    }

    protected async Task RollbackAsync()
    {
        if (!CanRollback || Busy) return;
        var reason = RollbackReason.Trim();
        if (reason.Length == 0 || reason.Length > 500)
        {
            Error = "Enter a rollback reason of at most 500 characters.";
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            _pendingRollback ??= new ProductionWorkOrderTransferCommand(Id, Document.RowVersion.ToArray(), Guid.NewGuid(), reason);
            if (!string.Equals(_pendingRollback.Reason, reason, StringComparison.Ordinal))
                _pendingRollback = new ProductionWorkOrderTransferCommand(Id, Document.RowVersion.ToArray(), Guid.NewGuid(), reason);
            var result = await Transfers.RollbackAsync(_pendingRollback);
            if (!result.Succeeded || result.Data is null)
                throw new InvalidOperationException(result.Message ?? "Unable to roll back the transfer.");
            Document = result.Data;
            Mode = CanEdit ? "edit" : "view";
            Message = "Work Order Transfer rolled back.";
            RollbackConfirmVisible = false;
            _pendingRollback = null;
            RollbackReason = "";
            SetCleanSnapshot();
            Nav.NavigateTo($"/planning/work-order-transfer/{Document.Id}/{Mode}", replace: true);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    private void SetCleanSnapshot()
    {
        _baselineTransferDateTime = Document.TransferDateTime;
        _baselineRemark = Document.Remark;
        _cleanSnapshot = CurrentSnapshot();
    }

    protected async Task BeforeNavigate(LocationChangingContext context)
    {
        if (Dirty && !await Js.InvokeAsync<bool>("confirm", "Discard unsaved Work Order Transfer changes?"))
            context.PreventNavigation();
    }

    protected void BackToList() => Nav.NavigateTo("/planning/work-order-transfer");

    private void CancelSourceLoad()
    {
        Interlocked.Increment(ref _sourceLoadSequence);
        try { _sourceLoadCts?.Cancel(); } catch (ObjectDisposedException) { }
        _sourceLoadCts?.Dispose();
        _sourceLoadCts = null;
    }

    private void CancelTargetLoad()
    {
        Interlocked.Increment(ref _targetLoadSequence);
        try { _targetLoadCts?.Cancel(); } catch (ObjectDisposedException) { }
        _targetLoadCts?.Dispose();
        _targetLoadCts = null;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        CancelSourceLoad();
        CancelTargetLoad();
        return ValueTask.CompletedTask;
    }
}
