using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Inventory.Lookups;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvStockTransfer : PageBase
{
    /// <summary>
    /// Navigation mode: "new", "edit", "view", or empty (treated as view when BatchNo is set).
    /// </summary>
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public int? BatchNo { get; set; }

    [Inject] private IIvStockTransferService StockTransferService { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool SplitPopupVisible;
    protected bool ConfirmDiscardVisible;
    protected string? PopupError;
    protected string? SplitPopupError;
    protected string BatchNoDisplay = "AUTO";
    protected string BatchStatusDisplay = IvBatchStatuses.New;
    protected bool LocationsLoading;
    protected bool CanEditPermission;

    private int _locationLoadVersion;
    private string? _pendingDefLocation;

    private IvStockTransferLineVm? _editingLine;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private bool _isDirty;

    protected IvStockTransferHeaderVm Header { get; set; } = CreateHeader();
    protected List<IvStockTransferLineVm> Lines { get; set; } = [];
    protected IvStockTransferPopupVm Popup { get; set; } = new();
    protected IvStockTransferSplitPopupVm SplitPopup { get; set; } = new();
    protected IvStockTransferLineVm? SplitSourceLine { get; set; }
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; set; } = [];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// View mode when explicitly "view", or when BatchNo supplied with no mode (direct URL).
    /// </summary>
    protected bool IsViewMode => !IsNewMode && !IsEditMode;

    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && string.Equals(BatchStatusDisplay, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase)
        && BatchNo is > 0;

    protected string PageHeading => IsNewMode
        ? "New Stock transfer"
        : IsEditMode
            ? "Edit Stock transfer"
            : "View Stock transfer";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected decimal DocumentTotal => Lines.Sum(x => x.Amount);
    protected decimal PopupAmount => decimal.Round(Popup.Quantity * Popup.UnitPrice, 2);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit transfer line" : "Add transfer line";
    protected string PopupPrimaryText => IsEditingLine ? "Update item" : "Add item";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";

    /// <summary>
    /// Maximum quantity that can be issued from the currently selected balance location,
    /// accounting for other lines in the document that reference the same FromBalLocId.
    /// Informational only — server enforces final validation.
    /// </summary>
    protected decimal PopupMaxIssueQty
    {
        get
        {
            if (Popup.FromBalLocId <= 0)
            {
                return 0m;
            }

            var otherLinesQty = Lines
                .Where(x => x != _editingLine && x.FromBalLocId == Popup.FromBalLocId)
                .Sum(x => x.Quantity);
            return Math.Max(0m, Popup.AvailableQty - otherLinesQty);
        }
    }

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Edit);
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
        SplitPopupVisible = false;
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
                Navigation.NavigateTo("/inventory/stock-transfer");
                return;
            }

            var result = await StockTransferService.GetAsync(BatchNo.Value);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load Stock transfer.";
                Navigation.NavigateTo("/inventory/stock-transfer");
                return;
            }

            ApplyDocument(result.Document);

            if (IsEditMode
                && !string.Equals(result.Document.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/inventory/stock-transfer/view/{result.Document.BatchNo}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDocument(IvStockTransferDocument doc)
    {
        BatchNoDisplay = doc.BatchNo.ToString();
        BatchStatusDisplay = doc.BatchStatus;
        Header = new IvStockTransferHeaderVm
        {
            TrxDate = doc.TrxDate == default ? DateTime.Today : doc.TrxDate.Date,
            RefNo = doc.RefNo ?? string.Empty,
            Remark = doc.Remark
        };
        Lines = doc.Lines.Select(x => new IvStockTransferLineVm
        {
            LineNo = x.LineNo,
            FromBalLocId = x.FromBalLocId,
            ICode = x.ICode,
            IDesc = x.IDesc ?? string.Empty,
            FrWarehouse = x.FrWarehouse,
            FrLocation = x.FrLocation ?? string.Empty,
            FrLotNo = x.FrLotNo ?? string.Empty,
            ToWarehouse = x.ToWarehouse,
            ToLocation = x.ToLocation ?? string.Empty,
            ToLotNo = x.ToLotNo ?? string.Empty,
            OriginalToLotNo = x.ToLotNo ?? string.Empty,
            LotControl = x.LotControl,
            AvailableQty = x.AvailableQty,
            Quantity = x.Quantity,
            Uom = x.Uom ?? string.Empty,
            IClassCode = x.IClassCode ?? string.Empty,
            IStatus = x.IStatus,
            UnitPrice = x.UnitPrice,
            Remarks = x.Remarks
        }).ToList();
    }

    private async Task LoadLookupsAsync()
    {
        var wh = await Lookups.ListActiveWarehousesAsync();
        if (!wh.Succeeded)
        {
            ErrorMessage = wh.ErrorMessage ?? "Unable to load warehouses.";
            Warehouses = [];
            return;
        }

        Warehouses = wh.Rows;
    }

    private async Task RefreshPeekBatchNoAsync()
    {
        var result = await StockTransferService.PeekNextBatchNoAsync();
        BatchNoDisplay = result.Succeeded ? result.PeekBatchNo.ToString() : "AUTO";
    }

    protected async Task OnNewLineClickAsync()
    {
        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        _editingLine = null;
        Popup = new IvStockTransferPopupVm();
        Locations = [];
        _pendingDefLocation = null;
        PopupError = null;
        PopupVisible = true;
    }

    protected async Task EditLineAsync(IvStockTransferLineVm line)
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
        Popup = new IvStockTransferPopupVm
        {
            ICode = line.ICode,
            IDesc = line.IDesc,
            IClassCode = line.IClassCode,
            FromBalLocId = line.FromBalLocId,
            BalLocDisplay = string.IsNullOrWhiteSpace(line.FrLotNo) ? $"#{line.FromBalLocId}" : line.FrLotNo,
            FrWarehouse = line.FrWarehouse,
            FrLocation = line.FrLocation,
            FrLotNo = line.FrLotNo,
            ToWarehouse = line.ToWarehouse,
            ToLocation = line.ToLocation,
            ToLotNo = line.ToLotNo,
            OriginalToLotNo = line.OriginalToLotNo,
            LotControl = line.LotControl,
            AvailableQty = line.AvailableQty,
            Uom = line.Uom,
            IStatus = line.IStatus,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            Remarks = line.Remarks
        };
        _pendingDefLocation = line.ToLocation;
        await OnWarehouseChangedAsync(line.ToWarehouse);
        if (!string.IsNullOrWhiteSpace(_pendingDefLocation))
        {
            Popup.ToLocation = _pendingDefLocation;
            _pendingDefLocation = null;
        }
        PopupError = null;
        PopupVisible = true;
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
        if (!string.IsNullOrWhiteSpace(_pendingDefLocation)
            && Locations.Any(x => string.Equals(x.Code, _pendingDefLocation, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.ToLocation = _pendingDefLocation;
            _pendingDefLocation = null;
        }
    }

    protected Task OnLineRowDoubleClick(GridRowClickEventArgs args)
    {
        if (!CanEditDocument)
        {
            return Task.CompletedTask;
        }

        if (args.Grid.GetDataItem(args.VisibleIndex) is IvStockTransferLineVm line)
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

        // Clear previous balance location selection when item changes
        Popup.FromBalLocId = 0;
        Popup.BalLocDisplay = null;
        Popup.FrWarehouse = string.Empty;
        Popup.FrLocation = string.Empty;
        Popup.FrLotNo = string.Empty;
        Popup.AvailableQty = 0m;
        Popup.Uom = string.Empty;
        Popup.IStatus = string.Empty;
    }

    protected Task OnBalLocSelectedAsync(IvBalLocLookupRow row)
    {
        Popup.FromBalLocId = row.Id;
        Popup.BalLocDisplay = string.IsNullOrWhiteSpace(row.LotNo) ? $"#{row.Id}" : row.LotNo;
        Popup.FrWarehouse = row.WhCode;
        Popup.FrLocation = row.LocCode;
        Popup.FrLotNo = row.LotNo;
        Popup.AvailableQty = row.StdQty;
        Popup.Uom = row.StdUom ?? string.Empty;
        Popup.IStatus = row.IStatus;
        Popup.LotControl = row.LotControl;

        if (string.IsNullOrWhiteSpace(Popup.IClassCode) && !string.IsNullOrWhiteSpace(row.IClassCode))
        {
            Popup.IClassCode = row.IClassCode;
        }

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
            var line = new IvStockTransferLineVm { LineNo = (short)(Lines.Count + 1) };
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

    protected void RemoveLine(IvStockTransferLineVm line)
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
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockTransfer, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Add at least one transfer line.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var request = new IvStockTransferSaveRequest
            {
                TrxDate = Header.TrxDate,
                RefNo = Header.RefNo,
                Remark = Header.Remark,
                Lines = Lines.Select(x => new IvStockTransferLineRequest
                {
                    FromBalLocId = x.FromBalLocId,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    FrWarehouse = x.FrWarehouse,
                    FrLocation = x.FrLocation,
                    FrLotNo = x.FrLotNo,
                    ToWarehouse = x.ToWarehouse,
                    ToLocation = x.ToLocation,
                    ToLotNo = x.ToLotNo,
                    LineNo = x.LineNo,
                    OriginalToLotNo = x.OriginalToLotNo,
                    Quantity = x.Quantity,
                    Uom = x.Uom,
                    IClassCode = x.IClassCode,
                    IStatus = x.IStatus,
                    UnitPrice = x.UnitPrice,
                    Remarks = x.Remarks
                }).ToList()
            };

            var result = IsNewMode
                ? await StockTransferService.SaveNewAsync(request)
                : await StockTransferService.UpdateAsync(BatchNo!.Value, request);

            if (result.Succeeded)
            {
                _isDirty = false;
                Navigation.NavigateTo("/inventory/stock-transfer");
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "Unable to save Stock transfer.";
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

        Navigation.NavigateTo("/inventory/stock-transfer");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/stock-transfer");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (BatchNo is null or <= 0)
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/stock-transfer/edit/{BatchNo.Value}");
    }

    protected void OnKeepEditing() => ConfirmDiscardVisible = false;

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/inventory/stock-transfer");
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return "tr-status is-on";
        }

        if (string.Equals(status, IvItemStatuses.Damaged, StringComparison.OrdinalIgnoreCase))
        {
            return "tr-status is-off";
        }

        return "tr-status is-hold";
    }

    private async Task<bool> CanMaintainLinesAsync()
    {
        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryStockTransfer, permission))
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

        if (Popup.FromBalLocId <= 0)
        {
            return "Transfer-from location is required. Use the search button to pick a balance location.";
        }

        if (string.IsNullOrWhiteSpace(Popup.FrWarehouse))
        {
            return "Warehouse is required.";
        }

        if (string.IsNullOrWhiteSpace(Popup.ToWarehouse))
        {
            return "Destination warehouse is required.";
        }

        if (Locations.Count > 0 && string.IsNullOrWhiteSpace(Popup.ToLocation))
        {
            return "Destination location is required.";
        }

        if (Popup.Quantity <= 0)
        {
            return "Transfer quantity must be greater than zero.";
        }

        if (Popup.UnitPrice < 0)
        {
            return "Unit price cannot be negative.";
        }

        if (Popup.LotControl)
        {
            if (string.IsNullOrWhiteSpace(Popup.ToLotNo))
            {
                return "Destination lot number is required.";
            }

            if (string.Equals(Popup.FrLotNo.Trim(), Popup.ToLotNo.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "Destination lot must differ from source lot.";
            }
        }

        return null;
    }

    private void ApplyPopupToLine(IvStockTransferLineVm line)
    {
        line.ICode = Popup.ICode.Trim();
        line.IDesc = Popup.IDesc.Trim();
        line.IClassCode = Popup.IClassCode.Trim();
        line.FromBalLocId = Popup.FromBalLocId;
        line.FrWarehouse = Popup.FrWarehouse.Trim();
        line.FrLocation = (Popup.FrLocation ?? string.Empty).Trim();
        line.FrLotNo = (Popup.FrLotNo ?? string.Empty).Trim();
        line.ToWarehouse = Popup.ToWarehouse.Trim();
        line.ToLocation = (Popup.ToLocation ?? string.Empty).Trim();
        line.ToLotNo = Popup.LotControl ? Popup.ToLotNo.Trim() : string.Empty;
        line.LotControl = Popup.LotControl;
        if (string.IsNullOrWhiteSpace(line.OriginalToLotNo) && !string.IsNullOrWhiteSpace(line.ToLotNo))
        {
            line.OriginalToLotNo = line.ToLotNo;
        }
        line.AvailableQty = Popup.AvailableQty;
        line.Quantity = Popup.Quantity;
        line.Uom = Popup.Uom.Trim();
        line.IStatus = Popup.IStatus.Trim().ToUpperInvariant();
        line.UnitPrice = Popup.UnitPrice;
        line.Remarks = string.IsNullOrWhiteSpace(Popup.Remarks) ? null : Popup.Remarks.Trim();
    }

    protected bool CanSplitLine(IvStockTransferLineVm line) =>
        CanEditDocument
        && line.LotControl
        && !string.IsNullOrWhiteSpace(line.FrLotNo)
        && line.Quantity > 0;

    protected async Task OpenSplitDialogAsync(IvStockTransferLineVm line)
    {
        if (!CanSplitLine(line) || !await CanMaintainLinesAsync())
        {
            return;
        }

        SplitSourceLine = line;
        SplitPopup = new IvStockTransferSplitPopupVm
        {
            ICode = line.ICode,
            IDesc = line.IDesc,
            OriginQty = line.Quantity,
            ToWarehouse = line.ToWarehouse,
            ToLocation = line.ToLocation,
            SplitCount = 2,
            AutoGenerate = true,
            RunNo = 1,
            NewLotNoStart = DateTime.Today.ToString("yyMMdd") + "001"
        };
        SplitPopupError = null;
        SplitPopupVisible = true;
        await OnSplitWarehouseChangedAsync(line.ToWarehouse);
        if (!string.IsNullOrWhiteSpace(line.ToLocation))
        {
            SplitPopup.ToLocation = line.ToLocation;
        }
    }

    protected void OnSplitPopupCancel()
    {
        SplitPopupVisible = false;
        SplitPopupError = null;
        SplitSourceLine = null;
    }

    protected async Task OnSplitWarehouseChangedAsync(string? warehouse)
    {
        SplitPopup.ToWarehouse = warehouse ?? string.Empty;
        SplitPopup.ToLocation = string.Empty;
        Locations = [];

        if (string.IsNullOrWhiteSpace(SplitPopup.ToWarehouse))
        {
            LocationsLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _locationLoadVersion);
        LocationsLoading = true;
        var result = await Lookups.ListActiveLocationsAsync(SplitPopup.ToWarehouse);
        if (version != _locationLoadVersion)
        {
            return;
        }

        LocationsLoading = false;
        if (!result.Succeeded)
        {
            SplitPopupError = result.ErrorMessage;
            Locations = [];
            return;
        }

        Locations = result.Rows;
    }

    protected async Task OnGenerateDestLotAsync()
    {
        PopupError = null;
        if (!Popup.LotControl || string.IsNullOrWhiteSpace(Popup.ICode))
        {
            return;
        }

        try
        {
            var prefix = DateTime.Today.ToString("yyMMdd");
            var lots = await IvLotNumberGenerator.AllocateAsync(
                1,
                prefix,
                1,
                autoGenerate: true,
                Lines.Where(x => x != _editingLine).Select(x => x.ToLotNo).ToList(),
                lot => Lookups.LotExistsAsync(Popup.ICode, lot));
            Popup.ToLotNo = lots[0];
        }
        catch (Exception ex)
        {
            PopupError = ex.Message;
        }
    }

    protected async Task OnSplitProcessAsync()
    {
        SplitPopupError = ValidateSplitPopup();
        if (SplitPopupError is not null || SplitSourceLine is null)
        {
            return;
        }

        try
        {
            var prefix = SplitPopup.AutoGenerate
                ? DateTime.Today.ToString("yyMMdd")
                : SplitPopup.NewLotNoStart.Trim();
            var used = Lines
                .Where(x => x != SplitSourceLine)
                .Select(x => x.ToLotNo)
                .ToList();
            var lotNos = await IvLotNumberGenerator.AllocateAsync(
                SplitPopup.SplitCount,
                prefix,
                SplitPopup.RunNo,
                SplitPopup.AutoGenerate,
                used,
                lot => Lookups.LotExistsAsync(SplitSourceLine.ICode, lot));

            var quantities = SplitEqualQuantities(SplitSourceLine.Quantity, SplitPopup.SplitCount);
            var source = SplitSourceLine;
            Lines.Remove(source);

            for (var i = 0; i < SplitPopup.SplitCount; i++)
            {
                Lines.Add(new IvStockTransferLineVm
                {
                    FromBalLocId = source.FromBalLocId,
                    ICode = source.ICode,
                    IDesc = source.IDesc,
                    IClassCode = source.IClassCode,
                    FrWarehouse = source.FrWarehouse,
                    FrLocation = source.FrLocation,
                    FrLotNo = source.FrLotNo,
                    ToWarehouse = SplitPopup.ToWarehouse.Trim(),
                    ToLocation = (SplitPopup.ToLocation ?? string.Empty).Trim(),
                    ToLotNo = lotNos[i],
                    OriginalToLotNo = lotNos[i],
                    LotControl = true,
                    AvailableQty = source.AvailableQty,
                    Quantity = quantities[i],
                    Uom = source.Uom,
                    IStatus = source.IStatus,
                    UnitPrice = source.UnitPrice,
                    Remarks = source.Remarks
                });
            }

            RenumberLines();
            _isDirty = true;
            SplitPopupVisible = false;
            SplitSourceLine = null;
            SplitPopupError = null;
        }
        catch (Exception ex)
        {
            SplitPopupError = ex.Message;
        }
    }

    private string? ValidateSplitPopup()
    {
        if (SplitPopup.SplitCount < 2)
        {
            return "Split count must be at least 2.";
        }

        if (SplitPopup.SplitCount > 99)
        {
            return "Split count cannot exceed 99.";
        }

        if (string.IsNullOrWhiteSpace(SplitPopup.ToWarehouse))
        {
            return "Destination warehouse is required.";
        }

        if (Locations.Count > 0 && string.IsNullOrWhiteSpace(SplitPopup.ToLocation))
        {
            return "Destination location is required.";
        }

        if (!SplitPopup.AutoGenerate && string.IsNullOrWhiteSpace(SplitPopup.NewLotNoStart))
        {
            return "New lot number start is required when auto generate is off.";
        }

        return null;
    }

    private static IReadOnlyList<decimal> SplitEqualQuantities(decimal originQty, int count)
    {
        var scale = IvQty.Scale;
        var factor = (decimal)Math.Pow(10, scale);
        var baseQty = decimal.Floor(originQty / count * factor) / factor;
        var result = new decimal[count];
        for (var i = 0; i < count - 1; i++)
        {
            result[i] = baseQty;
        }

        result[count - 1] = IvQty.Round(originQty - baseQty * (count - 1));
        return result;
    }

    private void RenumberLines()
    {
        short n = 1;
        foreach (var line in Lines)
        {
            line.LineNo = n++;
        }
    }

    private static IvStockTransferHeaderVm CreateHeader() =>
        new()
        {
            TrxDate = DateTime.Today,
            RefNo = "AUTO"
        };
}

public sealed class IvStockTransferHeaderVm
{
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string RefNo { get; set; } = "AUTO";
    public string? Remark { get; set; }
}

public sealed class IvStockTransferLineVm
{
    public short LineNo { get; set; }
    public int FromBalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public string FrWarehouse { get; set; } = string.Empty;
    public string FrLocation { get; set; } = string.Empty;
    public string FrLotNo { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public string? OriginalToLotNo { get; set; }
    public bool LotControl { get; set; }
    public decimal AvailableQty { get; set; }
    public decimal Quantity { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public string? Remarks { get; set; }
    public decimal Amount => decimal.Round(Quantity * UnitPrice, 2);
}

public sealed class IvStockTransferPopupVm
{
    public string ICode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string IDesc { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public int FromBalLocId { get; set; }

    /// <summary>Display text shown in the IvBalLocPicker (lot number or "#id").</summary>
    public string? BalLocDisplay { get; set; }

    public string FrWarehouse { get; set; } = string.Empty;
    public string FrLocation { get; set; } = string.Empty;
    public string FrLotNo { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string ToLotNo { get; set; } = string.Empty;
    public string? OriginalToLotNo { get; set; }
    public decimal AvailableQty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public bool LotControl { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Remarks { get; set; }
}

public sealed class IvStockTransferSplitPopupVm
{
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public decimal OriginQty { get; set; }
    public string ToWarehouse { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public int SplitCount { get; set; } = 2;
    public bool AutoGenerate { get; set; } = true;
    public string NewLotNoStart { get; set; } = string.Empty;
    public int RunNo { get; set; } = 1;
}
