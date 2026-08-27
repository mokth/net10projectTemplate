using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvMiscIssue : PageBase
{
    /// <summary>
    /// Navigation mode: "new", "edit", "view", or empty (treated as view when BatchNo is set).
    /// </summary>
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public int? BatchNo { get; set; }

    [Inject] private IIvMiscIssueService MiscIssue { get; set; } = default!;
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
    protected bool CanEditPermission;

    private IvMiscIssueLineVm? _editingLine;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private bool _isDirty;

    protected IvMiscIssueHeaderVm Header { get; set; } = CreateHeader();
    protected List<IvMiscIssueLineVm> Lines { get; set; } = [];
    protected IvMiscIssuePopupVm Popup { get; set; } = new();

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
        ? "New miscellaneous issue"
        : IsEditMode
            ? "Edit miscellaneous issue"
            : "View miscellaneous issue";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected decimal DocumentTotal => Lines.Sum(x => x.Amount);
    protected decimal PopupAmount => decimal.Round(Popup.Quantity * Popup.UnitPrice, 2);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit issue line" : "Add issue line";
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
            CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Edit);
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
                Navigation.NavigateTo("/inventory/misc-issue");
                return;
            }

            var result = await MiscIssue.GetAsync(BatchNo.Value);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load miscellaneous issue.";
                Navigation.NavigateTo("/inventory/misc-issue");
                return;
            }

            ApplyDocument(result.Document);

            if (IsEditMode
                && !string.Equals(result.Document.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/inventory/misc-issue/view/{result.Document.BatchNo}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDocument(IvMiscIssueDocument doc)
    {
        BatchNoDisplay = doc.BatchNo.ToString();
        BatchStatusDisplay = doc.BatchStatus;
        Header = new IvMiscIssueHeaderVm
        {
            TrxDate = doc.TrxDate == default ? DateTime.Today : doc.TrxDate.Date,
            RefNo = doc.RefNo ?? string.Empty,
            Remark = doc.Remark
        };
        Lines = doc.Lines.Select(x => new IvMiscIssueLineVm
        {
            LineNo = x.LineNo,
            FromBalLocId = x.FromBalLocId,
            ICode = x.ICode,
            IDesc = x.IDesc ?? string.Empty,
            FrWarehouse = x.FrWarehouse,
            FrLocation = x.FrLocation ?? string.Empty,
            FrLotNo = x.FrLotNo ?? string.Empty,
            AvailableQty = x.AvailableQty,
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

    private async Task RefreshPeekBatchNoAsync()
    {
        var result = await MiscIssue.PeekNextBatchNoAsync();
        BatchNoDisplay = result.Succeeded ? result.PeekBatchNo.ToString() : "AUTO";
    }

    protected async Task OnNewLineClickAsync()
    {
        if (!await CanMaintainLinesAsync())
        {
            return;
        }

        _editingLine = null;
        Popup = new IvMiscIssuePopupVm();
        PopupError = null;
        PopupVisible = true;
    }

    protected async Task EditLineAsync(IvMiscIssueLineVm line)
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
        Popup = new IvMiscIssuePopupVm
        {
            ICode = line.ICode,
            IDesc = line.IDesc,
            IClassCode = line.IClassCode,
            FromBalLocId = line.FromBalLocId,
            BalLocDisplay = string.IsNullOrWhiteSpace(line.FrLotNo) ? $"#{line.FromBalLocId}" : line.FrLotNo,
            FrWarehouse = line.FrWarehouse,
            FrLocation = line.FrLocation,
            FrLotNo = line.FrLotNo,
            AvailableQty = line.AvailableQty,
            Uom = line.Uom,
            IStatus = line.IStatus,
            ExpiryDate = line.ExpiryDate,
            LotControl = line.LotControl,
            Quantity = line.Quantity,
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

        if (args.Grid.GetDataItem(args.VisibleIndex) is IvMiscIssueLineVm line)
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
        Popup.ExpiryDate = null;
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
        Popup.ExpiryDate = row.ExpiryDate;
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
            var line = new IvMiscIssueLineVm { LineNo = (short)(Lines.Count + 1) };
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

    protected void RemoveLine(IvMiscIssueLineVm line)
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
        if (!await AccessRights.CanAsync(MenuCodes.InventoryMiscIssue, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        if (Lines.Count == 0)
        {
            ErrorMessage = "Add at least one issue line.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var request = new IvMiscIssueSaveRequest
            {
                TrxDate = Header.TrxDate,
                RefNo = Header.RefNo,
                Remark = Header.Remark,
                Lines = Lines.Select(x => new IvMiscIssueLineRequest
                {
                    FromBalLocId = x.FromBalLocId,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    FrWarehouse = x.FrWarehouse,
                    FrLocation = x.FrLocation,
                    FrLotNo = x.FrLotNo,
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
                ? await MiscIssue.SaveNewAsync(request)
                : await MiscIssue.UpdateAsync(BatchNo!.Value, request);

            if (result.Succeeded)
            {
                _isDirty = false;
                Navigation.NavigateTo("/inventory/misc-issue");
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "Unable to save miscellaneous issue.";
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

        Navigation.NavigateTo("/inventory/misc-issue");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/misc-issue");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (BatchNo is null or <= 0)
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/misc-issue/edit/{BatchNo.Value}");
    }

    protected void OnKeepEditing() => ConfirmDiscardVisible = false;

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/inventory/misc-issue");
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return "mi-status is-on";
        }

        if (string.Equals(status, IvItemStatuses.Damaged, StringComparison.OrdinalIgnoreCase))
        {
            return "mi-status is-off";
        }

        return "mi-status is-hold";
    }

    private async Task<bool> CanMaintainLinesAsync()
    {
        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryMiscIssue, permission))
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
            return "Issue-from location is required. Use the search button to pick a balance location.";
        }

        if (string.IsNullOrWhiteSpace(Popup.FrWarehouse))
        {
            return "Warehouse is required.";
        }

        if (Popup.Quantity <= 0)
        {
            return "Issue quantity must be greater than zero.";
        }

        if (Popup.UnitPrice < 0)
        {
            return "Unit price cannot be negative.";
        }

        return null;
    }

    private void ApplyPopupToLine(IvMiscIssueLineVm line)
    {
        line.ICode = Popup.ICode.Trim();
        line.IDesc = Popup.IDesc.Trim();
        line.IClassCode = Popup.IClassCode.Trim();
        line.FromBalLocId = Popup.FromBalLocId;
        line.FrWarehouse = Popup.FrWarehouse.Trim();
        line.FrLocation = (Popup.FrLocation ?? string.Empty).Trim();
        line.FrLotNo = (Popup.FrLotNo ?? string.Empty).Trim();
        line.AvailableQty = Popup.AvailableQty;
        line.Quantity = Popup.Quantity;
        line.Uom = Popup.Uom.Trim();
        line.IStatus = Popup.IStatus.Trim().ToUpperInvariant();
        line.UnitPrice = Popup.UnitPrice;
        line.ExpiryDate = Popup.ExpiryDate;
        line.Reason = string.IsNullOrWhiteSpace(Popup.Reason) ? null : Popup.Reason.Trim();
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

    private static IvMiscIssueHeaderVm CreateHeader() =>
        new()
        {
            TrxDate = DateTime.Today,
            RefNo = "AUTO"
        };
}

public sealed class IvMiscIssueHeaderVm
{
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string RefNo { get; set; } = "AUTO";
    public string? Remark { get; set; }
}

public sealed class IvMiscIssueLineVm
{
    public short LineNo { get; set; }
    public int FromBalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string IDesc { get; set; } = string.Empty;
    public string IClassCode { get; set; } = string.Empty;
    public string FrWarehouse { get; set; } = string.Empty;
    public string FrLocation { get; set; } = string.Empty;
    public string FrLotNo { get; set; } = string.Empty;
    public decimal AvailableQty { get; set; }
    public decimal Quantity { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = IvItemStatuses.Active;
    public decimal UnitPrice { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public bool LotControl { get; set; }
    public decimal Amount => decimal.Round(Quantity * UnitPrice, 2);
}

public sealed class IvMiscIssuePopupVm
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
    public decimal AvailableQty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }
    public bool LotControl { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
}
