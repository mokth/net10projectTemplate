using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvGoodsReceipt : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public int? BatchNo { get; set; }

    [Inject] private IIvGoodsReceiptService GoodsReceipt { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool LinePopupVisible;
    protected bool PoPickerVisible;
    protected bool PoPickerLoading;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmTrxTypeVisible;
    protected string? LinePopupError;
    protected string? PoPickerError;
    protected string BatchNoDisplay = "AUTO";
    protected string BatchStatusDisplay = IvBatchStatuses.New;
    protected string TrxTypeDisplay = IvTrxTypes.GoodsReceive;
    protected bool LocationsLoading;
    protected bool CanEditPermission;
    protected DateTime AppToday => Dates.Today;

    private int _locationLoadVersion;
    private int _poSearchVersion;
    private string? _pendingDefLocation;
    private string? _pendingTrxType;
    private IvGoodsReceiptLineVm? _editingLine;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private bool _isDirty;

    protected IvGoodsReceiptHeaderVm Header { get; set; } = CreateHeader();
    protected List<IvGoodsReceiptLineVm> Lines { get; set; } = [];
    protected IvGoodsReceiptLinePopupVm LinePopup { get; set; } = new();
    protected List<IvGoodsReceiptPoPickerRow> PoPickerRows { get; set; } = [];
    protected IReadOnlyList<IvGoodsReceiptPoPickerRow> SelectedPoPickerRows { get; set; } = [];
    protected string PoPickerSearchText { get; set; } = string.Empty;
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Statuses { get; set; } = [];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool IsStockGr => string.Equals(Header.TrxType, IvTrxTypes.GoodsReceive, StringComparison.OrdinalIgnoreCase);

    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && string.Equals(BatchStatusDisplay, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase)
        && BatchNo is > 0;

    protected string PageHeading => IsNewMode
        ? "New goods receipt"
        : IsEditMode
            ? "Edit goods receipt"
            : "View goods receipt";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected decimal DocumentTotal => Lines.Sum(x => x.Amount);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string LinePopupTitle => IsEditingLine ? "Edit receipt line" : "Receipt line";
    protected string LinePopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";

    protected IReadOnlyList<IvGoodsReceiptTrxTypeOption> TrxTypeOptions { get; } =
    [
        new(IvTrxTypes.GoodsReceive, "Stock GR"),
        new(IvTrxTypes.NonStockGoodsReceive, "Indirect NG")
    ];

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Edit);
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
        LinePopupVisible = false;
        PoPickerVisible = false;
        ConfirmDiscardVisible = false;
        ConfirmTrxTypeVisible = false;
        _editingLine = null;
        _isDirty = false;

        try
        {
            if (IsNewMode)
            {
                Header = CreateHeader();
                Lines = [];
                BatchStatusDisplay = IvBatchStatuses.New;
                TrxTypeDisplay = Header.TrxType;
                await RefreshPeekBatchNoAsync();
                return;
            }

            if (BatchNo is null or <= 0)
            {
                ErrorMessage = "Batch number is required.";
                Navigation.NavigateTo("/inventory/goods-receipts");
                return;
            }

            var result = await GoodsReceipt.GetAsync(BatchNo.Value);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load goods receipt.";
                Navigation.NavigateTo("/inventory/goods-receipts");
                return;
            }

            ApplyDocument(result.Document);

            if (IsEditMode
                && !string.Equals(result.Document.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/inventory/goods-receipts/view/{result.Document.BatchNo}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDocument(IvGoodsReceiptDocument doc)
    {
        BatchNoDisplay = doc.BatchNo.ToString();
        BatchStatusDisplay = doc.BatchStatus;
        TrxTypeDisplay = doc.TrxType;
        Header = new IvGoodsReceiptHeaderVm
        {
            TrxDate = doc.TrxDate == default ? DateTime.Today : doc.TrxDate.Date,
            TrxType = doc.TrxType,
            RefNo = doc.RefNo ?? string.Empty,
            Remark = doc.Remark
        };
        Lines = doc.Lines.Select(x => new IvGoodsReceiptLineVm
        {
            LineNo = x.LineNo,
            PoNo = x.PoNo,
            PoRelNo = x.PoRelNo,
            PoLineNo = x.PoLineNo,
            ICode = x.ICode,
            IDesc = x.IDesc ?? string.Empty,
            ToWarehouse = x.ToWarehouse,
            ToLocation = x.ToLocation ?? string.Empty,
            ToLotNo = x.ToLotNo ?? string.Empty,
            FrPurQty = x.FrPurQty,
            ToRecvQty = x.ToRecvQty,
            Uom = x.PurchaseUom ?? string.Empty,
            StdUom = x.StdUom,
            PackSz = x.PackSz,
            IStatus = x.IStatus,
            UnitPrice = x.UnitPrice,
            ExpiryDate = x.ExpiryDate,
            Remarks = x.Remarks,
            LotControl = x.LotControl
        }).ToList();
    }

    private async Task LoadLookupsAsync()
    {
        var wh = await Lookups.ListActiveWarehousesAsync();
        var st = await Lookups.ListActiveStatusesAsync();

        if (!wh.Succeeded || !st.Succeeded)
        {
            ErrorMessage = wh.ErrorMessage ?? st.ErrorMessage ?? "Unable to load lookups.";
            Warehouses = [];
            Statuses = [];
            return;
        }

        Warehouses = wh.Rows;
        Statuses = st.Rows;
    }

    private async Task RefreshPeekBatchNoAsync()
    {
        var result = await GoodsReceipt.PeekNextBatchNoAsync();
        BatchNoDisplay = result.Succeeded ? result.PeekBatchNo.ToString() : "AUTO";
    }

    protected async Task OnTrxTypeChangedAsync(string? trxType)
    {
        var next = NormalizeTrxType(trxType);
        if (string.Equals(next, Header.TrxType, StringComparison.OrdinalIgnoreCase))
        {
            TrxTypeDisplay = next;
            return;
        }

        if (Lines.Count > 0)
        {
            _pendingTrxType = next;
            ConfirmTrxTypeVisible = true;
            TrxTypeDisplay = Header.TrxType;
            return;
        }

        Header.TrxType = next;
        TrxTypeDisplay = next;
        _isDirty = true;
        await InvokeAsync(StateHasChanged);
    }

    protected void CancelTrxTypeChange()
    {
        ConfirmTrxTypeVisible = false;
        _pendingTrxType = null;
        TrxTypeDisplay = Header.TrxType;
    }

    protected void ConfirmTrxTypeChange()
    {
        ConfirmTrxTypeVisible = false;
        if (!string.IsNullOrWhiteSpace(_pendingTrxType))
        {
            Header.TrxType = _pendingTrxType;
            TrxTypeDisplay = _pendingTrxType;
            _pendingTrxType = null;
        }

        Lines = [];
        _editingLine = null;
        LinePopupVisible = false;
        _isDirty = true;
    }

    protected async Task OpenPoPickerAsync()
    {
        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        PoPickerError = null;
        PoPickerSearchText = string.Empty;
        PoPickerRows = [];
        SelectedPoPickerRows = [];
        PoPickerVisible = true;
        await SearchPoLinesAsync();
    }

    protected async Task OnPoPickerSearchChangedAsync(string? text)
    {
        PoPickerSearchText = text ?? string.Empty;
        var version = Interlocked.Increment(ref _poSearchVersion);
        await Task.Delay(350);
        if (version != _poSearchVersion || !PoPickerVisible)
        {
            return;
        }

        await SearchPoLinesAsync();
    }

    protected async Task SearchPoLinesAsync()
    {
        PoPickerLoading = true;
        PoPickerError = null;
        try
        {
            var result = await GoodsReceipt.SearchPoLinesAsync(
                Header.TrxType,
                string.IsNullOrWhiteSpace(PoPickerSearchText) ? null : PoPickerSearchText.Trim());
            if (!result.Succeeded)
            {
                PoPickerError = result.ErrorMessage ?? "Unable to search PO lines.";
                PoPickerRows = [];
                SelectedPoPickerRows = [];
                return;
            }

            var existingKeys = Lines
                .Select(x => x.LineKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            PoPickerRows = result.PoLines
                .Where(x => !existingKeys.Contains(IvGoodsReceiptLineVm.BuildLineKey(x.PoNo, x.PoRelNo, x.PoLineNo)))
                .Select(IvGoodsReceiptPoPickerRow.FromLookup)
                .ToList();
            SelectedPoPickerRows = PoPickerRows.ToList();

            if (PoPickerRows.Count == 0)
            {
                PoPickerError = "No remaining PO lines match the current search.";
            }
        }
        finally
        {
            PoPickerLoading = false;
        }
    }

    protected void OnSelectedPoPickerRowsChanged(IReadOnlyList<object> selected)
    {
        SelectedPoPickerRows = selected.OfType<IvGoodsReceiptPoPickerRow>().ToList();
    }

    protected void ClosePoPicker() => PoPickerVisible = false;

    protected async Task AddFromPoAsync()
    {
        if (SelectedPoPickerRows.Count == 0)
        {
            PoPickerError = "Select at least one PO line.";
            return;
        }

        var added = 0;
        var reservedLotsByItem = Lines
            .Where(x => x.LotControl && !string.IsNullOrWhiteSpace(x.ToLotNo))
            .GroupBy(x => x.ICode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ToLotNo).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var row in SelectedPoPickerRows)
        {
            var key = IvGoodsReceiptLineVm.BuildLineKey(row.PoNo, row.PoRelNo, row.PoLineNo);
            if (Lines.Any(x => string.Equals(x.LineKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var warehouse = string.Empty;
            var location = string.Empty;
            var lotNo = string.Empty;

            if (IsStockGr)
            {
                warehouse = ResolveDefaultWarehouse(row.ToWarehouse, row.DefWarehouse);
                location = await ResolveDefaultLocationAsync(warehouse, row.DefLocation);
                if (row.LotControl)
                {
                    reservedLotsByItem.TryGetValue(row.ICode, out var reserved);
                    reserved ??= [];
                    var allocate = await GoodsReceipt.AllocateLotAsync(
                        row.ICode,
                        reserved,
                        IsEditMode ? BatchNo : null);
                    if (!allocate.Succeeded || string.IsNullOrWhiteSpace(allocate.AllocatedLotNo))
                    {
                        PoPickerError = allocate.ErrorMessage ?? $"Unable to allocate lot for item '{row.ICode}'.";
                        return;
                    }

                    lotNo = allocate.AllocatedLotNo;
                    reserved.Add(lotNo);
                    reservedLotsByItem[row.ICode] = reserved;
                }
            }

            Lines.Add(new IvGoodsReceiptLineVm
            {
                LineNo = (short)(Lines.Count + 1),
                PoNo = row.PoNo,
                PoRelNo = row.PoRelNo,
                PoLineNo = row.PoLineNo,
                ICode = row.ICode,
                IDesc = row.IDesc ?? string.Empty,
                FrPurQty = row.AvailableQty,
                ToRecvQty = row.AvailableQty,
                Uom = row.PurchaseUom ?? string.Empty,
                StdUom = row.StdUom,
                PackSz = row.PackSz,
                ToWarehouse = warehouse,
                ToLocation = location,
                ToLotNo = lotNo,
                IStatus = DefaultItemStatus(),
                LotControl = row.LotControl,
                DefLocation = row.DefLocation
            });
            added++;
        }

        if (added == 0)
        {
            PoPickerError = "Selected PO lines are already on this receipt.";
            return;
        }

        RenumberLines();
        _isDirty = true;
        StatusMessage = added == 1 ? "1 PO line added." : $"{added} PO lines added.";
        PoPickerVisible = false;
        ErrorMessage = null;
    }

    protected async Task EditLineAsync(IvGoodsReceiptLineVm line)
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
        LinePopup = new IvGoodsReceiptLinePopupVm
        {
            PoNo = line.PoNo,
            PoRelNo = line.PoRelNo,
            PoLineNo = line.PoLineNo,
            ICode = line.ICode,
            IDesc = line.IDesc,
            FrPurQty = line.FrPurQty,
            ToRecvQty = line.ToRecvQty,
            ToWarehouse = line.ToWarehouse,
            ToLocation = line.ToLocation,
            ToLotNo = line.ToLotNo,
            IStatus = line.IStatus,
            ExpiryDate = line.ExpiryDate,
            Remarks = line.Remarks,
            LotControl = line.LotControl,
            Uom = line.Uom
        };
        _pendingDefLocation = string.IsNullOrWhiteSpace(line.ToLocation) ? line.DefLocation : line.ToLocation;
        LinePopupError = null;
        LinePopupVisible = true;

        if (IsStockGr && !string.IsNullOrWhiteSpace(line.ToWarehouse))
        {
            await OnLineWarehouseChangedAsync(line.ToWarehouse);
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

        if (args.Grid.GetDataItem(args.VisibleIndex) is IvGoodsReceiptLineVm line)
        {
            return EditLineAsync(line);
        }

        return Task.CompletedTask;
    }

    protected async Task OnLineWarehouseChangedAsync(string? warehouse)
    {
        var previousLocation = LinePopup.ToLocation;
        LinePopup.ToWarehouse = warehouse ?? string.Empty;
        LinePopup.ToLocation = string.Empty;
        Locations = [];

        if (string.IsNullOrWhiteSpace(LinePopup.ToWarehouse))
        {
            LocationsLoading = false;
            _pendingDefLocation = null;
            return;
        }

        var version = Interlocked.Increment(ref _locationLoadVersion);
        LocationsLoading = true;
        var result = await Lookups.ListActiveLocationsAsync(LinePopup.ToWarehouse);
        if (version != _locationLoadVersion)
        {
            return;
        }

        LocationsLoading = false;
        if (!result.Succeeded)
        {
            LinePopupError = result.ErrorMessage;
            Locations = [];
            return;
        }

        Locations = result.Rows;
        if (!string.IsNullOrWhiteSpace(previousLocation)
            && Locations.Any(x => string.Equals(x.Code, previousLocation, StringComparison.OrdinalIgnoreCase)))
        {
            LinePopup.ToLocation = previousLocation;
        }
        else if (!string.IsNullOrWhiteSpace(_pendingDefLocation)
            && Locations.Any(x => string.Equals(x.Code, _pendingDefLocation, StringComparison.OrdinalIgnoreCase)))
        {
            LinePopup.ToLocation = _pendingDefLocation;
        }

        _pendingDefLocation = null;
    }

    protected async Task OnGenerateLotAsync()
    {
        LinePopupError = null;
        if (!LinePopup.LotControl || string.IsNullOrWhiteSpace(LinePopup.ICode))
        {
            return;
        }

        var reserved = Lines
            .Where(x => x != _editingLine
                && string.Equals(x.ICode, LinePopup.ICode, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.ToLotNo))
            .Select(x => x.ToLotNo)
            .ToList();

        var result = await GoodsReceipt.AllocateLotAsync(
            LinePopup.ICode,
            reserved,
            IsEditMode ? BatchNo : null);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.AllocatedLotNo))
        {
            LinePopupError = result.ErrorMessage ?? "Unable to allocate lot number.";
            return;
        }

        LinePopup.ToLotNo = result.AllocatedLotNo;
    }

    protected void CommitLineEdit()
    {
        LinePopupError = ValidateStockReceiveLine(
            LinePopup.ToRecvQty,
            LinePopup.FrPurQty,
            LinePopup.ToWarehouse,
            LinePopup.ToLocation,
            LinePopup.ToLotNo,
            LinePopup.IStatus,
            LinePopup.ExpiryDate,
            LinePopup.LotControl,
            locationsLoaded: true,
            locationOptions: Locations);
        if (LinePopupError is not null || _editingLine is null)
        {
            return;
        }

        ApplyPopupToLine(_editingLine);
        LinePopupVisible = false;
        LinePopupError = null;
        ErrorMessage = null;
        _editingLine = null;
        _isDirty = true;
    }

    protected void CancelLineEdit()
    {
        LinePopupVisible = false;
        LinePopupError = null;
        _editingLine = null;
    }

    protected void RemoveLine(IvGoodsReceiptLineVm line)
    {
        if (!CanEditDocument)
        {
            return;
        }

        if (_editingLine == line)
        {
            CancelLineEdit();
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
        if (!await AccessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Add at least one receipt line from a purchase order.";
            return;
        }

        foreach (var line in Lines)
        {
            var error = ValidateStockReceiveLine(
                line.ToRecvQty,
                line.FrPurQty,
                line.ToWarehouse,
                line.ToLocation,
                line.ToLotNo,
                line.IStatus,
                line.ExpiryDate,
                line.LotControl,
                locationsLoaded: false,
                locationOptions: null);
            if (error is not null)
            {
                ErrorMessage = $"Line {line.LineNo}: {error}";
                return;
            }
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var request = new IvGoodsReceiptSaveRequest
            {
                TrxType = Header.TrxType,
                TrxDate = Header.TrxDate,
                RefNo = Header.RefNo,
                Remark = Header.Remark,
                Lines = Lines.Select(x => new IvGoodsReceiptLineRequest
                {
                    PoNo = x.PoNo,
                    PoRelNo = x.PoRelNo,
                    PoLineNo = x.PoLineNo,
                    ToWarehouse = x.ToWarehouse,
                    ToLocation = string.IsNullOrWhiteSpace(x.ToLocation) ? null : x.ToLocation,
                    ToLotNo = string.IsNullOrWhiteSpace(x.ToLotNo) ? null : x.ToLotNo,
                    ToRecvQty = x.ToRecvQty,
                    IStatus = x.IStatus,
                    ExpiryDate = x.ExpiryDate,
                    Remarks = x.Remarks
                }).ToList()
            };

            var result = IsNewMode
                ? await GoodsReceipt.SaveNewAsync(request)
                : await GoodsReceipt.UpdateAsync(BatchNo!.Value, request);

            if (result.Succeeded)
            {
                _isDirty = false;
                Navigation.NavigateTo("/inventory/goods-receipts");
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "Unable to save goods receipt.";
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

        Navigation.NavigateTo("/inventory/goods-receipts");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/goods-receipts");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (BatchNo is null or <= 0)
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/goods-receipts/edit/{BatchNo.Value}");
    }

    protected void OnKeepEditing() => ConfirmDiscardVisible = false;

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/inventory/goods-receipts");
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string TrxTypeLabel(string? trxType) =>
        string.Equals(trxType, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase)
            ? "Indirect NG"
            : "Stock GR";

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return "mr-status is-on";
        }

        if (string.Equals(status, IvItemStatuses.Damaged, StringComparison.OrdinalIgnoreCase))
        {
            return "mr-status is-off";
        }

        return "mr-status is-hold";
    }

    private async Task<bool> CanMaintainLinesAsync()
    {
        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, permission))
        {
            ErrorMessage = "Access denied.";
            return false;
        }

        return true;
    }

    private string? ValidateStockReceiveLine(
        decimal toRecvQty,
        decimal frPurQty,
        string? toWarehouse,
        string? toLocation,
        string? toLotNo,
        string? iStatus,
        DateTime? expiryDate,
        bool lotControl,
        bool locationsLoaded,
        IReadOnlyList<IvCodeLookupRow>? locationOptions)
    {
        if (toRecvQty <= 0m)
        {
            return "Receive quantity must be greater than zero.";
        }

        if (toRecvQty > frPurQty)
        {
            return $"Receive quantity cannot exceed the balance ({frPurQty:n4}).";
        }

        if (!IsStockGr)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(toWarehouse))
        {
            return "Warehouse is required.";
        }

        if (locationsLoaded && locationOptions is { Count: > 0 } && string.IsNullOrWhiteSpace(toLocation))
        {
            return "Location is required.";
        }

        if (locationsLoaded
            && locationOptions is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(toLocation)
            && !locationOptions.Any(x => string.Equals(x.Code, toLocation, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Location '{toLocation}' is not valid for warehouse '{toWarehouse}'.";
        }

        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return "Item status is required.";
        }

        if (!string.IsNullOrWhiteSpace(toLotNo) && toLotNo.Trim().Length > 50)
        {
            return "Lot number must be at most 50 characters.";
        }

        if (lotControl)
        {
            if (expiryDate is null)
            {
                return "Expiry date is required for this item.";
            }

            if (expiryDate.Value.Date < AppToday.Date)
            {
                return "Expiry date cannot be earlier than today.";
            }
        }

        return null;
    }

    private void ApplyPopupToLine(IvGoodsReceiptLineVm line)
    {
        line.ToRecvQty = LinePopup.ToRecvQty;
        line.ToWarehouse = (LinePopup.ToWarehouse ?? string.Empty).Trim();
        line.ToLocation = (LinePopup.ToLocation ?? string.Empty).Trim();
        line.ToLotNo = LinePopup.LotControl ? (LinePopup.ToLotNo ?? string.Empty).Trim() : string.Empty;
        line.IStatus = (LinePopup.IStatus ?? IvItemStatuses.Active).Trim().ToUpperInvariant();
        line.ExpiryDate = LinePopup.LotControl ? LinePopup.ExpiryDate : null;
        line.Remarks = string.IsNullOrWhiteSpace(LinePopup.Remarks) ? null : LinePopup.Remarks.Trim();
    }

    private void RenumberLines()
    {
        short n = 1;
        foreach (var line in Lines)
        {
            line.LineNo = n++;
        }
    }

    private string DefaultItemStatus() =>
        Statuses.Any(x => string.Equals(x.Code, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
            ? IvItemStatuses.Active
            : (Statuses.FirstOrDefault()?.Code ?? IvItemStatuses.Active);

    private string ResolveDefaultWarehouse(string? poWarehouse, string? defWarehouse)
    {
        foreach (var candidate in new[] { poWarehouse, defWarehouse })
        {
            var code = (candidate ?? string.Empty).Trim();
            if (code.Length == 0)
            {
                continue;
            }

            if (Warehouses.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                return code;
            }
        }

        return string.Empty;
    }

    private async Task<string> ResolveDefaultLocationAsync(string warehouse, string? defLocation)
    {
        if (string.IsNullOrWhiteSpace(warehouse) || string.IsNullOrWhiteSpace(defLocation))
        {
            return string.Empty;
        }

        var result = await Lookups.ListActiveLocationsAsync(warehouse);
        if (!result.Succeeded)
        {
            return string.Empty;
        }

        return result.Rows.Any(x => string.Equals(x.Code, defLocation, StringComparison.OrdinalIgnoreCase))
            ? defLocation.Trim()
            : string.Empty;
    }

    private static string NormalizeTrxType(string? trxType) =>
        string.Equals((trxType ?? string.Empty).Trim(), IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase)
            ? IvTrxTypes.NonStockGoodsReceive
            : IvTrxTypes.GoodsReceive;

    private static IvGoodsReceiptHeaderVm CreateHeader() =>
        new()
        {
            TrxDate = DateTime.Today,
            TrxType = IvTrxTypes.GoodsReceive,
            RefNo = "AUTO"
        };
}

public sealed record IvGoodsReceiptTrxTypeOption(string Code, string Label);

public sealed class IvGoodsReceiptHeaderVm
{
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string TrxType { get; set; } = IvTrxTypes.GoodsReceive;
    public string RefNo { get; set; } = "AUTO";
    public string? Remark { get; set; }
}

public sealed class IvGoodsReceiptLineVm
{
    public short LineNo { get; set; }
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public decimal FrPurQty { get; set; }
    public decimal ToRecvQty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string? StdUom { get; set; }
    public decimal PackSz { get; set; }
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
    public string? DefLocation { get; set; }

    public string LineKey => BuildLineKey(PoNo, PoRelNo, PoLineNo);
    public decimal ToStdQty => PoOrderCalc.ComputeStdQty(ToRecvQty, PackSz);
    public decimal Amount => decimal.Round(ToRecvQty * UnitPrice, 2);

    public static string BuildLineKey(string poNo, short poRelNo, short poLineNo) =>
        $"{poNo}|{poRelNo}|{poLineNo}";
}

public sealed class IvGoodsReceiptLinePopupVm
{
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public decimal FrPurQty { get; set; }
    public decimal ToRecvQty { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public DateTime? ExpiryDate { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
    public string Uom { get; set; } = string.Empty;
}

public sealed class IvGoodsReceiptPoPickerRow
{
    public string RowKey { get; init; } = string.Empty;
    public string PoNo { get; init; } = string.Empty;
    public short PoRelNo { get; init; }
    public short PoLineNo { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? VendCode { get; init; }
    public string? VendName { get; init; }
    public decimal BalanceQty { get; init; }
    public decimal DraftQty { get; init; }
    public decimal AvailableQty { get; init; }
    public string? PurchaseUom { get; init; }
    public string? StdUom { get; init; }
    public decimal PackSz { get; init; }
    public string? ToWarehouse { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public bool LotControl { get; init; }

    public static IvGoodsReceiptPoPickerRow FromLookup(IvGoodsReceiptPoLineLookupRow row) =>
        new()
        {
            RowKey = IvGoodsReceiptLineVm.BuildLineKey(row.PoNo, row.PoRelNo, row.PoLineNo),
            PoNo = row.PoNo,
            PoRelNo = row.PoRelNo,
            PoLineNo = row.PoLineNo,
            ICode = row.ICode,
            IDesc = row.IDesc,
            VendCode = row.VendCode,
            VendName = row.VendName,
            BalanceQty = row.BalanceQty,
            DraftQty = row.DraftQty,
            AvailableQty = row.AvailableQty,
            PurchaseUom = row.PurchaseUom,
            StdUom = row.StdUom,
            PackSz = row.PackSz,
            ToWarehouse = row.ToWarehouse,
            DefWarehouse = row.DefWarehouse,
            DefLocation = row.DefLocation,
            LotControl = row.LotControl
        };
}
