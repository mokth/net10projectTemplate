using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrProductionStockAdjustmentEntry : IAsyncDisposable
{
    private const int SourcePageSize = 20;
    [Parameter] public int Id { get; set; }
    [Inject] private IProductionStockAdjustmentService Adjustments { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private string? _loadedKey;
    private string _saved = "";
    private DateTime _initialEffectiveDate;
    private ProductionStockAdjustmentCommand? _pending;
    private CancellationTokenSource? _sourceSearchCts;
    private long _sourceSearchVersion;
    private AdjustmentEditor? _costEditor;
    private string? _costEvidenceDraft;
    private decimal? _proposedUnitCostDraft;
    private string? _costOverrideReasonDraft;
    protected string? CostPopupError;

    protected ProductionStockAdjustmentDocument Document = new() { EffectiveDate = DateTime.Today, Status = "NEW" };
    protected List<ProductionStockAdjustmentSource> Sources = [];
    protected List<AdjustmentEditor> Editors = [];
    protected string SourceSearch = "";
    protected string RollbackReason = "";
    protected bool BusyLoading = true, Busy, CanAdd, CanEdit, CanDeleteAccess, CanPostAccess, CanRollbackAccess, CanPriceOverride;
    protected bool RollbackVisible, SourcePickerVisible, SourceLoading, ChangeWorkOrderConfirmVisible, PostConfirmVisible, DeleteConfirmVisible, CostOverrideVisible;
    protected int SourcePage, SourceTotalCount;
    protected string? StatusMessage;
    protected IReadOnlyList<string> ReasonOptions { get; } = ProductionStockAdjustmentReasons.All;
    protected IReadOnlyList<string> DirectionOptions { get; } = ["ADD", "REDUCE"];
    protected IReadOnlyList<string> CostEvidenceOptions { get; } = [InventoryCostEvidenceTypes.ManualApproved, InventoryCostEvidenceTypes.ZeroCostApproved];
    protected bool IsView => Navigation.RelativePath.Contains("/view", StringComparison.OrdinalIgnoreCase) || Id > 0 && Document.Status != "NEW";
    protected bool IsEditRequested => Navigation.RelativePath.Contains("/edit", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditFields => !IsView && Document.Status == "NEW" && (Id == 0 ? CanAdd : CanEdit);
    protected bool Dirty => CanEditFields && _saved.Length > 0 && _saved != JsonSerializer.Serialize(ToRequest());
    protected bool CanSave => CanEditFields && GetUiIssues().Count == 0;
    protected bool ShowPost => Id > 0 && Document.Status == "NEW" && CanPostAccess;
    protected bool CanRollback => Id > 0 && Document.Status == "POSTED" && CanRollbackAccess;
    protected bool CanDelete => Id > 0 && Document.Status == "NEW" && CanDeleteAccess;
    protected bool CanCorrection => Id > 0 && Document.Status == "REVERSED" && CanAdd;
    protected bool CanEditDraft => Id > 0 && Document.Status == "NEW" && IsView && CanEdit;
    protected bool CanChangeWorkOrder => CanEditFields && Id == 0 && Document.WorkOrderId > 0;
    protected bool CanAddMoreSources => Editors.Count < 100;
    protected int SourceTotalPages => Math.Max(1, (int)Math.Ceiling(SourceTotalCount / (double)SourcePageSize));
    protected string SourcePageLabel => $"{SourceTotalCount:N0} source pools · page {Math.Min(SourcePage + 1, SourceTotalPages)} / {SourceTotalPages}";
    protected string Heading => Id == 0 ? "New Production Stock Adjustment" : CanEditFields ? "Edit Production Stock Adjustment" : "View Production Stock Adjustment";

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
        _costEditor = null;
        _costEvidenceDraft = null;
        _proposedUnitCostDraft = null;
        _costOverrideReasonDraft = null;
        CostPopupError = null;
        RollbackVisible = false;
        ChangeWorkOrderConfirmVisible = false;
        PostConfirmVisible = false;
        DeleteConfirmVisible = false;
        CostOverrideVisible = false;
        BusyLoading = true;
        ErrorMessage = null;
        StatusMessage = null;

        CanAdd = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.Edit);
        CanDeleteAccess = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.Delete);
        CanPostAccess = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.Post);
        CanRollbackAccess = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.Rollback);
        CanPriceOverride = await Access.CanAsync(MenuCodes.PlanningStockAdjustment, PermissionCodes.PriceOverride);

        if (Id > 0)
        {
            var result = await Adjustments.GetAsync(Id);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message;
                BusyLoading = false;
                return;
            }

            Document = result.Data;
            if (IsEditRequested && Document.Status == "NEW" && !CanEdit)
            {
                Navigation.NavigateTo($"/planning/stock-adjustments/{Id}/view", replace: true);
                return;
            }

            Editors = Document.Lines.Select(line => new AdjustmentEditor(
                new ProductionStockAdjustmentSource(line.ProductionBalLotId, Document.WorkOrderNo, line.ItemCode,
                    line.Description, line.PoolKind, line.LotNo, line.CurrentQty, line.Uom, line.CurrentBaseQty,
                    line.BaseUom, "Saved source", null, false),
                line.AdjustmentQty > 0m ? "ADD" : "REDUCE", Math.Abs(line.AdjustmentQty), line.Reason,
                line.Remarks, line.ProposedUnitCost, line.CostEvidenceType, line.CostOverrideReason)).ToList();
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
        Adjustments.ResolveEligibleWorkOrderAsync(text, ct);

    private Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchWorkOrdersAsync(LargeLookupSearchRequest request, CancellationToken ct) =>
        Adjustments.SearchEligibleWorkOrdersAsync(request, ct);

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
        if (HasMeaningfulEntry()) { ChangeWorkOrderConfirmVisible = true; return; }
        ClearSelectedWorkOrder();
    }

    protected void ConfirmChangeWorkOrder()
    {
        ChangeWorkOrderConfirmVisible = false;
        ClearSelectedWorkOrder();
    }

    private bool HasMeaningfulEntry() => Editors.Count > 0 || !string.IsNullOrWhiteSpace(Document.RefNo)
        || !string.IsNullOrWhiteSpace(Document.Remarks) || Document.EffectiveDate.Date != _initialEffectiveDate.Date;

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

    protected Task PreviousSourcePageAsync() { SourcePage = Math.Max(0, SourcePage - 1); return LoadSourcesAsync(debounce: false); }
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
            var result = await Adjustments.SearchSourcesAsync(new ProductionStockAdjustmentSourceQuery
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

    protected void AddSource(ProductionStockAdjustmentSource source)
    {
        if (!CanEditFields || Editors.Count >= 100 || source.Readiness != "READY"
            || Editors.Any(line => line.Source.ProductionBalLotId == source.ProductionBalLotId)) return;
        Editors.Add(new AdjustmentEditor(source, "ADD", 0m, ProductionStockAdjustmentReasons.Count, "", null, null, ""));
    }

    protected bool IsSourceAdded(long poolId) => Editors.Any(line => line.Source.ProductionBalLotId == poolId);
    protected void RemoveEditor(AdjustmentEditor editor) { if (CanEditFields) Editors.Remove(editor); }
    protected decimal ProjectedQty(AdjustmentEditor editor) => editor.Direction == "ADD"
        ? editor.Source.Qty + editor.InputQty
        : editor.Source.Qty - editor.InputQty;

    protected void SetDirection(AdjustmentEditor editor, string direction)
    {
        if (!CanEditFields || direction is not ("ADD" or "REDUCE")) return;
        editor.Direction = direction;
        if (direction == "REDUCE") ClearCostOverride(editor);
    }

    protected bool CanSetCostOverride(AdjustmentEditor editor) => CanEditFields && CanPriceOverride
        && editor.Direction == "ADD" && editor.Source.BaseQty == 0m && editor.Source.PoolKind == ProductionBalLotKinds.Wip;

    protected string CostStatus(AdjustmentEditor editor)
    {
        if (editor.CostEvidenceType == InventoryCostEvidenceTypes.ManualApproved) return "Manual override";
        if (editor.CostEvidenceType == InventoryCostEvidenceTypes.ZeroCostApproved) return "Approved zero cost";
        if (editor.Direction == "REDUCE") return "System — pooled cost";
        if (editor.Source.BaseQty > 0m) return "System — current pool cost";
        if (editor.Source.PoolKind == ProductionBalLotKinds.MaterialIn) return "System — original ISSUE cost";
        return "Automatic Standard Cost if configured";
    }

    protected void OpenCostOverride(AdjustmentEditor editor)
    {
        if (!CanSetCostOverride(editor)) return;
        _costEditor = editor;
        _costEvidenceDraft = editor.CostEvidenceType;
        _proposedUnitCostDraft = editor.ProposedUnitCost;
        _costOverrideReasonDraft = editor.CostOverrideReason;
        CostPopupError = null;
        CostOverrideVisible = true;
        ErrorMessage = null;
    }

    protected void ApplyCostOverride()
    {
        if (_costEditor is null || !CanSetCostOverride(_costEditor)) return;
        if (_costEvidenceDraft == InventoryCostEvidenceTypes.ManualApproved && _proposedUnitCostDraft is not > 0m)
        { CostPopupError = "Manual approved unit cost must be greater than zero."; return; }
        if (_costEvidenceDraft == InventoryCostEvidenceTypes.ZeroCostApproved && _proposedUnitCostDraft != 0m)
        { CostPopupError = "Zero cost approval requires an exact zero unit cost."; return; }
        if (string.IsNullOrWhiteSpace(_costEvidenceDraft) || !_costEvidenceDraft.Equals(InventoryCostEvidenceTypes.ManualApproved, StringComparison.Ordinal)
            && !_costEvidenceDraft.Equals(InventoryCostEvidenceTypes.ZeroCostApproved, StringComparison.Ordinal))
        { CostPopupError = "Choose Manual Approved or Zero Cost Approved."; return; }
        if (string.IsNullOrWhiteSpace(_costOverrideReasonDraft)) { CostPopupError = "Cost approval reason is required."; return; }
        if (_costOverrideReasonDraft.Length > 250) { CostPopupError = "Cost approval reason is limited to 250 characters."; return; }
        _costEditor.CostEvidenceType = _costEvidenceDraft;
        _costEditor.ProposedUnitCost = _proposedUnitCostDraft;
        _costEditor.CostOverrideReason = _costOverrideReasonDraft.Trim();
        CostOverrideVisible = false;
        CostPopupError = null;
        _costEditor = null;
    }

    protected void CancelCostOverride() { CostOverrideVisible = false; CostPopupError = null; _costEditor = null; }

    private static void ClearCostOverride(AdjustmentEditor editor)
    {
        editor.CostEvidenceType = null;
        editor.ProposedUnitCost = null;
        editor.CostOverrideReason = "";
    }

    protected async Task SaveAsync()
    {
        if (!CanSave || Busy) return;
        Busy = true;
        ErrorMessage = null;
        try
        {
            var result = await Adjustments.SaveAsync(ToRequest());
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message);
            Document = result.Data;
            _saved = JsonSerializer.Serialize(ToRequest());
            Navigation.NavigateTo($"/planning/stock-adjustments/{Document.Id}/view", replace: true);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; }
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
        try
        {
            _pending ??= new(Document.Id, Document.RowVersion, Guid.NewGuid());
            var result = await Adjustments.PostAsync(_pending);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message);
            Document = result.Data;
            _pending = null;
            StatusMessage = "Production Stock Adjustment posted.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; }
    }

    protected void BeginRollback() => RollbackVisible = true;

    protected async Task RollbackAsync()
    {
        if (!CanRollback || string.IsNullOrWhiteSpace(RollbackReason) || Busy)
        { ErrorMessage = "Rollback reason is required."; return; }
        Busy = true;
        try
        {
            _pending ??= new(Document.Id, Document.RowVersion, Guid.NewGuid(), RollbackReason.Trim());
            var result = await Adjustments.RollbackAsync(_pending);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message);
            Document = result.Data;
            _pending = null;
            RollbackVisible = false;
            StatusMessage = "Production Stock Adjustment reversed.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; }
    }

    protected void BeginDelete() { if (CanDelete) DeleteConfirmVisible = true; }

    protected async Task ConfirmDeleteAsync()
    {
        if (!CanDelete || Busy) return;
        Busy = true;
        try
        {
            var result = await Adjustments.DeleteAsync(Id, Document.RowVersion);
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
            Navigation.NavigateTo("/planning/stock-adjustments");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; DeleteConfirmVisible = false; }
    }

    protected async Task CreateCorrectionAsync()
    {
        if (!CanCorrection || Busy) return;
        Busy = true;
        try
        {
            var result = await Adjustments.CreateCorrectionAsync(Id);
            if (!result.Succeeded || result.Data is null) throw new InvalidOperationException(result.Message);
            Navigation.NavigateTo($"/planning/stock-adjustments/{result.Data.Id}/edit");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { Busy = false; }
    }

    protected void EditDraft() => Navigation.NavigateTo($"/planning/stock-adjustments/{Id}/edit");

    protected async Task BeforeNavigate(LocationChangingContext context)
    {
        if (Dirty && !await Js.InvokeAsync<bool>("confirm", "Discard unsaved Production Stock Adjustment changes?"))
            context.PreventNavigation();
    }

    protected Task BackAsync() { Navigation.NavigateTo("/planning/stock-adjustments"); return Task.CompletedTask; }

    protected List<string> GetUiIssues()
    {
        var issues = new List<string>();
        if (Document.WorkOrderId <= 0) issues.Add("Select a Work Order.");
        if (Editors.Count == 0) issues.Add("Add at least one Production pool line.");
        if (Editors.Count > 100) issues.Add("A document can contain at most 100 lines.");
        if (Document.RefNo?.Length > 30) issues.Add("Reference is limited to 30 characters.");
        if (Document.Remarks?.Length > 200) issues.Add("Header remarks are limited to 200 characters.");
        if (Document.EffectiveDate.Date > Clock.Now.Date) issues.Add("Effective Date cannot be in the future.");
        foreach (var line in Editors)
        {
            var label = line.Source.ItemCode;
            if (line.InputQty <= 0m) issues.Add($"{label}: Qty must be greater than zero.");
            if (IvQty.Round(line.InputQty) != line.InputQty) issues.Add($"{label}: Qty supports at most four decimal places.");
            if (line.Direction == "REDUCE" && ProjectedQty(line) < 0m) issues.Add($"{label}: projected Production quantity cannot be below zero.");
            if (!ReasonOptions.Contains(line.Reason, StringComparer.Ordinal)) issues.Add($"{label}: Select a valid reason.");
            if (line.Reason == ProductionStockAdjustmentReasons.Other && string.IsNullOrWhiteSpace(line.Remarks)) issues.Add($"{label}: Remark required for OTHER.");
            if (line.Remarks?.Length > 250) issues.Add($"{label}: line remarks are limited to 250 characters.");
            if (line.CostEvidenceType is not null)
            {
                if (!CanSetCostOverride(line)) issues.Add($"{label}: cost override is no longer applicable.");
                if (line.CostEvidenceType == InventoryCostEvidenceTypes.ManualApproved && line.ProposedUnitCost is not > 0m)
                    issues.Add($"{label}: manual approved unit cost must be greater than zero.");
                if (line.CostEvidenceType == InventoryCostEvidenceTypes.ZeroCostApproved && line.ProposedUnitCost != 0m)
                    issues.Add($"{label}: zero cost approval requires an exact zero unit cost.");
                if (string.IsNullOrWhiteSpace(line.CostOverrideReason)) issues.Add($"{label}: cost approval reason is required.");
                if (line.CostOverrideReason?.Length > 250) issues.Add($"{label}: cost approval reason is limited to 250 characters.");
            }
        }
        return issues;
    }

    private ProductionStockAdjustmentSaveRequest ToRequest() => new()
    {
        Id = Document.Id,
        ExpectedVersion = Document.RowVersion,
        WorkOrderId = Document.WorkOrderId,
        SnapshotRevision = Document.SnapshotRevision,
        SnapshotHash = Document.SnapshotHash,
        EffectiveDate = Document.EffectiveDate,
        RefNo = Document.RefNo,
        Remarks = Document.Remarks,
        Lines = Editors.Select(line => new ProductionStockAdjustmentLineInput
        {
            ProductionBalLotId = line.Source.ProductionBalLotId,
            AdjustmentQty = line.Direction == "ADD" ? Math.Abs(line.InputQty) : -Math.Abs(line.InputQty),
            Reason = line.Reason,
            Remarks = line.Remarks,
            ProposedUnitCost = line.ProposedUnitCost,
            CostEvidenceType = line.CostEvidenceType,
            CostOverrideReason = line.CostOverrideReason
        }).ToList()
    };

    public ValueTask DisposeAsync() { CancelSourceSearch(); return ValueTask.CompletedTask; }

    protected sealed class AdjustmentEditor(ProductionStockAdjustmentSource source, string direction, decimal qty,
        string reason, string? remarks, decimal? cost, string? evidence, string? costReason)
    {
        public ProductionStockAdjustmentSource Source { get; } = source;
        public string Direction { get; set; } = direction;
        public decimal InputQty { get; set; } = qty;
        public string Reason { get; set; } = reason;
        public string? Remarks { get; set; } = remarks;
        public decimal? ProposedUnitCost { get; set; } = cost;
        public string? CostEvidenceType { get; set; } = evidence;
        public string? CostOverrideReason { get; set; } = costReason;
    }
}
