using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Common.OperatorError;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrProductionStockReturnEntry : IAsyncDisposable
{
    private const int SourcePageSize = 20;

    /// <summary>Unexpected failure wording. Never the raw exception, and never a claim that nothing was written.</summary>
    private const string UnexpectedPostMessage =
        "The response could not be received. Reload the return to confirm whether it posted before retrying.";
    private const string UnexpectedRollbackMessage =
        "The response could not be received. Reload the return to confirm its status before retrying.";

    [Parameter] public int Id { get; set; }
    [Inject] private IProductionStockReturnService Returns { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private string? _loadedKey;
    private string _saved = "";
    private DateTime _initialEffectiveDate;
    private ProductionStockReturnCommand? _pending;
    private CancellationTokenSource? _sourceSearchCts;
    private long _sourceSearchVersion;

    protected ProductionStockReturnDocument Document = new() { EffectiveDate = DateTime.Today, Status = "NEW" };
    protected List<ProductionStockReturnSource> Sources = [];
    protected List<ReturnEditor> Editors = [];
    protected string SourceSearch = string.Empty;
    protected string RollbackReason = "";

    /// <summary>Operator-language view of the current failed post/rollback, or null when the failure
    /// carried no structured error (then the existing message is shown unchanged).</summary>
    protected ErpOperatorFailureView? OperatorFailure { get; private set; }
    protected bool BusyLoading = true, Busy, CanAdd, CanEdit, CanDeleteAccess, CanPostAccess, CanRollbackAccess;
    protected bool RollbackVisible, SourcePickerVisible, SourceLoading, ChangeWorkOrderConfirmVisible, PostConfirmVisible, DeleteConfirmVisible;
    protected int SourcePage, SourceTotalCount;
    protected string? StatusMessage;
    protected IReadOnlyList<string> ReasonOptions { get; } = ProductionStockReturnReasons.All;
    protected bool IsView => Navigation.RelativePath.Contains("/view", StringComparison.OrdinalIgnoreCase) || Id > 0 && Document.Status != "NEW";
    protected bool IsEditRequested => Navigation.RelativePath.Contains("/edit", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditFields => !IsView && Document.Status == "NEW" && (Id == 0 ? CanAdd : CanEdit);
    protected bool Dirty => CanEditFields && _saved.Length > 0 && _saved != JsonSerializer.Serialize(ToRequest());
    protected bool CanSave => CanEditFields && GetUiIssues().Count == 0;
    protected bool ShowPost => Id > 0 && Document.Status == "NEW" && CanPostAccess;
    protected bool CanRollback => Id > 0 && Document.Status == "POSTED" && CanRollbackAccess;
    protected bool CanDelete => Id > 0 && Document.Status == "NEW" && CanDeleteAccess;
    protected bool CanEditDraft => Id > 0 && Document.Status == "NEW" && IsView && CanEdit;
    protected bool CanChangeWorkOrder => CanEditFields && Id == 0 && Document.WorkOrderId > 0;
    protected bool CanAddMoreSources => Editors.Count < 100;
    protected int SourceTotalPages => Math.Max(1, (int)Math.Ceiling(SourceTotalCount / (double)SourcePageSize));
    protected string SourcePageLabel => $"{SourceTotalCount:N0} source pools · page {Math.Min(SourcePage + 1, SourceTotalPages)} / {SourceTotalPages}";
    protected int LineCount => IsView ? Document.Lines.Count : Editors.Count;
    protected List<string> UiIssues => GetUiIssues();
    protected string Heading => Id == 0 ? "New Production Stock Return" : CanEditFields ? "Edit Production Stock Return" : "View Production Stock Return";

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{Id}|{Navigation.RelativePath}";
        if (_loadedKey == key) return;
        _loadedKey = key;
        CancelSourceSearch();
        ResetSourcePickerState();
        Editors = [];
        Document = new() { EffectiveDate = Clock.Now, Status = "NEW" };
        _saved = "";
        _pending = null;
        RollbackVisible = false;
        ChangeWorkOrderConfirmVisible = false;
        PostConfirmVisible = false;
        DeleteConfirmVisible = false;
        BusyLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        OperatorFailure = null;

        CanAdd = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Edit);
        CanDeleteAccess = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Delete);
        CanPostAccess = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Post);
        CanRollbackAccess = await Access.CanAsync(MenuCodes.PlanningStockReturn, PermissionCodes.Rollback);

        if (Id > 0)
        {
            var result = await Returns.GetAsync(Id);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message;
                BusyLoading = false;
                return;
            }

            Document = result.Data;
            if (IsEditRequested && Document.Status == "NEW" && !CanEdit)
            {
                Navigation.NavigateTo($"/planning/stock-returns/{Id}/view", replace: true);
                return;
            }

            Editors = Document.Lines.Select(line => new ReturnEditor(
                new ProductionStockReturnSource(line.ProductionBalLotId, line.SourceIssueMovementId, Document.WorkOrderNo,
                    line.ItemCode, line.ItemDescription, line.LotNo, line.ProductionQty, line.CurrentReturnableQty,
                    line.SourceUom, 0, line.Warehouse, line.Location, line.InventoryLotNo, line.ItemStatus, line.BaseUom,
                    line.ExpiryDate, "", line.CurrentReadiness, line.TotalValue),
                line.RequestedQty, line.Reason, line.Remarks)).ToList();
        }
        else
        {
            Document.EffectiveDate = Clock.Now;
            _initialEffectiveDate = Document.EffectiveDate;
        }

        _saved = JsonSerializer.Serialize(ToRequest());
        BusyLoading = false;
    }

    private Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveWorkOrderAsync(string text, CancellationToken ct) =>
        Returns.ResolveEligibleWorkOrderAsync(text, ct);

    private Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchWorkOrdersAsync(LargeLookupSearchRequest request, CancellationToken ct) =>
        Returns.SearchEligibleWorkOrdersAsync(request, ct);

    protected async Task OnWorkOrderSelectedAsync(ProductionStockWorkOrderLookupRow row)
    {
        if (!CanEditFields || Id != 0) return;
        CancelSourceSearch();
        Document.WorkOrderId = row.WorkOrderId;
        Document.WorkOrderNo = row.WorkOrderNo;
        Document.ProductCode = row.ProductCode;
        Document.ProductDescription = row.ProductDescription;
        Document.WorkOrderStatus = row.Status;
        Document.SnapshotRevision = row.SnapshotRevision;
        Document.SnapshotHash = row.SnapshotHash;
        Editors.Clear();
        ResetSourcePickerState();
        SourcePickerVisible = true;
        await LoadSourcesAsync(debounce: false);
    }

    protected Task OnWorkOrderClearedAsync()
    {
        if (Document.WorkOrderId > 0) RequestChangeWorkOrder();
        return Task.CompletedTask;
    }

    protected void RequestChangeWorkOrder()
    {
        if (!CanChangeWorkOrder) return;
        if (HasMeaningfulEntry())
        {
            ChangeWorkOrderConfirmVisible = true;
            return;
        }
        ClearSelectedWorkOrder();
    }

    protected void ConfirmChangeWorkOrder()
    {
        ChangeWorkOrderConfirmVisible = false;
        ClearSelectedWorkOrder();
    }

    private bool HasMeaningfulEntry() => Editors.Count > 0
        || !string.IsNullOrWhiteSpace(Document.RefNo)
        || !string.IsNullOrWhiteSpace(Document.Remarks)
        || Document.EffectiveDate.Date != _initialEffectiveDate.Date;

    private void ClearSelectedWorkOrder()
    {
        CancelSourceSearch();
        Document.WorkOrderId = 0;
        Document.WorkOrderNo = "";
        Document.ProductCode = "";
        Document.ProductDescription = null;
        Document.WorkOrderStatus = "";
        Document.SnapshotRevision = 0;
        Document.SnapshotHash = "";
        Editors.Clear();
        ResetSourcePickerState();
    }

    private void ResetSourcePickerState()
    {
        SourceSearch = "";
        SourcePage = 0;
        SourceTotalCount = 0;
        Sources = [];
        SourceLoading = false;
        SourcePickerVisible = false;
    }

    protected Task OnSourceSearchChanged(string value)
    {
        SourceSearch = value ?? "";
        SourcePage = 0;
        return LoadSourcesAsync(debounce: true);
    }

    /// <summary>
    /// Magnifier action: cancels the pending 350 ms debounce and loads the latest filter once.
    /// </summary>
    protected Task ApplySourceSearchAsync()
    {
        SourcePage = 0;
        return LoadSourcesAsync(debounce: false);
    }

    protected async Task OpenSourcePickerAsync()
    {
        if (!CanEditFields || Busy || !CanAddMoreSources || Document.WorkOrderId <= 0) return;

        // Start with an unfiltered list each time the picker opens, then refresh its availability.
        SourceSearch = string.Empty;
        SourcePage = 0;
        SourcePickerVisible = true;
        await LoadSourcesAsync(debounce: false);
    }

    protected Task PreviousSourcePageAsync()
    {
        SourcePage = Math.Max(0, SourcePage - 1);
        return LoadSourcesAsync(debounce: false);
    }

    protected Task NextSourcePageAsync()
    {
        if (SourcePage + 1 >= SourceTotalPages) return Task.CompletedTask;
        SourcePage++;
        return LoadSourcesAsync(debounce: false);
    }

    private async Task LoadSourcesAsync(bool debounce)
    {
        if (Document.WorkOrderId <= 0) return;
        CancelSourceSearch();
        var cts = new CancellationTokenSource();
        _sourceSearchCts = cts;
        var version = Interlocked.Increment(ref _sourceSearchVersion);
        SourceLoading = true;
        ErrorMessage = null;
        try
        {
            if (debounce) await Task.Delay(400, cts.Token);
            var result = await Returns.SearchSourcesAsync(new ProductionStockReturnSourceQuery
            {
                WorkOrderId = Document.WorkOrderId,
                SearchText = string.IsNullOrWhiteSpace(SourceSearch) ? null : SourceSearch.Trim(),
                Skip = SourcePage * SourcePageSize,
                Take = SourcePageSize
            }, cts.Token);
            if (cts.IsCancellationRequested || version != _sourceSearchVersion) return;
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message;
                Sources = [];
                SourceTotalCount = 0;
                return;
            }
            Sources = result.Data.Rows.ToList();
            SourceTotalCount = result.Data.TotalCount;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && version == _sourceSearchVersion)
            {
                ErrorMessage = ex.Message;
                Sources = [];
                SourceTotalCount = 0;
            }
        }
        finally
        {
            if (version == _sourceSearchVersion)
            {
                SourceLoading = false;
                if (ReferenceEquals(_sourceSearchCts, cts)) _sourceSearchCts = null;
            }
            cts.Dispose();
        }
    }

    private void CancelSourceSearch()
    {
        Interlocked.Increment(ref _sourceSearchVersion);
        try { _sourceSearchCts?.Cancel(); } catch (ObjectDisposedException) { }
        _sourceSearchCts?.Dispose();
        _sourceSearchCts = null;
    }

    protected void AddSource(ProductionStockReturnSource source)
    {
        if (!CanEditFields || Editors.Count >= 100 || source.Readiness != "READY" || source.ReturnableQty <= 0m
            || Editors.Any(x => x.Source.ProductionBalLotId == source.ProductionBalLotId)) return;
        Editors.Add(new ReturnEditor(source, 0m, ProductionStockReturnReasons.Excess, ""));
    }

    protected bool IsSourceAdded(long poolId) => Editors.Any(x => x.Source.ProductionBalLotId == poolId);
    protected bool SourceCanBeAdded(ProductionStockReturnSource source) => source.Readiness == "READY" && source.ReturnableQty > 0m;
    protected void RemoveEditor(ReturnEditor editor) { if (CanEditFields) Editors.Remove(editor); }
    protected decimal ProjectedAfterReturn(ReturnEditor editor) => editor.Source.ProductionQty - editor.Quantity;

    protected async Task SaveAsync()
    {
        if (!CanSave || Busy) return;
        Busy = true;
        ErrorMessage = null;
        try
        {
            var result = await Returns.SaveAsync(ToRequest());
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message);
            Document = result.Data;
            _saved = JsonSerializer.Serialize(ToRequest());
            Navigation.NavigateTo($"/planning/stock-returns/{Document.Id}/view", replace: true);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; }
    }

    protected void DismissOperatorFailure()
    {
        OperatorFailure = null;
        ErrorMessage = null;
    }

    protected void BeginPost()
    {
        if (!ShowPost) { StatusMessage = "Access denied."; return; }
        if (Dirty) { ErrorMessage = "Save the draft before posting."; return; }
        PostConfirmVisible = true;
    }
    protected async Task ConfirmPostAsync() { PostConfirmVisible = false; await PostAsync(); }

    private async Task PostAsync()
    {
        if (!ShowPost || Busy) return;
        if (Dirty) { ErrorMessage = "Save the draft before posting."; return; }
        Busy = true;
        ErrorMessage = null;
        OperatorFailure = null;
        try
        {
            _pending ??= new(Document.Id, Document.RowVersion, Guid.NewGuid());
            var result = await Returns.PostAsync(_pending);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to post the Production Stock Return.";
                OperatorFailure = ErpOperatorFailureView.FromError(result.OperatorError);
                return;
            }
            Document = result.Data;
            _pending = null;
            StatusMessage = "Production Stock Return posted.";
        }
        catch (Exception)
        {
            ErrorMessage = UnexpectedPostMessage;
        }
        finally { Busy = false; }
    }

    protected void BeginRollback() => RollbackVisible = true;

    protected async Task RollbackAsync()
    {
        if (!CanRollback || string.IsNullOrWhiteSpace(RollbackReason) || Busy)
        {
            ErrorMessage = "Rollback reason is required.";
            return;
        }
        Busy = true;
        ErrorMessage = null;
        OperatorFailure = null;
        try
        {
            _pending ??= new(Document.Id, Document.RowVersion, Guid.NewGuid(), RollbackReason.Trim());
            var result = await Returns.RollbackAsync(_pending);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to roll back the Production Stock Return.";
                OperatorFailure = ErpOperatorFailureView.FromError(result.OperatorError);
                return;
            }
            Document = result.Data;
            _pending = null;
            RollbackVisible = false;
            StatusMessage = "Production Stock Return reopened as NEW after rollback.";
        }
        catch (Exception)
        {
            ErrorMessage = UnexpectedRollbackMessage;
        }
        finally { Busy = false; }
    }

    protected void BeginDelete() { if (CanDelete) DeleteConfirmVisible = true; }

    protected async Task ConfirmDeleteAsync()
    {
        if (!CanDelete || Busy) return;
        Busy = true;
        try
        {
            var result = await Returns.DeleteAsync(Id, Document.RowVersion);
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
            Navigation.NavigateTo("/planning/stock-returns");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; DeleteConfirmVisible = false; }
    }

    protected void EditDraft() => Navigation.NavigateTo($"/planning/stock-returns/{Id}/edit");

    protected async Task BeforeNavigate(LocationChangingContext context)
    {
        if (Dirty && !await Js.InvokeAsync<bool>("confirm", "Discard unsaved Production Stock Return changes?"))
            context.PreventNavigation();
    }

    protected Task BackAsync() { Navigation.NavigateTo("/planning/stock-returns"); return Task.CompletedTask; }

    protected List<string> GetUiIssues()
    {
        var issues = new List<string>();
        if (Document.WorkOrderId <= 0) issues.Add("Select a Work Order.");
        if (Editors.Count == 0) issues.Add("Add at least one material line.");
        if (Editors.Count > 100) issues.Add("A document can contain at most 100 lines.");
        if (Document.RefNo?.Length > 30) issues.Add("Reference is limited to 30 characters.");
        if (Document.Remarks?.Length > 200) issues.Add("Header remarks are limited to 200 characters.");
        if (Document.EffectiveDate.Date > Clock.Now.Date) issues.Add("Effective Date cannot be in the future.");
        foreach (var line in Editors)
        {
            var label = line.Source.ItemCode;
            if (line.Source.Readiness != "READY") issues.Add($"{label}: {line.Source.Readiness}");
            if (line.Quantity <= 0m) issues.Add($"{label}: Return Qty must be greater than zero.");
            if (line.Quantity > line.Source.ReturnableQty) issues.Add($"{label}: Return Qty exceeds current returnable quantity.");
            if (IvQty.Round(line.Quantity) != line.Quantity) issues.Add($"{label}: Return Qty supports at most four decimal places.");
            if (!ReasonOptions.Contains(line.Reason, StringComparer.Ordinal)) issues.Add($"{label}: Select a valid reason.");
            if (line.Reason == ProductionStockReturnReasons.Other && string.IsNullOrWhiteSpace(line.Remarks)) issues.Add($"{label}: Remark required for OTHER.");
            if (line.Remarks?.Length > 250) issues.Add($"{label}: line remarks are limited to 250 characters.");
        }
        return issues;
    }

    private ProductionStockReturnSaveRequest ToRequest() => new()
    {
        Id = Document.Id,
        ExpectedVersion = Document.RowVersion,
        WorkOrderId = Document.WorkOrderId,
        SnapshotRevision = Document.SnapshotRevision,
        SnapshotHash = Document.SnapshotHash,
        EffectiveDate = Document.EffectiveDate,
        RefNo = Document.RefNo,
        Remarks = Document.Remarks,
        Lines = Editors.Select(line => new ProductionStockReturnLineInput
        {
            ProductionBalLotId = line.Source.ProductionBalLotId,
            Quantity = line.Quantity,
            Reason = line.Reason,
            Remarks = line.Remarks
        }).ToList()
    };

    public ValueTask DisposeAsync() { CancelSourceSearch(); return ValueTask.CompletedTask; }

    protected sealed class ReturnEditor(ProductionStockReturnSource source, decimal quantity, string reason, string? remarks)
    {
        public ProductionStockReturnSource Source { get; } = source;
        public long ProductionBalLotId => Source.ProductionBalLotId;
        public decimal Quantity { get; set; } = quantity;
        public string Reason { get; set; } = reason;
        public string? Remarks { get; set; } = remarks;
    }
}
