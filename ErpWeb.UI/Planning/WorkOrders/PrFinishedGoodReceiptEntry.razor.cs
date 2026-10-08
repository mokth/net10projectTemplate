using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrFinishedGoodReceiptEntry : PageBase, IDisposable
{
    [Parameter] public int Id { get; set; }
    [Inject] private IProductionFinishedGoodReceiptService Receipts { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService Access { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private const int SourcePageSize = 20;
    private string _saved = "";
    private string? _loadedKey;
    private bool _navigating;
    private FinishedGoodReceiptCommand? _pending;
    private CancellationTokenSource? _costTraceCts;
    private long _costTraceRequestVersion;

    protected FinishedGoodReceiptDocument Document = new();
    protected List<ReceiptLineEditor> Editors { get; set; } = [];
    protected List<FinishedGoodSourceRow> Sources { get; set; } = [];
    protected FinishedGoodSourceFilterOptions FilterOptions { get; set; } = new();
    protected List<ProductionOutputChoice> Warehouses { get; set; } = [];
    protected string? StatusMessage;
    protected string SearchWo = string.Empty;
    protected string SearchWorkCentre = string.Empty;
    protected string SearchProcess = string.Empty;
    protected string SearchItem = string.Empty;
    protected string RollbackReason = string.Empty;
    protected string RefNo
    {
        get => Document.RefNo ?? "";
        set => Document.RefNo = value;
    }
    protected string Remarks
    {
        get => Document.Remarks ?? "";
        set => Document.Remarks = value;
    }

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool IsSearchingSources;
    protected bool MoreFiltersVisible;
    protected bool SourcePickerVisible;
    protected bool PostConfirmationVisible;
    protected bool DeleteConfirmationVisible;
    protected bool RollbackConfirmVisible;
    protected bool ChangeWorkOrderVisible;
    protected bool CostTraceVisible;
    protected bool IsCostTraceLoading;
    protected string? CostTraceError;
    protected FinishedGoodCostTrace? CostTrace;
    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDeleteAccess;
    protected bool CanPostAccess;
    protected bool CanRollbackAccess;
    protected int SourceTotalCount;
    protected int SourcePage;

    protected bool IsViewRequested =>
        Navigation.RelativePath.Contains("/view", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditRequested =>
        Navigation.RelativePath.Contains("/edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsNewMode => Id == 0;
    protected bool LoadFailed =>
        !IsBootstrapping
        && Id > 0
        && Document.Id == 0;
    protected bool IsViewMode =>
        IsViewRequested || (Id > 0 && !string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase));
    protected bool IsEditMode => Id > 0 && IsEditRequested && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase);
    protected bool EntryPermissionDenied =>
        !LoadFailed
        && ((IsNewMode && !CanAdd)
            || (IsEditRequested
                && Id > 0
                && Document.Id > 0
                && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
                && !CanEdit));
    protected bool IsSelectingSource =>
        !LoadFailed
        && !EntryPermissionDenied
        && !IsViewMode
        && CanEditFields
        && !HasLockedWorkOrder;
    protected bool IsReceiptWorkspace =>
        !LoadFailed
        && !EntryPermissionDenied
        && !IsViewMode
        && CanEditFields
        && HasLockedWorkOrder;
    protected bool CanAddSourceLot =>
        IsReceiptWorkspace
        && Editors.Count < 200;
    protected string ModeChip => IsViewMode ? "VIEW" : IsEditMode ? "EDIT" : "NEW";
    protected string PageHeading => IsNewMode
        ? "New Finished Good Receipt"
        : IsEditMode
            ? "Edit Finished Good Receipt"
            : "View Finished Good Receipt";
    protected int LineCount => IsViewMode ? Document.Lines.Count : Editors.Count;
    protected bool CanEditFields =>
        !IsViewMode
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && (IsNewMode ? CanAdd : CanEdit);
    protected bool CanSave => CanEditFields && Editors.Count > 0;
    protected bool ShowPost =>
        Id > 0
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && CanPostAccess;
    protected bool CanPost =>
        ShowPost
        && Document.PostingEnabled
        && !Dirty
        && !IsSubmitting;
    protected bool CanRollback =>
        Id > 0
        && string.Equals(Document.Status, "POSTED", StringComparison.OrdinalIgnoreCase)
        && CanRollbackAccess;
    protected bool CanDelete =>
        Id > 0
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && CanDeleteAccess;
    protected bool CanCreateCorrection =>
        Id > 0
        && string.Equals(Document.Status, "REVERSED", StringComparison.OrdinalIgnoreCase)
        && CanAdd;
    protected bool CanOpenEditFromView =>
        IsViewMode
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && CanEdit
        && Id > 0;
    protected bool HasLockedWorkOrder => Document.WorkOrderId != 0;
    protected bool CanChangeWorkOrder => CanEditFields && HasLockedWorkOrder;
    protected int SourcePageCount => Math.Max(1, (SourceTotalCount + SourcePageSize - 1) / SourcePageSize);
    protected string SourceResultLabel => SourceTotalCount == 1
        ? "1 source"
        : $"{SourceTotalCount:N0} sources";
    protected bool Dirty => CanEditFields && _saved != JsonSerializer.Serialize(ToRequest());
    protected string SourceItemSummary => DistinctOrDash(Editors.Select(x => x.Line.ItemCode));
    protected string SourceLotSummary => DistinctOrDash(Editors.Select(x => x.Line.SourceLot));
    protected string SourceWorkCentreSummary => CommonOrMultiple(Editors.Select(x => x.Line.WorkCentre));
    protected string SourceProcessSummary => CommonOrMultiple(Editors.Select(x => x.Line.Process));
    protected bool ShowCompactReadiness =>
        IsReceiptWorkspace
        || (IsViewMode
            && !LoadFailed
            && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase));
    protected string PermissionDeniedCopy => IsNewMode
        ? "You do not have permission to create a finished good receipt."
        : "You do not have permission to edit this finished good receipt.";
    protected bool CanTraceCost =>
        IsViewMode
        && Document.CanViewCost
        && (string.Equals(Document.Status, "POSTED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Document.Status, "REVERSED", StringComparison.OrdinalIgnoreCase));

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{Id}|{Navigation.RelativePath}";
        if (_loadedKey == key)
            return;

        _loadedKey = key;
        IsBootstrapping = true;
        ErrorMessage = null;
        StatusMessage = null;
        _pending = null;
        MoreFiltersVisible = false;
        SourcePickerVisible = false;
        CloseCostTrace();

        CanAdd = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Add);
        CanEdit = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Edit);
        CanDeleteAccess = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Delete);
        CanPostAccess = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Post);
        CanRollbackAccess = await Access.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Rollback);

        Editors = [];
        Sources = [];
        SourceTotalCount = 0;
        SourcePage = 0;
        SearchWo = string.Empty;
        SearchWorkCentre = string.Empty;
        SearchProcess = string.Empty;
        SearchItem = string.Empty;

        if (Id > 0)
        {
            var result = await Receipts.GetAsync(Id);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to load the finished good receipt.";
                Document = new();
                IsBootstrapping = false;
                return;
            }

            Document = result.Data;

            if (IsEditRequested
                && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
                && !CanEdit)
            {
                _navigating = true;
                Navigation.NavigateTo($"/planning/finished-good-receipts/{Id}/view", replace: true);
                return;
            }
        }
        else
        {
            Document = new() { EffectiveDate = Clock.Now, Status = "NEW", PostingEnabled = false };
        }

        if (CanEditFields)
        {
            await LoadWarehousesAsync();
            Editors = [];
            foreach (var line in Document.Lines)
                Editors.Add(await CreateEditorAsync(line, defaultWarehouse: false));

            if (!HasLockedWorkOrder)
            {
                await LoadFilterOptionsAsync(workOrderId: null);
                await SearchSourcesAsync();
            }
        }

        _saved = JsonSerializer.Serialize(ToRequest());
        IsBootstrapping = false;
    }

    protected async Task FindSourcesAsync()
    {
        SourcePage = 0;
        await SearchSourcesAsync();
    }

    protected async Task ClearInitialSourceFiltersAsync()
    {
        SearchWo = string.Empty;
        SearchWorkCentre = string.Empty;
        SearchProcess = string.Empty;
        SearchItem = string.Empty;
        SourcePage = 0;
        await SearchSourcesAsync();
    }

    protected async Task ClearPickerFiltersAsync()
    {
        SearchWorkCentre = string.Empty;
        SearchProcess = string.Empty;
        SearchItem = string.Empty;
        SourcePage = 0;
        await SearchSourcesAsync();
    }

    protected async Task ChangeSourcePageAsync(int delta)
    {
        var next = SourcePage + delta;
        if (next < 0 || next >= SourcePageCount)
            return;
        SourcePage = next;
        await SearchSourcesAsync();
    }

    protected async Task OpenSourcePickerAsync()
    {
        if (!CanAddSourceLot)
            return;

        SearchWorkCentre = string.Empty;
        SearchProcess = string.Empty;
        SearchItem = string.Empty;
        MoreFiltersVisible = false;
        SourcePage = 0;
        SourcePickerVisible = true;

        await LoadFilterOptionsAsync(Document.WorkOrderId);
        await SearchSourcesAsync();
    }

    protected void CloseSourcePicker() => SourcePickerVisible = false;

    protected async Task AddSourceAsync(FinishedGoodSourceRow source)
    {
        if (HasLockedWorkOrder && Document.WorkOrderId != source.WorkOrderId)
        {
            ErrorMessage = "This receipt is locked to one Work Order. Use CHANGE WORK ORDER first.";
            return;
        }

        if (Editors.Any(x => x.Line.ProductionBalLotId == source.Id))
            StatusMessage = "This source is already on the receipt. A second line will split the destination.";

        Document.WorkOrderId = source.WorkOrderId;
        Document.WorkOrderNo = source.WorkOrderNo;
        SearchWo = source.WorkOrderNo;
        var line = new FinishedGoodReceiptLine
        {
            ProductionBalLotId = source.Id,
            ItemCode = source.ItemCode,
            SourceLot = source.LotNo,
            SourceUom = source.Uom,
            AvailableQty = source.AvailableQty,
            WorkCentre = source.WorkCentre,
            Process = source.Process,
            LotControl = source.LotControl,
            LotNo = source.LotControl ? source.LotNo : "",
            ExpiryDate = null
        };
        Editors.Add(await CreateEditorAsync(line, defaultWarehouse: true));
        SourcePickerVisible = false;
    }

    protected void RemoveLine(ReceiptLineEditor editor)
    {
        Editors.Remove(editor);
    }

    protected async Task OnWarehouseChangedAsync(ReceiptLineEditor editor, string? warehouse)
    {
        editor.Line.Warehouse = warehouse ?? "";
        editor.Line.Location = "";
        await LoadLineLocationsAsync(editor);
        editor.LookupRevision++;
        StateHasChanged();
    }

    protected async Task SaveAsync()
    {
        if (IsSubmitting || !CanSave)
            return;

        if ((Document.RefNo?.Length ?? 0) > 30 || (Document.Remarks?.Length ?? 0) > 200)
        {
            ErrorMessage = "Reference is limited to 30 and remarks to 200 characters.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Receipts.SaveAsync(ToRequest());
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to save the receipt.";
                return;
            }

            Document = result.Data;
            _saved = JsonSerializer.Serialize(ToRequest());
            _pending = null;
            _navigating = true;
            Navigation.NavigateTo($"/planning/finished-good-receipts/{Document.Id}/view", replace: true);
        }
        catch (Exception)
        {
            ErrorMessage = "Unable to save the receipt. Reload the list before retrying if the response was lost.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenPostConfirm()
    {
        if (!ShowPost)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (!Document.PostingEnabled)
        {
            StatusMessage = "FG posting is disabled pending release acceptance.";
            return;
        }

        if (Dirty)
        {
            ErrorMessage = "Save the draft before posting.";
            return;
        }

        PostConfirmationVisible = true;
    }

    protected void OpenRollbackConfirm()
    {
        RollbackReason = string.Empty;
        RollbackConfirmVisible = true;
    }

    protected void OpenDeleteConfirm() => DeleteConfirmationVisible = true;
    protected void OpenChangeWorkOrderConfirm() => ChangeWorkOrderVisible = true;

    protected void OpenEdit()
    {
        if (Id <= 0)
            return;
        _navigating = true;
        Navigation.NavigateTo(DocumentReturnNavigation.PreserveReturnUrl(
            Navigation.Uri, $"/planning/finished-good-receipts/{Id}/edit"));
    }

    protected async Task PostAsync()
    {
        await ApplyCommandAsync("POST");
        PostConfirmationVisible = false;
    }

    protected async Task RollbackAsync()
    {
        var reason = (RollbackReason ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "Rollback reason is required.";
            return;
        }

        if (reason.Length > 500)
        {
            ErrorMessage = "Rollback reason cannot exceed 500 characters.";
            return;
        }

        await ApplyCommandAsync("ROLLBACK", reason);
        RollbackConfirmVisible = false;
    }

    protected async Task DeleteAsync()
    {
        if (IsSubmitting || Id <= 0)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var deleted = await Receipts.DeleteAsync(Id, Document.RowVersion);
            if (!deleted.Succeeded)
            {
                ErrorMessage = deleted.Message ?? "Unable to delete the receipt.";
                return;
            }

            _navigating = true;
            BackToList();
        }
        catch (Exception)
        {
            ErrorMessage = "The response could not be received. Retry to resolve the same request safely.";
        }
        finally
        {
            IsSubmitting = false;
            DeleteConfirmationVisible = false;
        }
    }

    protected async Task CreateCorrectionAsync()
    {
        if (IsSubmitting || Id <= 0)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Receipts.CreateCorrectionAsync(Id);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to create a correction.";
                return;
            }

            _navigating = true;
            Navigation.NavigateTo($"/planning/finished-good-receipts/{result.Data.Id}/edit");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ChangeWorkOrderAsync()
    {
        Editors.Clear();
        Document.WorkOrderId = 0;
        Document.WorkOrderNo = "";
        SearchWo = string.Empty;
        SearchWorkCentre = string.Empty;
        SearchProcess = string.Empty;
        SearchItem = string.Empty;
        MoreFiltersVisible = false;
        SourcePickerVisible = false;
        ChangeWorkOrderVisible = false;
        SourcePage = 0;

        await LoadFilterOptionsAsync(workOrderId: null);
        await SearchSourcesAsync();
    }

    protected void BackToList() => DocumentReturnNavigation.NavigateBack(Navigation, "/planning/finished-good-receipts");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected async Task OpenCostTraceAsync(FinishedGoodReceiptLine line)
    {
        if (!CanTraceCost || line.TotalValue is null || line.SourceId <= 0)
            return;

        var requestVersion = ++_costTraceRequestVersion;
        CancelCostTrace();
        var cts = new CancellationTokenSource();
        _costTraceCts = cts;
        CostTraceVisible = true;
        IsCostTraceLoading = true;
        CostTraceError = null;
        CostTrace = null;
        StateHasChanged();

        try
        {
            var result = await Receipts.GetCostTraceAsync(Document.Id, line.SourceId, cts.Token);
            if (requestVersion != _costTraceRequestVersion || cts.IsCancellationRequested)
                return;

            if (result.Succeeded && result.Data is not null)
                CostTrace = result.Data;
            else
                CostTraceError = result.Message ?? "Unable to load the posted cost trace.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (requestVersion == _costTraceRequestVersion)
                CostTraceError = ex.Message;
        }
        finally
        {
            if (requestVersion == _costTraceRequestVersion)
            {
                IsCostTraceLoading = false;
                StateHasChanged();
            }
        }
    }

    protected void CloseCostTrace()
    {
        ++_costTraceRequestVersion;
        CancelCostTrace();
        CostTraceVisible = false;
        IsCostTraceLoading = false;
        CostTraceError = null;
        CostTrace = null;
    }

    private void CancelCostTrace()
    {
        if (_costTraceCts is null)
            return;

        _costTraceCts.Cancel();
        _costTraceCts.Dispose();
        _costTraceCts = null;
    }

    protected async Task BeforeNavigate(LocationChangingContext context)
    {
        if (!_navigating && Dirty && !await Js.InvokeAsync<bool>("confirm", "Discard unsaved receipt changes?"))
            context.PreventNavigation();
    }

    public void Dispose() => CloseCostTrace();

    private async Task ApplyCommandAsync(string action, string? reason = null)
    {
        if (IsSubmitting || Id <= 0)
            return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            _pending ??= new(Id, Document.RowVersion, Guid.NewGuid(), reason);
            var result = action == "POST"
                ? await Receipts.PostAsync(_pending)
                : await Receipts.RollbackAsync(_pending);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? $"Unable to {action.ToLowerInvariant()} the receipt.";
                _pending = null;
                return;
            }

            Document = result.Data;
            Editors = [];
            _saved = JsonSerializer.Serialize(ToRequest());
            _pending = null;
            StatusMessage = action == "POST" ? "Posted the finished good receipt." : "Rolled back the finished good receipt.";
        }
        catch (Exception)
        {
            ErrorMessage = "The response could not be received. Retry to resolve the same request safely.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task SearchSourcesAsync()
    {
        IsSearchingSources = true;
        try
        {
            var result = await Receipts.SearchSourcesAsync(new FinishedGoodSourceQuery
            {
                WorkOrderId = Document.WorkOrderId == 0 ? null : Document.WorkOrderId,
                WorkOrderNo = HasLockedWorkOrder ? null : NullIfEmpty(SearchWo),
                WorkCentreCode = NullIfEmpty(SearchWorkCentre),
                ProcessCode = NullIfEmpty(SearchProcess),
                ItemCode = NullIfEmpty(SearchItem),
                Skip = SourcePage * SourcePageSize,
                Take = SourcePageSize
            });
            Sources = result.Data?.Rows.ToList() ?? [];
            SourceTotalCount = result.Data?.TotalCount ?? 0;
            if (!result.Succeeded)
                ErrorMessage = result.Message ?? "Unable to load eligible sources.";
        }
        finally
        {
            IsSearchingSources = false;
        }
    }

    private async Task LoadFilterOptionsAsync(long? workOrderId)
    {
        var result = workOrderId.HasValue
            ? await Receipts.GetSourceFilterOptionsAsync(workOrderId.Value)
            : await Receipts.GetSourceFilterOptionsAsync();
        FilterOptions = result.Data ?? new FinishedGoodSourceFilterOptions();
        if (!result.Succeeded)
            ErrorMessage = result.Message ?? "Unable to load source filters.";
    }

    private async Task LoadWarehousesAsync()
    {
        var result = await Lookups.ListActiveWarehousesAsync();
        Warehouses = ToChoices(result.Rows);
        if (!result.Succeeded)
            ErrorMessage = result.ErrorMessage ?? "Unable to load warehouses.";
    }

    private async Task<ReceiptLineEditor> CreateEditorAsync(FinishedGoodReceiptLine line, bool defaultWarehouse)
    {
        if (defaultWarehouse && string.IsNullOrWhiteSpace(line.Warehouse))
        {
            var active = Warehouses
                .Where(x => !x.Label.Contains("(historical)", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (active.Count == 1)
                line.Warehouse = active[0].Code;
        }

        if (!line.LotControl)
        {
            line.LotNo = "";
            line.ExpiryDate = null;
        }

        var editor = new ReceiptLineEditor { Line = line };
        if (!defaultWarehouse)
            Warehouses = AddHistoricalChoice(Warehouses, line.Warehouse).ToList();
        await LoadLineLocationsAsync(editor);
        return editor;
    }

    private async Task LoadLineLocationsAsync(ReceiptLineEditor editor)
    {
        if (string.IsNullOrWhiteSpace(editor.Line.Warehouse))
        {
            editor.Locations = [];
            return;
        }

        var result = await Lookups.ListActiveLocationsAsync(editor.Line.Warehouse);
        IReadOnlyList<ProductionOutputChoice> locations = ToChoices(result.Rows);
        if (IsEditMode)
            locations = AddHistoricalChoice(locations, editor.Line.Location);
        editor.Locations = locations.ToList();
        if (!result.Succeeded)
            ErrorMessage = result.ErrorMessage ?? "Unable to load locations.";
    }

    private FinishedGoodReceiptSaveRequest ToRequest() => new()
    {
        Id = Document.Id,
        ExpectedVersion = Document.RowVersion ?? [],
        WorkOrderId = Document.WorkOrderId,
        EffectiveDate = Document.EffectiveDate,
        RefNo = Document.RefNo,
        Remarks = Document.Remarks,
        Lines = Editors.Select(x => new FinishedGoodReceiptLineInput
        {
            ProductionBalLotId = x.Line.ProductionBalLotId,
            Quantity = x.Line.Quantity,
            Warehouse = x.Line.Warehouse,
            Location = x.Line.Location,
            LotNo = x.Line.LotControl ? x.Line.LotNo : "",
            ExpiryDate = x.Line.LotControl ? x.Line.ExpiryDate : null
        }).ToList()
    };

    private static List<ProductionOutputChoice> ToChoices(IEnumerable<IvCodeLookupRow> rows) =>
        rows.Select(x => new ProductionOutputChoice(x.Code,
                string.IsNullOrWhiteSpace(x.Desc) ? x.Code : $"{x.Code} — {x.Desc}"))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DistinctOrDash(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? "—" : string.Join(", ", distinct);
    }

    private static string CommonOrMultiple(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
            return "—";
        return distinct.Count == 1 ? distinct[0] : "Multiple";
    }

    protected sealed class ReceiptLineEditor
    {
        public Guid Key { get; } = Guid.NewGuid();
        public FinishedGoodReceiptLine Line { get; init; } = new();
        public List<ProductionOutputChoice> Locations { get; set; } = [];
        public int LookupRevision { get; set; }
    }
}
