using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvStockAdjustment : PageBase
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public int? BatchNo { get; set; }

    [Inject] private IIvStockAdjustmentService StockAdjustment { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool ConfirmDiscardVisible;
    protected string? PopupError;
    protected string BatchNoDisplay = "AUTO";
    protected string BatchStatusDisplay = IvBatchStatuses.New;
    protected bool CanEditPermission;

    private IvStockAdjustmentLineVm? _editingLine;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private bool _isDirty;
    private bool _suppressQtyBind;

    protected IvStockAdjustmentHeaderVm Header { get; set; } = CreateHeader();
    protected List<IvStockAdjustmentLineVm> Lines { get; set; } = [];
    protected IvStockAdjustmentPopupVm Popup { get; set; } = new();

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && string.Equals(BatchStatusDisplay, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase)
        && BatchNo is > 0;

    protected string PageHeading => IsNewMode
        ? "New stock adjustment"
        : IsEditMode
            ? "Edit stock adjustment"
            : "View stock adjustment";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
    protected decimal DocumentTotal => Lines.Sum(x => x.Amount);
    protected decimal PopupAmount => decimal.Round(Popup.AdjustQty * Popup.UnitPrice, 2);

    protected decimal PopupAdjustQty
    {
        get => Popup.AdjustQty;
        set
        {
            if (_suppressQtyBind)
            {
                return;
            }

            _suppressQtyBind = true;
            Popup.AdjustQty = value;
            _suppressQtyBind = false;
        }
    }

    protected decimal PopupNewQty
    {
        get => Popup.CurrentQty + Popup.AdjustQty;
        set
        {
            if (_suppressQtyBind)
            {
                return;
            }

            _suppressQtyBind = true;
            Popup.AdjustQty = value - Popup.CurrentQty;
            _suppressQtyBind = false;
        }
    }
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit adjustment line" : "Add adjustment line";
    protected string PopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";

    protected IReadOnlyList<string> ReasonOptions { get; } = IvAdjustmentReasons.All;

    protected decimal PopupMaxDecreaseQty
    {
        get
        {
            if (Popup.BalLocId <= 0)
            {
                return 0m;
            }

            var otherNet = Lines
                .Where(x => x != _editingLine && x.BalLocId == Popup.BalLocId)
                .Sum(x => x.AdjustQty);
            return Math.Max(0m, Popup.CurrentQty + otherNet);
        }
    }

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Edit);
            _lookupsLoaded = true;
        }

        var key = $"{Mode}|{BatchNo}";
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        PopupVisible = false;
        ConfirmDiscardVisible = false;
        _editingLine = null;
        _isDirty = false;

        try
        {
            if (IsNewMode)
            {
                Header = CreateHeader();
                Lines = [];
                BatchStatusDisplay = IvBatchStatuses.New;
                await RefreshPeekBatchNoAsync();
                return;
            }

            if (BatchNo is null or <= 0)
            {
                ErrorMessage = "Batch number is required.";
                Navigation.NavigateTo("/inventory/stock-adjustment");
                return;
            }

            var result = await StockAdjustment.GetAsync(BatchNo.Value);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load stock adjustment.";
                Navigation.NavigateTo("/inventory/stock-adjustment");
                return;
            }

            ApplyDocument(result.Document);

            if (IsEditMode
                && !string.Equals(result.Document.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/inventory/stock-adjustment/view/{result.Document.BatchNo}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDocument(IvStockAdjustmentDocument doc)
    {
        BatchNoDisplay = doc.BatchNo.ToString();
        BatchStatusDisplay = doc.BatchStatus;
        Header = new IvStockAdjustmentHeaderVm
        {
            TrxDate = doc.TrxDate == default ? DateTime.Today : doc.TrxDate.Date,
            RefNo = doc.RefNo ?? string.Empty,
            Remark = doc.Remark
        };
        Lines = doc.Lines.Select(x => new IvStockAdjustmentLineVm
        {
            LineNo = x.LineNo,
            BalLocId = x.BalLocId,
            ICode = x.ICode,
            IDesc = x.IDesc ?? string.Empty,
            Warehouse = x.Warehouse,
            Location = x.Location ?? string.Empty,
            LotNo = x.LotNo ?? string.Empty,
            CurrentQty = x.CurrentQty,
            AdjustQty = x.AdjustQty,
            Uom = x.Uom ?? string.Empty,
            IClassCode = x.IClassCode ?? string.Empty,
            IStatus = x.IStatus,
            UnitPrice = x.UnitPrice,
            ExpiryDate = x.ExpiryDate,
            Reason = x.Reason,
            Remarks = x.Remarks,
            LotControl = x.LotControl
        }).ToList();
    }

    private async Task RefreshPeekBatchNoAsync()
    {
        var result = await StockAdjustment.PeekNextBatchNoAsync();
        BatchNoDisplay = result.Succeeded ? result.PeekBatchNo.ToString() : "AUTO";
    }

    protected async Task OnNewLineClickAsync()
    {
        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        _editingLine = null;
        Popup = new IvStockAdjustmentPopupVm();
        PopupError = null;
        PopupVisible = true;
    }

    protected async Task EditLineAsync(IvStockAdjustmentLineVm line)
    {
        if (IsSubmitting || !CanEditDocument)
        {
            return;
        }

        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        _editingLine = line;
        Popup = new IvStockAdjustmentPopupVm
        {
            ICode = line.ICode,
            IDesc = line.IDesc,
            IClassCode = line.IClassCode,
            BalLocId = line.BalLocId,
            BalLocDisplay = string.IsNullOrWhiteSpace(line.LotNo) ? $"#{line.BalLocId}" : line.LotNo,
            Warehouse = line.Warehouse,
            Location = line.Location,
            LotNo = line.LotNo,
            CurrentQty = line.CurrentQty,
            AdjustQty = line.AdjustQty,
            Uom = line.Uom,
            IStatus = line.IStatus,
            ExpiryDate = line.ExpiryDate,
            LotControl = line.LotControl,
            UnitPrice = line.UnitPrice,
            Reason = line.Reason,
            Remarks = line.Remarks
        };
        PopupError = null;
        PopupVisible = true;
    }

    protected Task OnLineRowDoubleClick(GridRowClickEventArgs args)
    {
        if (!CanEditDocument)
        {
            return Task.CompletedTask;
        }

        if (args.Grid.GetDataItem(args.VisibleIndex) is IvStockAdjustmentLineVm line)
        {
            return EditLineAsync(line);
        }

        return Task.CompletedTask;
    }

    protected Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        ApplyResolvedItem(item);
        return Task.CompletedTask;
    }

    protected async Task OnBarcodeResolveAsync()
    {
        PopupError = null;
        var term = (Popup.Barcode ?? string.Empty).Trim();
        if (term.Length == 0)
        {
            return;
        }

        var result = await Lookups.ResolveItemAsync(term);
        if (!result.Succeeded || result.Item is null)
        {
            PopupError = result.ErrorMessage ?? "Item was not found.";
            return;
        }

        ApplyResolvedItem(result.Item);
    }

    protected async Task OnBarcodeKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await OnBarcodeResolveAsync();
        }
    }

    private void ApplyResolvedItem(IvStockMasterLookupRow item)
    {
        Popup.ICode = item.ICode;
        Popup.IDesc = item.IDesc ?? string.Empty;
        Popup.IClassCode = item.IClassCode ?? string.Empty;
        Popup.LotControl = item.LotControl;
        Popup.UnitPrice = item.PurchasePrice ?? 0m;
        ResetBalLocSelection();
    }

    protected Task OnBalLocSelectedAsync(IvBalLocLookupRow row)
    {
        Popup.BalLocId = row.Id;
        Popup.BalLocDisplay = string.IsNullOrWhiteSpace(row.LotNo) ? $"#{row.Id}" : row.LotNo;
        Popup.Warehouse = row.WhCode;
        Popup.Location = row.LocCode;
        Popup.LotNo = row.LotNo;
        Popup.CurrentQty = row.StdQty;
        Popup.Uom = row.StdUom ?? string.Empty;
        Popup.IStatus = row.IStatus;
        Popup.ExpiryDate = row.ExpiryDate;
        Popup.LotControl = row.LotControl;
        Popup.AdjustQty = 0m;

        if (string.IsNullOrWhiteSpace(Popup.IClassCode) && !string.IsNullOrWhiteSpace(row.IClassCode))
        {
            Popup.IClassCode = row.IClassCode;
        }

        if (Popup.UnitPrice == 0m && row.PurchasePrice is > 0m)
        {
            Popup.UnitPrice = row.PurchasePrice.Value;
        }

        Popup.Reason = null;
        Popup.Remarks = null;
        return Task.CompletedTask;
    }

    protected void OnCommitLine()
    {
        PopupError = ValidatePopup();
        if (PopupError is not null)
        {
            return;
        }

        if (_editingLine is not null)
        {
            ApplyPopupToLine(_editingLine);
        }
        else
        {
            var line = new IvStockAdjustmentLineVm { LineNo = (short)(Lines.Count + 1) };
            ApplyPopupToLine(line);
            Lines.Add(line);
        }

        PopupVisible = false;
        PopupError = null;
        ErrorMessage = null;
        _editingLine = null;
        _isDirty = true;
    }

    protected void OnPopupCancel()
    {
        PopupVisible = false;
        PopupError = null;
        _editingLine = null;
    }

    protected void RemoveLine(IvStockAdjustmentLineVm line)
    {
        if (!CanEditDocument)
        {
            return;
        }

        if (_editingLine == line)
        {
            OnPopupCancel();
        }

        Lines.Remove(line);
        RenumberLines();
        _isDirty = true;
    }

    protected async Task OnSaveAsync()
    {
        if (IsSubmitting || !CanEditDocument)
        {
            return;
        }

        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Add at least one adjustment line.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var request = new IvStockAdjustmentSaveRequest
            {
                TrxDate = Header.TrxDate,
                RefNo = Header.RefNo,
                Remark = Header.Remark,
                Lines = Lines.Select(x => new IvStockAdjustmentLineRequest
                {
                    BalLocId = x.BalLocId,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    Warehouse = x.Warehouse,
                    Location = x.Location,
                    LotNo = x.LotNo,
                    AdjustQty = x.AdjustQty,
                    Uom = x.Uom,
                    IClassCode = x.IClassCode,
                    IStatus = x.IStatus,
                    UnitPrice = x.UnitPrice,
                    ExpiryDate = x.ExpiryDate,
                    Reason = x.Reason,
                    Remarks = x.Remarks
                }).ToList()
            };

            var result = IsNewMode
                ? await StockAdjustment.SaveNewAsync(request)
                : await StockAdjustment.UpdateAsync(BatchNo!.Value, request);

            if (result.Succeeded)
            {
                _isDirty = false;
                Navigation.NavigateTo("/inventory/stock-adjustment");
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "Unable to save stock adjustment.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (IsEditMode || _isDirty || (IsNewMode && Lines.Count > 0))
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/inventory/stock-adjustment");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/stock-adjustment");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (BatchNo is null or <= 0)
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/stock-adjustment/edit/{BatchNo.Value}");
    }

    protected void OnKeepEditing() => ConfirmDiscardVisible = false;

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/inventory/stock-adjustment");
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string AdjustQtyClass(decimal adjustQty) =>
        adjustQty < 0m ? "sa-qty sa-qty--neg" : adjustQty > 0m ? "sa-qty sa-qty--pos" : "sa-qty";

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return "sa-status is-on";
        }

        if (string.Equals(status, IvItemStatuses.Damaged, StringComparison.OrdinalIgnoreCase))
        {
            return "sa-status is-off";
        }

        return "sa-status is-hold";
    }

    private async Task<bool> CanMaintainLinesAsync()
    {
        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, permission))
        {
            ErrorMessage = "Access denied.";
            return false;
        }

        return true;
    }

    private string? ValidatePopup()
    {
        if (string.IsNullOrWhiteSpace(Popup.ICode))
        {
            return "Item code is required.";
        }

        if (Popup.BalLocId <= 0)
        {
            return "Balance location is required. Use the search button to pick an existing balance.";
        }

        if (string.IsNullOrWhiteSpace(Popup.Warehouse))
        {
            return "Warehouse is required.";
        }

        if (Popup.AdjustQty == 0m)
        {
            return "Adjust quantity cannot be zero.";
        }

        if (PopupNewQty < 0m)
        {
            return "New quantity cannot be negative.";
        }

        if (Popup.AdjustQty < 0m && Math.Abs(Popup.AdjustQty) > PopupMaxDecreaseQty)
        {
            return $"Decrease exceeds available quantity (max {PopupMaxDecreaseQty:n4}).";
        }

        if (string.IsNullOrWhiteSpace(Popup.Reason))
        {
            return "Reason is required.";
        }

        if (string.Equals(Popup.Reason, IvAdjustmentReasons.Other, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(Popup.Remarks))
        {
            return "Remark is required when reason is OTHER.";
        }

        if (Popup.UnitPrice < 0)
        {
            return "Unit price cannot be negative.";
        }

        return null;
    }

    private void ApplyPopupToLine(IvStockAdjustmentLineVm line)
    {
        line.ICode = Popup.ICode.Trim();
        line.IDesc = Popup.IDesc.Trim();
        line.IClassCode = Popup.IClassCode.Trim();
        line.BalLocId = Popup.BalLocId;
        line.Warehouse = Popup.Warehouse.Trim();
        line.Location = (Popup.Location ?? string.Empty).Trim();
        line.LotNo = (Popup.LotNo ?? string.Empty).Trim();
        line.CurrentQty = Popup.CurrentQty;
        line.AdjustQty = Popup.AdjustQty;
        line.Uom = Popup.Uom.Trim();
        line.IStatus = Popup.IStatus.Trim().ToUpperInvariant();
        line.UnitPrice = Popup.UnitPrice;
        line.ExpiryDate = Popup.ExpiryDate;
        line.Reason = string.IsNullOrWhiteSpace(Popup.Reason) ? null : Popup.Reason.Trim();
        line.Remarks = string.IsNullOrWhiteSpace(Popup.Remarks) ? null : Popup.Remarks.Trim();
        line.LotControl = Popup.LotControl;
    }

    private void ResetBalLocSelection()
    {
        Popup.BalLocId = 0;
        Popup.BalLocDisplay = null;
        Popup.Warehouse = string.Empty;
        Popup.Location = string.Empty;
        Popup.LotNo = string.Empty;
        Popup.CurrentQty = 0m;
        Popup.AdjustQty = 0m;
        Popup.Uom = string.Empty;
        Popup.IStatus = string.Empty;
        Popup.ExpiryDate = null;
        Popup.Reason = null;
        Popup.Remarks = null;
    }

    private void RenumberLines()
    {
        short n = 1;
        foreach (var line in Lines)
        {
            line.LineNo = n++;
        }
    }

    private static IvStockAdjustmentHeaderVm CreateHeader() =>
        new()
        {
            TrxDate = DateTime.Today,
            RefNo = "AUTO"
        };
}

public sealed class IvStockAdjustmentHeaderVm
{
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string RefNo { get; set; } = "AUTO";
    public string? Remark { get; set; }
}

public sealed class IvStockAdjustmentLineVm
{
    public short LineNo { get; set; }
    public int BalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public string Warehouse { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public decimal CurrentQty { get; set; }
    public decimal AdjustQty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
    public decimal NewQty => CurrentQty + AdjustQty;
    public decimal Amount => decimal.Round(AdjustQty * UnitPrice, 2);
}

public sealed class IvStockAdjustmentPopupVm
{
    public string ICode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string IDesc { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public int BalLocId { get; set; }
    public string? BalLocDisplay { get; set; }
    public string Warehouse { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public decimal CurrentQty { get; set; }
    public decimal AdjustQty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }
    public bool LotControl { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
}
