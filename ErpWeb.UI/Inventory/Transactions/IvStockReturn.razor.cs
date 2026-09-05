using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvStockReturn : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public int? BatchNo { get; set; }

    [Inject] private IIvStockReturnService StockReturn { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool ConfirmDiscardVisible;
    protected string? PopupError;
    protected string BatchNoDisplay = "AUTO";
    protected string BatchStatusDisplay = IvBatchStatuses.New;
    protected bool LocationsLoading;
    protected bool CanEditPermission;
    protected DateTime AppToday => Dates.Today;

    private int _locationLoadVersion;
    private string? _pendingDefLocation;
    private IvStockReturnLineVm? _editingLine;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private bool _isDirty;
    private readonly InventoryLotEntryState _lotEntry = new();

    protected IvStockReturnHeaderVm Header { get; set; } = CreateHeader();
    protected List<IvStockReturnLineVm> Lines { get; set; } = [];
    protected IvStockReturnPopupVm Popup { get; set; } = new();
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Uoms { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

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
        ? "New Stock Return"
        : IsEditMode
            ? "Edit Stock Return"
            : "View Stock Return";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected decimal DocumentTotal => Lines.Sum(x => x.Amount);
    protected decimal PopupAmount => decimal.Round(Popup.Quantity * Popup.UnitPrice, 2);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit return line" : "Add return line";
    protected string PopupPrimaryText => IsEditingLine ? "Update item" : "Add item";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockReturn, PermissionCodes.Edit);
            await LoadLookupsAsync();
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
                Navigation.NavigateTo("/inventory/stock-return");
                return;
            }

            var result = await StockReturn.GetAsync(BatchNo.Value);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load Stock Return.";
                Navigation.NavigateTo("/inventory/stock-return");
                return;
            }

            ApplyDocument(result.Document);

            if (IsEditMode
                && !string.Equals(result.Document.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/inventory/stock-return/view/{result.Document.BatchNo}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDocument(IvStockReturnDocument doc)
    {
        BatchNoDisplay = doc.BatchNo.ToString();
        BatchStatusDisplay = doc.BatchStatus;
        Header = new IvStockReturnHeaderVm
        {
            TrxDate = doc.TrxDate == default ? DateTime.Today : doc.TrxDate.Date,
            RefNo = doc.RefNo ?? string.Empty,
            Remark = doc.Remark
        };
        Lines = doc.Lines.Select(x => new IvStockReturnLineVm
        {
            LineNo = x.LineNo,
            ICode = x.ICode,
            IDesc = x.IDesc ?? string.Empty,
            ToWarehouse = x.ToWarehouse,
            ToLocation = x.ToLocation ?? string.Empty,
            ToLotNo = x.ToLotNo ?? string.Empty,
            Quantity = x.Quantity,
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

    private async Task LoadLookupsAsync()
    {
        var wh = await Lookups.ListActiveWarehousesAsync();
        var cls = await Lookups.ListActiveClassesAsync();
        var uom = await Lookups.ListActiveUomsAsync();
        var st = await Lookups.ListActiveStatusesAsync();

        if (!wh.Succeeded || !cls.Succeeded || !uom.Succeeded || !st.Succeeded)
        {
            ErrorMessage = wh.ErrorMessage ?? cls.ErrorMessage ?? uom.ErrorMessage ?? st.ErrorMessage
                ?? "Unable to load lookups.";
            Warehouses = [];
            Classes = [];
            Uoms = [];
            Statuses = [];
            return;
        }

        Warehouses = wh.Rows;
        Classes = cls.Rows;
        Uoms = uom.Rows;
        Statuses = st.Rows;
    }

    private async Task RefreshPeekBatchNoAsync()
    {
        var result = await StockReturn.PeekNextBatchNoAsync();
        BatchNoDisplay = result.Succeeded ? result.PeekBatchNo.ToString() : "AUTO";
    }

    protected async Task OnNewLineClickAsync()
    {
        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        _editingLine = null;
        _lotEntry.Reset();
        Popup = CreateBlankPopup();
        Popup.ToLotNo = string.Empty;
        Popup.ExpiryDate = null;
        Locations = [];
        _pendingDefLocation = null;
        PopupError = null;
        PopupVisible = true;
    }

    protected async Task EditLineAsync(IvStockReturnLineVm line)
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
        Popup = new IvStockReturnPopupVm
        {
            ICode = line.ICode,
            IDesc = line.IDesc,
            ToLotNo = line.ToLotNo,
            Quantity = line.Quantity,
            ToWarehouse = line.ToWarehouse,
            Uom = line.Uom,
            ToLocation = line.ToLocation,
            UnitPrice = line.UnitPrice,
            IClassCode = line.IClassCode,
            IStatus = line.IStatus,
            Reason = line.Reason,
            ExpiryDate = line.ExpiryDate,
            Remarks = line.Remarks,
            LotControl = line.LotControl
        };
        _lotEntry.InitializeExisting(line.ICode);
        _pendingDefLocation = line.ToLocation;
        PopupError = null;
        PopupVisible = true;

        if (!string.IsNullOrWhiteSpace(line.ToWarehouse))
        {
            await OnWarehouseChangedAsync(line.ToWarehouse);
        }
        else
        {
            Locations = [];
        }
    }

    protected Task OnLineRowDoubleClick(GridRowClickEventArgs args)
    {
        if (!CanEditDocument)
        {
            return Task.CompletedTask;
        }

        if (args.Grid.GetDataItem(args.VisibleIndex) is IvStockReturnLineVm line)
        {
            return EditLineAsync(line);
        }

        return Task.CompletedTask;
    }

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        Popup.ICode = item.ICode;
        Popup.IDesc = item.IDesc ?? string.Empty;
        Popup.IClassCode = item.IClassCode ?? string.Empty;
        Popup.Uom = item.StdUom ?? string.Empty;
        Popup.LotControl = item.LotControl;
        Popup.UnitPrice = item.PurchasePrice ?? 0m;
        _pendingDefLocation = item.DefLocation;

        if (!string.IsNullOrWhiteSpace(item.DefWarehouse) &&
            Warehouses.Any(w => string.Equals(w.Code, item.DefWarehouse, StringComparison.OrdinalIgnoreCase)))
        {
            await OnWarehouseChangedAsync(item.DefWarehouse);
        }
        else
        {
            Popup.ToWarehouse = string.Empty;
            Popup.ToLocation = string.Empty;
            Locations = [];
        }

        var result = _lotEntry.SelectItem(item.ICode, item.LotControl, NextLotNo);
        switch (result.Change)
        {
            case LotEntryChange.Keep:
                break;
            case LotEntryChange.Clear:
                Popup.ToLotNo = string.Empty;
                Popup.ExpiryDate = null;
                break;
            case LotEntryChange.NewLot:
                Popup.ToLotNo = result.LotNo ?? string.Empty;
                Popup.ExpiryDate = null;
                break;
        }
    }

    protected async Task OnWarehouseChangedAsync(string? warehouse)
    {
        Popup.ToWarehouse = warehouse ?? string.Empty;
        Popup.ToLocation = string.Empty;
        Locations = [];

        if (string.IsNullOrWhiteSpace(Popup.ToWarehouse))
        {
            LocationsLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _locationLoadVersion);
        LocationsLoading = true;
        var result = await Lookups.ListActiveLocationsAsync(Popup.ToWarehouse);
        if (version != _locationLoadVersion)
        {
            return;
        }

        LocationsLoading = false;
        if (!result.Succeeded)
        {
            PopupError = result.ErrorMessage;
            Locations = [];
            return;
        }

        Locations = result.Rows;
        if (!string.IsNullOrWhiteSpace(_pendingDefLocation) &&
            Locations.Any(x => string.Equals(x.Code, _pendingDefLocation, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.ToLocation = _pendingDefLocation;
        }

        _pendingDefLocation = null;
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
            var line = new IvStockReturnLineVm { LineNo = (short)(Lines.Count + 1) };
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

    protected void RemoveLine(IvStockReturnLineVm line)
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
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockReturn, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Add at least one return line.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var request = new IvStockReturnSaveRequest
            {
                TrxDate = Header.TrxDate,
                RefNo = Header.RefNo,
                Remark = Header.Remark,
                Lines = Lines.Select(x => new IvStockReturnLineRequest
                {
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    ToWarehouse = x.ToWarehouse,
                    ToLocation = x.ToLocation,
                    ToLotNo = x.ToLotNo,
                    Quantity = x.Quantity,
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
                ? await StockReturn.SaveNewAsync(request)
                : await StockReturn.UpdateAsync(BatchNo!.Value, request);

            if (result.Succeeded)
            {
                _isDirty = false;
                Navigation.NavigateTo("/inventory/stock-return");
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "Unable to save Stock Return.";
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

        Navigation.NavigateTo("/inventory/stock-return");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/stock-return");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (BatchNo is null or <= 0)
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/stock-return/edit/{BatchNo.Value}");
    }

    protected void OnKeepEditing() => ConfirmDiscardVisible = false;

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/inventory/stock-return");
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return "sr-status is-on";
        }

        if (string.Equals(status, IvItemStatuses.Damaged, StringComparison.OrdinalIgnoreCase))
        {
            return "sr-status is-off";
        }

        return "sr-status is-hold";
    }

    private async Task<bool> CanMaintainLinesAsync()
    {
        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockReturn, permission))
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

        if (string.IsNullOrWhiteSpace(Popup.ToWarehouse))
        {
            return "Warehouse is required.";
        }

        if (Locations.Count > 0 && string.IsNullOrWhiteSpace(Popup.ToLocation))
        {
            return "Location is required.";
        }

        if (string.IsNullOrWhiteSpace(Popup.Uom))
        {
            return "UOM is required.";
        }

        if (string.IsNullOrWhiteSpace(Popup.IClassCode))
        {
            return "Item class is required.";
        }

        if (string.IsNullOrWhiteSpace(Popup.IStatus))
        {
            return "Item status is required.";
        }

        if (Popup.Quantity <= 0)
        {
            return "Quantity must be greater than zero.";
        }

        if (Popup.UnitPrice < 0)
        {
            return "Unit price cannot be negative.";
        }

        if (string.IsNullOrWhiteSpace(Popup.Reason)
            || !IvReturnReasons.All.Any(x => string.Equals(x, Popup.Reason.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "Reason is required.";
        }

        if (Popup.LotControl)
        {
            if (string.IsNullOrWhiteSpace(Popup.ToLotNo))
            {
                return "Lot number is required for this item.";
            }

            if (Popup.ExpiryDate is null)
            {
                return "Expiry date is required for this item.";
            }

            if (Popup.ExpiryDate.Value.Date < AppToday.Date)
            {
                return "Expiry date cannot be earlier than today.";
            }
        }

        return null;
    }

    private string NextLotNo()
    {
        var prefix = DateTime.Today.ToString("yyMMdd");
        var used = Lines
            .Where(x => x != _editingLine && !string.IsNullOrWhiteSpace(x.ToLotNo))
            .Select(x => x.ToLotNo)
            .ToList();
        return IvLotNumberGenerator.AllocateAsync(
                1,
                prefix,
                1,
                autoGenerate: true,
                used,
                _ => Task.FromResult(false))
            .GetAwaiter()
            .GetResult()[0];
    }

    private void ApplyPopupToLine(IvStockReturnLineVm line)
    {
        line.ICode = Popup.ICode.Trim();
        line.IDesc = Popup.IDesc.Trim();
        line.ToWarehouse = Popup.ToWarehouse.Trim();
        line.ToLocation = (Popup.ToLocation ?? string.Empty).Trim();
        line.ToLotNo = Popup.LotControl ? Popup.ToLotNo.Trim() : string.Empty;
        line.Quantity = Popup.Quantity;
        line.Uom = Popup.Uom.Trim();
        line.IClassCode = Popup.IClassCode.Trim();
        line.IStatus = Popup.IStatus.Trim().ToUpperInvariant();
        line.UnitPrice = Popup.UnitPrice;
        line.ExpiryDate = Popup.LotControl ? Popup.ExpiryDate : null;
        line.Reason = IvReturnReasons.All.First(x =>
            string.Equals(x, Popup.Reason.Trim(), StringComparison.OrdinalIgnoreCase));
        line.Remarks = string.IsNullOrWhiteSpace(Popup.Remarks) ? null : Popup.Remarks.Trim();
        line.LotControl = Popup.LotControl;
    }

    private void RenumberLines()
    {
        short n = 1;
        foreach (var line in Lines)
        {
            line.LineNo = n++;
        }
    }

    private IvStockReturnPopupVm CreateBlankPopup() =>
        new()
        {
            Reason = IvReturnReasons.Return,
            IStatus = Statuses.Any(x =>
                string.Equals(x.Code, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
                ? IvItemStatuses.Active
                : (Statuses.FirstOrDefault()?.Code ?? string.Empty)
        };

    private static IvStockReturnHeaderVm CreateHeader() =>
        new()
        {
            TrxDate = DateTime.Today,
            RefNo = "AUTO"
        };
}

public sealed class IvStockReturnHeaderVm
{
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string RefNo { get; set; } = "AUTO";
    public string? Remark { get; set; }
}

public sealed class IvStockReturnLineVm
{
    public short LineNo { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
    public decimal Amount => decimal.Round(Quantity * UnitPrice, 2);
}

public sealed class IvStockReturnPopupVm
{
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string Uom { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string IClassCode { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public string? Reason { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
}
