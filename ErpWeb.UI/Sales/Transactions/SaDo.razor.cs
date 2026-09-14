using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaDo : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? DoNo { get; set; }

    [Inject] private ISaDoService Dos { get; set; } = default!;
    [Inject] private ISaSoService Sos { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private ISaSalesRefService SalesRefService { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool SoPickerVisible;
    protected bool SoPickerLoading;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCustChangeVisible;
    protected bool ConfirmShipOverwriteVisible;
    protected bool ShipEditorVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected string? SoPickerError;
    protected bool CanEditPermission;
    protected string DoNoDisplay = "AUTO";
    protected string StatusDisplay = SaDoStatuses.New;
    protected DateTime DoDate;
    protected string? CustCode;
    protected string? CustName;
    protected string? Prefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? TaxGrCode;
    protected string? SalesRep;
    protected string? Ref1;
    protected string? ProjId;
    protected string? PayCode;
    protected string? Remarks;
    protected string? ShipVia;
    protected string? InvName;
    protected string? InvAddress1;
    protected string? InvAddress2;
    protected string? InvAddress3;
    protected string? InvCity;
    protected string? InvState;
    protected string? InvPostalCode;
    protected string? InvCountry;
    protected string? InvTel;
    protected string? InvFax;
    protected string? ShipName;
    protected string? ShipAddress1;
    protected string? ShipAddress2;
    protected string? ShipAddress3;
    protected string? ShipCity;
    protected string? ShipState;
    protected string? ShipPostalCode;
    protected string? ShipCountry;
    protected string? ShipTel;
    protected string? ShipFax;
    protected decimal GrossAmnt;
    protected decimal Taxes;
    protected decimal TotAmnt;
    protected bool ShipmentComplete = true;
    protected bool DateShipmentWarning;
    protected bool NeedsShipment;
    protected int ActiveTabIndex;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private SaDoLineVm? _editingLine;
    private string? _loadedKey;
    private bool _isDirty;
    private bool _disposed;
    private bool _isApplyingDefaults;
    private bool _hasShipment;
    private string? _pendingCustCode;
    private string? _discountMethod;
    private bool _decPoint;
    private bool? _taxable;
    private byte[] _rowVersion = [];
    private int _customerApplySeq;
    private CancellationTokenSource _cts = new();
    private string? _shipConfirmMessage;
    private byte[]? _shipConfirmToken;
    private int? _shipEditLine;
    private int? _shipToLine;
    private IReadOnlyList<SaCustAddressVm> _shipToOptions = [];

    protected List<SaDoLineVm> Lines { get; set; } = [];
    protected List<SaDoCustomerLookupRow> Customers { get; set; } = [];
    protected List<SaDoItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<SaDoTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> SalesReps { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<IvCodeLookupRow> Projects { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected IReadOnlyList<SaCustAddressVm> ShipToOptions => _shipToOptions;
    protected int? ShipToLine => _shipToLine;
    protected SaDoLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }
    protected List<SaSoPickerOption> SoPickerOptions { get; set; } = [];
    protected List<SaSoLineDto> SoPickerLines { get; set; } = [];
    /// <summary>Typed selection. Grid event boundary maps <c>IReadOnlyList&lt;object&gt;</c> via <see cref="OnSelectedSoPickerLinesChanged"/>.</summary>
    protected IReadOnlyList<SaSoLineDto> SelectedSoPickerLines { get; set; } = [];
    protected string? SelectedSourceSoNo { get; set; }

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool CanEditAddresses =>
        CanEditDocument
        && string.Equals(StatusDisplay, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && string.Equals(StatusDisplay, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(DoNo);
    protected string PageHeading => IsNewMode ? "New delivery order" : IsEditMode ? "Edit delivery order" : "View delivery order";
    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected bool HasCustomer => !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanMutateLines => CanEditDocument && HasCustomer && CurrRateValid && !IsSubmitting;
    protected bool CanOpenSoPicker => CanMutateLines && HasCustomer;
    protected bool TaxGroupRequired => _taxable == true;
    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasCustomer
        && CurrRateValid
        && !string.IsNullOrWhiteSpace(PayCode)
        && (!TaxGroupRequired || !string.IsNullOrWhiteSpace(TaxGrCode));
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit line" : "Add line";
    protected string PopupPrimaryText => IsEditingLine ? "Update item" : "Add item";
    protected bool PopupInclusiveLocked =>
        Lines.Count > 1 || (_editingLine is null && Lines.Count > 0);
    protected SaInvoiceLineCalcState PopupCalc => BuildPopupCalc();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{DoNo}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConfirmDiscardVisible = false;
        ConfirmCustChangeVisible = false;
        ConfirmShipOverwriteVisible = false;
        ConcurrencyVisible = false;
        DateShipmentWarning = false;
        NeedsShipment = false;
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;
        ResetSoPicker();

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesDeliveryOrder, PermissionCodes.Edit);
        var lookups = await Dos.GetLookupsAsync(_cts.Token);
        if (_disposed)
        {
            return;
        }

        if (lookups.Succeeded)
        {
            Customers = lookups.Customers.ToList();
            Items = lookups.Items.ToList();
            Warehouses = lookups.Warehouses.ToList();
            TaxGroups = lookups.TaxGroups.ToList();
            PayCodes = lookups.PayCodes.ToList();
            Projects = lookups.Projects.ToList();
        }

        Countries = await Lookups.ListCountriesForAssignmentAsync(_cts.Token);
        States = await Lookups.ListStatesForAssignmentAsync(_cts.Token);
        var salesRepsResult = await SalesRefService.ListSalesRepsAsync(_cts.Token);
        if (salesRepsResult.Succeeded && salesRepsResult.Data is not null)
        {
            SalesReps = salesRepsResult.Data
                .Where(x => x.IsActive)
                .Select(x => new IvCodeLookupRow { Code = x.Code, Desc = x.Name })
                .ToList();
        }

        if (_disposed)
        {
            return;
        }

        if (IsNewMode)
        {
            ResetNewDocument();
            IsLoading = false;
            return;
        }

        var result = await Dos.GetAsync(DoNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Delivery order was not found.";
            if (result.ErrorKind == SaDoErrorKind.NotFound)
            {
                Navigation.NavigateTo("/sales/delivery-orders");
            }

            IsLoading = false;
            return;
        }

        ApplyDocument(result.Document);
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        IsLoading = false;
    }

    private void ResetNewDocument()
    {
        DoNo = null;
        DoNoDisplay = "AUTO";
        StatusDisplay = SaDoStatuses.New;
        DoDate = Dates.Today.Date;
        CustCode = null;
        CustName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        TaxGrCode = null;
        SalesRep = null;
        Ref1 = null;
        ProjId = null;
        PayCode = null;
        Remarks = null;
        ShipVia = null;
        ClearAddresses();
        ClearShipToState();
        Lines = [];
        GrossAmnt = 0;
        Taxes = 0;
        TotAmnt = 0;
        ShipmentComplete = true;
        _hasShipment = false;
        NeedsShipment = false;
        _rowVersion = [];
        _discountMethod = null;
        _decPoint = false;
        _taxable = null;
        ResetSoPicker();
    }

    private void ClearAddresses()
    {
        InvName = InvAddress1 = InvAddress2 = InvAddress3 = null;
        InvCity = InvState = InvPostalCode = InvCountry = InvTel = InvFax = null;
        ShipName = ShipAddress1 = ShipAddress2 = ShipAddress3 = null;
        ShipCity = ShipState = ShipPostalCode = ShipCountry = ShipTel = ShipFax = null;
    }

    private void ClearShipToState()
    {
        _shipToLine = null;
        _shipToOptions = [];
    }

    private void WipeAllCustomerDependentFields()
    {
        CustName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        TaxGrCode = null;
        SalesRep = null;
        Ref1 = null;
        ProjId = null;
        PayCode = null;
        Remarks = null;
        ClearAddresses();
        ClearShipToState();
        _taxable = null;
        _discountMethod = null;
        _decPoint = false;
        ResetSoPicker();
    }

    private static string NormCustCode(string? custCode) => (custCode ?? string.Empty).Trim();

    private void ApplyDocument(SaDoDocument doc)
    {
        DoNo = doc.DoNo;
        DoNoDisplay = doc.DoNo;
        DoDate = doc.DoDate;
        StatusDisplay = doc.Status;
        CustCode = doc.CustCode;
        CustName = doc.CustName;
        Prefix = doc.Prefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = doc.CurrRate > 0m;
        TaxGrCode = doc.TaxGrCode;
        SalesRep = doc.SalesRep;
        Ref1 = doc.Ref1;
        ProjId = doc.ProjId;
        PayCode = doc.PayCode;
        Remarks = doc.Remarks;
        ShipVia = doc.ShipVia;
        InvName = doc.InvName;
        InvAddress1 = doc.InvAddress1;
        InvAddress2 = doc.InvAddress2;
        InvAddress3 = doc.InvAddress3;
        InvCity = doc.InvCity;
        InvState = doc.InvState;
        InvPostalCode = doc.InvPostalCode;
        InvCountry = doc.InvCountry;
        InvTel = doc.InvTel;
        InvFax = doc.InvFax;
        ShipName = doc.ShipName;
        ShipAddress1 = doc.ShipAddress1;
        ShipAddress2 = doc.ShipAddress2;
        ShipAddress3 = doc.ShipAddress3;
        ShipCity = doc.ShipCity;
        ShipState = doc.ShipState;
        ShipPostalCode = doc.ShipPostalCode;
        ShipCountry = doc.ShipCountry;
        ShipTel = doc.ShipTel;
        ShipFax = doc.ShipFax;
        GrossAmnt = doc.GrossAmnt;
        Taxes = doc.Taxes;
        TotAmnt = doc.TotAmnt;
        ShipmentComplete = doc.ShipmentComplete;
        NeedsShipment = doc.NeedsShipment;
        _hasShipment = doc.SpBatchNo is not null || doc.Shipment.Count > 0;
        _rowVersion = doc.RowVersion ?? [];
        Lines = doc.Lines.Select(SaDoLineVm.FromDto).ToList();
        foreach (var line in Lines)
        {
            RefreshPackFromItem(line);
        }
    }

    private async Task ApplyCustomerDefaultsAsync(string? custCode, bool addressApply, int seq)
    {
        var code = NormCustCode(custCode);
        if (string.IsNullOrEmpty(code))
        {
            if (addressApply)
            {
                WipeAllCustomerDependentFields();
            }
            else
            {
                ClearShipToState();
            }

            return;
        }

        var result = await Dos.GetCustomerDefaultsAsync(code, DoDate, _cts.Token);
        // Stale check BEFORE any stamp
        if (seq != _customerApplySeq || _disposed)
        {
            return;
        }

        if (!result.Succeeded || result.CustomerDefaults is null)
        {
            if (addressApply)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load customer defaults.";
            }

            return;
        }

        var d = result.CustomerDefaults;
        _taxable = d.Taxable;
        _discountMethod = d.DiscountMethod ?? _discountMethod;
        _decPoint = d.DecPoint == true;
        _shipToOptions = d.ShipToAddresses ?? [];

        if (!addressApply)
        {
            _shipToLine = null;
            return;
        }

        if (seq != _customerApplySeq)
        {
            return;
        }

        ApplyDefaults(d);
        _shipToLine = null;
        Remarks = null;
    }

    protected async Task OnCustCodeChanged(string? value)
    {
        if (_isApplyingDefaults || _disposed || !CanEditDocument)
        {
            return;
        }

        var next = NormCustCode(value);
        next = string.IsNullOrEmpty(next) ? null : next;
        if (string.Equals(CustCode, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Confirm only when changing to another customer while lines exist.
        if (Lines.Count > 0 && next is not null)
        {
            _pendingCustCode = next;
            ConfirmCustChangeVisible = true;
            return;
        }

        // Clearing customer always wipes lines — no confirm (locked).
        if (Lines.Count > 0 && next is null)
        {
            Lines.Clear();
        }

        ResetSoPicker();
        await ApplyCustomerAsync(next);
        RecalcDocument();
    }

    protected async Task ConfirmCustChangeAsync()
    {
        ConfirmCustChangeVisible = false;
        var next = _pendingCustCode;
        _pendingCustCode = null;
        Lines.Clear();
        await ApplyCustomerAsync(next);
        RecalcDocument();
        MarkDirty();
    }

    protected void CancelCustChange()
    {
        ConfirmCustChangeVisible = false;
        _pendingCustCode = null;
    }

    private async Task ApplyCustomerAsync(string? custCode)
    {
        var next = string.IsNullOrEmpty(NormCustCode(custCode)) ? null : NormCustCode(custCode);
        var seq = Interlocked.Increment(ref _customerApplySeq);
        CustCode = next;
        _isApplyingDefaults = true;
        try
        {
            if (next is null)
            {
                WipeAllCustomerDependentFields();
                MarkDirty();
                return;
            }

            await ApplyCustomerDefaultsAsync(next, addressApply: true, seq: seq);
            MarkDirty();
        }
        finally
        {
            _isApplyingDefaults = false;
        }
    }

    private void ApplyDefaults(SaDoCustomerDefaults d)
    {
        CustCode = d.CustCode;
        CustName = d.CustName;
        Prefix = null; // DO prefix comes from numbering series, not customer
        Currency = d.Currency ?? "MYR";
        CurrRate = d.CurrRate;
        CurrRateValid = d.CurrRateValid;
        TaxGrCode = d.TaxGrCode;
        SalesRep = d.SalesRep;
        PayCode = d.PayCode;
        _taxable = d.Taxable;
        _discountMethod = d.DiscountMethod;
        _decPoint = d.DecPoint == true;

        InvName = d.InvName;
        InvAddress1 = d.InvAddress1;
        InvAddress2 = d.InvAddress2;
        InvAddress3 = d.InvAddress3;
        InvCity = d.InvCity;
        InvState = d.InvState;
        InvPostalCode = d.InvPostalCode;
        InvCountry = d.InvCountry;
        InvTel = d.InvTel;
        InvFax = d.InvFax;
        ShipName = d.ShipName;
        ShipAddress1 = d.ShipAddress1;
        ShipAddress2 = d.ShipAddress2;
        ShipAddress3 = d.ShipAddress3;
        ShipCity = d.ShipCity;
        ShipState = d.ShipState;
        ShipPostalCode = d.ShipPostalCode;
        ShipCountry = d.ShipCountry;
        ShipTel = d.ShipTel;
        ShipFax = d.ShipFax;
        _shipToOptions = d.ShipToAddresses ?? [];
    }

    protected void OnShipToLineChanged(int? line)
    {
        if (!CanEditAddresses)
        {
            return;
        }

        _shipToLine = line;
        if (line is null)
        {
            return;
        }

        var row = _shipToOptions.FirstOrDefault(x => x.Line == line);
        if (row is null)
        {
            _shipToLine = null;
            return;
        }

        StampShipFrom(row);
        MarkDirty();
    }

    private void StampShipFrom(SaCustAddressVm row)
    {
        ShipName = !string.IsNullOrWhiteSpace(row.AddName) ? row.AddName : row.DeliverTo;
        ShipAddress1 = row.Address1;
        ShipAddress2 = row.Address2;
        ShipAddress3 = row.Address3;
        ShipCity = row.City;
        ShipState = row.State;
        ShipPostalCode = row.PostalCode;
        ShipCountry = row.Country;
        ShipTel = row.Tel;
        ShipFax = row.Fax;
    }

    protected void OnShipFieldEdited()
    {
        _shipToLine = null;
        MarkDirty();
    }

    protected async Task OnDoDateChanged(DateTime newDate)
    {
        if (!CanEditDocument)
        {
            return;
        }

        var previous = DoDate;
        DoDate = newDate.Date;
        if (previous.Date != DoDate.Date && _hasShipment)
        {
            DateShipmentWarning = true;
        }

        await RefreshFxAsync();
        MarkDirty();
        RecalcDocument();
    }

    private async Task RefreshFxAsync()
    {
        if (string.IsNullOrWhiteSpace(Currency))
        {
            CurrRateValid = false;
            return;
        }

        var result = await Dos.ResolveCurrencyRateAsync(Currency, DoDate, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded)
        {
            CurrRateValid = false;
            CurrRate = 0m;
            return;
        }

        CurrRate = result.CurrRate;
        CurrRateValid = result.CurrRateValid;
    }

    protected void OnHeaderFieldChanged()
    {
        MarkDirty();
        RecalcDocument();
    }

    protected Task OnTaxGrCodeChanged(string? value)
    {
        TaxGrCode = value;
        OnHeaderFieldChanged();
        return Task.CompletedTask;
    }

    protected Task OnPayCodeChanged(string? value)
    {
        PayCode = value;
        OnHeaderFieldChanged();
        return Task.CompletedTask;
    }

    protected void OnNewLineClick()
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = null;
        Popup = new SaDoLineVm
        {
            FrWarehouse = Warehouses.FirstOrDefault()?.WarehouseCode,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? false
        };
        PopupDiscountIsAmount = false;
        PopupError = null;
        PopupVisible = true;
    }

    protected async Task OpenSoPickerAsync()
    {
        if (!CanOpenSoPicker)
        {
            return;
        }

        SoPickerVisible = true;
        await LoadSoPickerOptionsAsync();
    }

    protected async Task OnSelectedSourceSoChangedAsync(string? soNo)
    {
        SelectedSourceSoNo = soNo;
        SelectedSoPickerLines = [];
        SoPickerLines = [];
        SoPickerError = null;

        if (string.IsNullOrWhiteSpace(SelectedSourceSoNo))
        {
            return;
        }

        var requestedSoNo = SelectedSourceSoNo;
        SoPickerLoading = true;
        try
        {
            var result = await Sos.GetRemainingLinesAsync(
                requestedSoNo,
                excludeDoNo: IsEditMode ? DoNo : null,
                _cts.Token);
            if (_disposed)
            {
                return;
            }

            // Stale async: ignore if user moved to another SO (or cleared) while loading.
            if (!string.Equals(SelectedSourceSoNo, requestedSoNo, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!result.Succeeded)
            {
                SoPickerError = result.ErrorMessage ?? "Unable to load remaining SO lines.";
                return;
            }

            var existingKeys = Lines
                .Where(x => !string.IsNullOrWhiteSpace(x.SoNo) && x.SoLine is not null)
                .Select(x => (SoNo: x.SoNo!, SoLine: (int)x.SoLine!.Value));
            // Grid holds exactly one selected SO; KeyFieldName=Line is unique within this collection.
            SoPickerLines = SaDocPickerLines.FilterRemainingSoLines(
                result.RemainingLines,
                requestedSoNo,
                existingKeys).ToList();
            SelectedSoPickerLines = SaDocPickerLines.SelectAllCurrent(SoPickerLines);
            if (SoPickerLines.Count == 0)
            {
                SoPickerError = "Selected sales order has no remaining lines (qty may be reserved on other delivery orders or draft invoices).";
            }
        }
        finally
        {
            SoPickerLoading = false;
        }
    }

    protected void OnSelectedSoPickerLinesChanged(IReadOnlyList<object> selected)
    {
        SelectedSoPickerLines = selected.OfType<SaSoLineDto>().ToList();
    }

    protected void CloseSoPicker()
    {
        SoPickerVisible = false;
    }

    protected void AddFromSo()
    {
        if (string.IsNullOrWhiteSpace(SelectedSourceSoNo))
        {
            SoPickerError = "Select a sales order first.";
            return;
        }

        if (SelectedSoPickerLines.Count == 0)
        {
            SoPickerError = "Select at least one sales order line.";
            return;
        }

        var added = 0;
        foreach (var source in SelectedSoPickerLines)
        {
            // Second guard: skip lines already on the document (SoNo + SoLine).
            var keyExists = Lines.Any(x =>
                string.Equals(x.SoNo, SelectedSourceSoNo, StringComparison.OrdinalIgnoreCase)
                && x.SoLine == source.Line);
            if (keyExists)
            {
                continue;
            }

            Lines.Add(SaDoLineVm.FromSalesOrder(source, SelectedSourceSoNo));
            added++;
        }

        if (added == 0)
        {
            SoPickerError = "All remaining lines from this sales order are already added.";
            return;
        }

        Renumber();
        Lines = Lines.ToList();
        RecalcDocument();
        MarkDirty();
        StatusMessage = added == 1 ? "1 sales order line added." : $"{added} sales order lines added.";
        ResetSoPicker();
    }

    protected void EditLine(SaDoLineVm line)
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = line;
        Popup = line.Clone();
        RefreshPackFromItem(Popup);
        PopupDiscountIsAmount = Popup.ItemDiscAmount != 0m || Popup.ItemDiscAmount1 != 0m;
        PopupError = null;
        PopupVisible = true;
    }

    protected void RemoveLine(SaDoLineVm line)
    {
        if (!CanMutateLines)
        {
            return;
        }

        Lines.Remove(line);
        Renumber();
        RecalcDocument();
        MarkDirty();
    }

    protected void OnPopupItemChanged(string? iCode)
    {
        Popup.ICode = iCode ?? string.Empty;
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, Popup.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        Popup.IDesc = item.IDesc;
        Popup.StdUom = item.StdUom;
        Popup.StdPackSize = item.StdPackSize;
        Popup.StockControl = item.StockControl;
        Popup.UnitPrice = item.SellingPrice ?? 0m;
        if (!string.IsNullOrWhiteSpace(item.TaxGroup)
            && TaxGroups.Any(x => string.Equals(x.TaxGrCode, item.TaxGroup, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.TaxGrCode = item.TaxGroup;
        }

        if (string.IsNullOrWhiteSpace(Popup.FrWarehouse))
        {
            Popup.FrWarehouse = item.DefWarehouse ?? Warehouses.FirstOrDefault()?.WarehouseCode;
        }
    }

    protected void OnPopupDiscountModeChanged(bool amountMode)
    {
        PopupDiscountIsAmount = amountMode;
        if (amountMode)
        {
            Popup.ItemDiscount = Popup.ItemDiscount2 = Popup.ItemDiscount3 = 0m;
            Popup.ItemDiscount4 = Popup.ItemDiscount5 = Popup.ItemDiscount6 = 0m;
        }
        else
        {
            Popup.ItemDiscAmount = 0m;
            Popup.ItemDiscAmount1 = 0m;
        }
    }

    protected void OnPopupSave()
    {
        if (string.IsNullOrWhiteSpace(Popup.ICode))
        {
            PopupError = "Item is required.";
            return;
        }

        if (Popup.Qty <= 0m)
        {
            PopupError = "Quantity must be greater than zero.";
            return;
        }

        if (Lines.Count > 0)
        {
            var expected = _editingLine?.IsInclusive ?? Lines[0].IsInclusive;
            if (Popup.IsInclusive != expected)
            {
                PopupError = "ST000032: All lines must use the same tax type (inclusive or exclusive).";
                return;
            }
        }

        if (_editingLine is null)
        {
            Lines.Add(Popup.Clone());
        }
        else
        {
            var idx = Lines.IndexOf(_editingLine);
            if (idx >= 0)
            {
                Lines[idx] = Popup.Clone();
            }
        }

        Renumber();
        RecalcDocument();
        MarkDirty();
        PopupVisible = false;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        try
        {
            var request = ToRequest();
            var result = IsNewMode
                ? await Dos.SaveNewAsync(request, _cts.Token)
                : await Dos.UpdateAsync(DoNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result, stayOnPage: true))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo("/sales/delivery-orders");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnAddShipmentAsync()
    {
        if (IsNewMode || string.IsNullOrWhiteSpace(DoNo))
        {
            ErrorMessage = "Save the delivery order before adding shipment.";
            return;
        }

        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        try
        {
            if (_isDirty)
            {
                var saved = await Dos.UpdateAsync(DoNo, ToRequest(), _cts.Token);
                if (_disposed)
                {
                    return;
                }

                if (!HandleOperationResult(saved, stayOnPage: true) || saved.Document is null)
                {
                    return;
                }

                ApplyDocument(saved.Document);
                _isDirty = false;
            }

            var ship = await Dos.AddShipmentAsync(DoNo, overwriteExisting: false, _rowVersion, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (ship.RequiresConfirmation)
            {
                _shipConfirmMessage = ship.ErrorMessage ?? "Shipment already exists. Confirm to overwrite.";
                _shipConfirmToken = ship.Document?.RowVersion ?? _rowVersion;
                ConfirmShipOverwriteVisible = true;
                return;
            }

            if (!HandleOperationResult(ship, stayOnPage: true) || ship.Document is null)
            {
                return;
            }

            ApplyDocument(ship.Document);
            DateShipmentWarning = false;
            if (ship.Document.ShipmentComplete)
            {
                StatusMessage = "Shipment allocated.";
            }
            else
            {
                ErrorMessage = ship.Document.Shipment.Count == 0
                    ? "ST000051: No eligible stock found. Check warehouse, tenant location, ACTIVE status, and lot date versus DO date."
                    : "Shipment allocated with incomplete lines. Post will be blocked until complete.";
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmShipOverwriteAsync()
    {
        ConfirmShipOverwriteVisible = false;
        if (string.IsNullOrWhiteSpace(DoNo) || IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var token = _shipConfirmToken ?? _rowVersion;
            var ship = await Dos.AddShipmentAsync(DoNo, overwriteExisting: true, token, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(ship, stayOnPage: true) || ship.Document is null)
            {
                return;
            }

            ApplyDocument(ship.Document);
            DateShipmentWarning = false;
            StatusMessage = "Shipment rebuilt.";
        }
        finally
        {
            IsSubmitting = false;
            _shipConfirmToken = null;
        }
    }

    protected void CancelShipOverwrite()
    {
        ConfirmShipOverwriteVisible = false;
        _shipConfirmToken = null;
    }

    protected void OpenShipmentEditor(int line)
    {
        if (string.IsNullOrWhiteSpace(DoNo) || !_hasShipment)
        {
            return;
        }

        _shipEditLine = line;
        ShipEditorVisible = true;
    }

    protected void CloseShipmentEditor()
    {
        ShipEditorVisible = false;
        _shipEditLine = null;
    }

    protected Task OnShipmentAppliedAsync(SaDoDocument document)
    {
        ApplyDocument(document);
        ShipEditorVisible = false;
        _shipEditLine = null;
        StatusMessage = "Shipment line updated.";
        return Task.CompletedTask;
    }

    protected void OnShipmentApplyFailed((string Message, SaDoDocument? Document) args)
    {
        ErrorMessage = args.Message;
    }

    protected Task OnCancelAsync()
    {
        if (_isDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/sales/delivery-orders");
        return Task.CompletedTask;
    }

    protected void OnClose() => Navigation.NavigateTo("/sales/delivery-orders");

    protected void OnEditFromView() => Navigation.NavigateTo($"/sales/delivery-orders/edit/{DoNo}");

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/sales/delivery-orders");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || string.IsNullOrWhiteSpace(DoNo))
        {
            return;
        }

        var result = await Dos.GetAsync(DoNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload delivery order.";
            return;
        }

        ApplyDocument(result.Document);
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        _isDirty = false;
        ValidationErrors.Clear();
        StatusMessage = "Loaded latest version.";
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private bool HandleOperationResult(SaDoOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        switch (result.ErrorKind)
        {
            case SaDoErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                ErrorMessage = result.ErrorMessage ?? "Validation failed.";
                break;
            case SaDoErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This delivery order was changed by another user.";
                break;
            case SaDoErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Delivery order was not found.";
                if (!stayOnPage)
                {
                    Navigation.NavigateTo("/sales/delivery-orders");
                }

                break;
            case SaDoErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private SaDoSaveRequest ToRequest() =>
        new()
        {
            DoDate = DoDate,
            CustCode = CustCode ?? string.Empty,
            Currency = Currency,
            PayCode = PayCode,
            TaxGrCode = TaxGrCode,
            SalesRep = SalesRep,
            Ref1 = Ref1,
            ProjId = ProjId,
            Remarks = Remarks,
            ShipVia = ShipVia,
            ShipName = ShipName,
            ShipAddress1 = ShipAddress1,
            ShipAddress2 = ShipAddress2,
            ShipAddress3 = ShipAddress3,
            ShipCity = ShipCity,
            ShipState = ShipState,
            ShipPostalCode = ShipPostalCode,
            ShipCountry = ShipCountry,
            ShipTel = ShipTel,
            ShipFax = ShipFax,
            InvName = InvName,
            InvAddress1 = InvAddress1,
            InvAddress2 = InvAddress2,
            InvAddress3 = InvAddress3,
            InvCity = InvCity,
            InvState = InvState,
            InvPostalCode = InvPostalCode,
            InvCountry = InvCountry,
            InvTel = InvTel,
            InvFax = InvFax,
            RowVersion = IsNewMode ? null : _rowVersion,
            Lines = Lines.Select(x => x.ToRequest()).ToList()
        };

    private void RecalcDocument()
    {
        if (Lines.Count == 0)
        {
            GrossAmnt = Taxes = TotAmnt = 0m;
            return;
        }

        var states = new List<SaInvoiceLineCalcState>();
        foreach (var line in Lines)
        {
            var state = line.ToCalcState();
            SaInvoiceCalc.CalculateLine(state, ResolveTaxPercent(line.TaxGrCode), _decPoint, _discountMethod);
            states.Add(state);
        }

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(states);
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].Amount = states[i].Amount;
            Lines[i].TaxAmt = states[i].TaxAmt;
            Lines[i].NetAmount = states[i].NetAmount;
        }

        var header = SaInvoiceCalc.CalculateHeader(states, _decPoint);
        GrossAmnt = header.GrossAmnt;
        Taxes = header.Taxes;
        TotAmnt = header.TotAmnt;
    }

    private SaInvoiceLineCalcState BuildPopupCalc()
    {
        var state = Popup.ToCalcState();
        SaInvoiceCalc.CalculateLine(state, ResolveTaxPercent(Popup.TaxGrCode), _decPoint, _discountMethod);
        return state;
    }

    private decimal ResolveTaxPercent(string? lineTax)
    {
        var code = string.IsNullOrWhiteSpace(lineTax) ? TaxGrCode : lineTax;
        if (string.IsNullOrWhiteSpace(code))
        {
            return 0m;
        }

        var match = TaxGroups.FirstOrDefault(x =>
            string.Equals(x.TaxGrCode, code, StringComparison.OrdinalIgnoreCase));
        return match?.Percentage ?? 0m;
    }

    private void RefreshPackFromItem(SaDoLineVm line)
    {
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, line.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            line.StdPackSize = item.StdPackSize;
            line.StockControl = item.StockControl;
            line.StdUom = item.StdUom ?? line.StdUom;
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].Line = i + 1;
        }
    }

    private void MarkDirty()
    {
        if (CanEditDocument)
        {
            _isDirty = true;
        }
    }

    protected void MarkDirtyOnly() => MarkDirty();

    private async Task LoadSoPickerOptionsAsync()
    {
        SoPickerLoading = true;
        SoPickerError = null;
        SelectedSourceSoNo = null;
        SoPickerLines = [];
        SelectedSoPickerLines = [];
        try
        {
            var result = await Sos.SearchAsync(new SaSoListQuery
            {
                SearchText = CustCode,
                Skip = 0,
                Take = 50,
                SortDescending = true
            }, _cts.Token);

            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded || result.ListPage is null)
            {
                SoPickerOptions = [];
                SoPickerError = result.ErrorMessage ?? "Unable to search sales orders.";
                return;
            }

            SoPickerOptions = result.ListPage.Rows
                .Where(x =>
                    string.Equals(x.CustCode, CustCode, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(x.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new SaSoPickerOption(x.SoNo, x.SoDate, x.Status, x.CustPo))
                .ToList();

            if (SoPickerOptions.Count == 0)
            {
                SoPickerError = "No open sales orders were found for this customer.";
            }
        }
        finally
        {
            SoPickerLoading = false;
        }
    }

    private void ResetSoPicker()
    {
        SoPickerVisible = false;
        SoPickerLoading = false;
        SoPickerError = null;
        SelectedSourceSoNo = null;
        SoPickerLines = [];
        SelectedSoPickerLines = [];
        SoPickerOptions = [];
    }
}

public sealed class SaDoLineVm
{
    public int Line { get; set; }
    public string? SoNo { get; set; }
    public short? SoLine { get; set; }
    public short? CustRel { get; set; }
    public string? CustPo { get; set; }
    public bool HasSource => !string.IsNullOrWhiteSpace(SoNo);
    public string? SoRevDisplay => HasSource ? (CustRel is > 0 ? CustRel.Value.ToString() : "1") : null;
    public string? SoLineDisplay => SoLine is > 0 ? SoLine.Value.ToString() : null;
    public bool LinkDo { get; set; }
    public decimal SoConsumedQty { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public decimal Qty { get; set; } = 1m;
    public decimal? StdPackSize { get; set; }
    public string? StdUom { get; set; }
    public string? FrWarehouse { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount2 { get; set; }
    public decimal ItemDiscount3 { get; set; }
    public decimal ItemDiscount4 { get; set; }
    public decimal ItemDiscount5 { get; set; }
    public decimal ItemDiscount6 { get; set; }
    public decimal ItemDiscAmount { get; set; }
    public decimal ItemDiscAmount1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGrCode { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal NetAmount { get; set; }
    public bool StockControl { get; set; } = true;
    public bool ShipmentComplete { get; set; }
    public string? Remarks { get; set; }

    public SaDoLineVm Clone() => new()
    {
        Line = Line,
        SoNo = SoNo,
        SoLine = SoLine,
        CustRel = CustRel,
        CustPo = CustPo,
        LinkDo = LinkDo,
        SoConsumedQty = SoConsumedQty,
        ICode = ICode,
        IDesc = IDesc,
        Qty = Qty,
        StdPackSize = StdPackSize,
        StdUom = StdUom,
        FrWarehouse = FrWarehouse,
        UnitPrice = UnitPrice,
        ItemDiscount = ItemDiscount,
        ItemDiscount2 = ItemDiscount2,
        ItemDiscount3 = ItemDiscount3,
        ItemDiscount4 = ItemDiscount4,
        ItemDiscount5 = ItemDiscount5,
        ItemDiscount6 = ItemDiscount6,
        ItemDiscAmount = ItemDiscAmount,
        ItemDiscAmount1 = ItemDiscAmount1,
        IsInclusive = IsInclusive,
        TaxGrCode = TaxGrCode,
        Amount = Amount,
        TaxAmt = TaxAmt,
        NetAmount = NetAmount,
        StockControl = StockControl,
        ShipmentComplete = ShipmentComplete,
        Remarks = Remarks
    };

    public SaDoLineRequest ToRequest() =>
        new()
        {
            Line = Line,
            SoNo = SoNo,
            SoLine = SoLine,
            CustRel = CustRel,
            LinkDo = LinkDo,
            ICode = ICode,
            IDesc = IDesc,
            Qty = Qty,
            FrWarehouse = FrWarehouse,
            UnitPrice = UnitPrice,
            ItemDiscount = ItemDiscount,
            ItemDiscount2 = ItemDiscount2,
            ItemDiscount3 = ItemDiscount3,
            ItemDiscount4 = ItemDiscount4,
            ItemDiscount5 = ItemDiscount5,
            ItemDiscount6 = ItemDiscount6,
            ItemDiscAmount = ItemDiscAmount,
            ItemDiscAmount1 = ItemDiscAmount1,
            IsInclusive = IsInclusive,
            TaxGrCode = TaxGrCode,
            Remarks = Remarks
        };

    public SaInvoiceLineCalcState ToCalcState() =>
        new()
        {
            Line = Line,
            Qty = Qty,
            UnitPrice = UnitPrice,
            ItemDiscount = ItemDiscount,
            ItemDiscount2 = ItemDiscount2,
            ItemDiscount3 = ItemDiscount3,
            ItemDiscount4 = ItemDiscount4,
            ItemDiscount5 = ItemDiscount5,
            ItemDiscount6 = ItemDiscount6,
            ItemDiscAmount = ItemDiscAmount,
            ItemDiscAmount1 = ItemDiscAmount1,
            IsInclusive = IsInclusive
        };

    public static SaDoLineVm FromDto(SaDoLineDto dto) =>
        new()
        {
            Line = dto.Line,
            SoNo = dto.SoNo,
            SoLine = dto.SoLine,
            CustRel = dto.CustRel,
            CustPo = dto.CustPo,
            LinkDo = false,
            SoConsumedQty = dto.SoConsumedQty,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.Qty,
            StdPackSize = dto.StdPsize > 0m ? dto.StdPsize : (decimal?)null,
            StdUom = dto.StdUom,
            FrWarehouse = dto.FrWarehouse,
            UnitPrice = dto.UnitPrice,
            ItemDiscount = dto.ItemDiscount,
            ItemDiscount2 = dto.ItemDiscount2,
            ItemDiscount3 = dto.ItemDiscount3,
            ItemDiscount4 = dto.ItemDiscount4,
            ItemDiscount5 = dto.ItemDiscount5,
            ItemDiscount6 = dto.ItemDiscount6,
            ItemDiscAmount = dto.ItemDiscAmount,
            ItemDiscAmount1 = dto.ItemDiscAmount1,
            IsInclusive = dto.IsInclusive,
            TaxGrCode = dto.TaxGroup,   // SaDoLineDto uses TaxGroup; VM uses TaxGrCode
            Amount = dto.Amount,
            TaxAmt = dto.TaxAmt,
            NetAmount = dto.NetAmount,
            StockControl = dto.StockControl,
            ShipmentComplete = dto.ShipmentComplete,
            Remarks = dto.Remarks
        };

    public static SaDoLineVm FromSalesOrder(SaSoLineDto dto, string soNo) =>
        new()
        {
            SoNo = soNo,
            SoLine = checked((short)dto.Line),
            CustRel = dto.CustRel,
            CustPo = dto.CustPo,
            LinkDo = false,
            SoConsumedQty = 0m,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.BalanceQty,
            StdPackSize = dto.StdPsize > 0m ? dto.StdPsize : (decimal?)null,
            StdUom = dto.StdUom,
            FrWarehouse = dto.Warehouse,
            UnitPrice = dto.UnitPrice,
            ItemDiscount = dto.ItemDiscount,
            ItemDiscount2 = dto.ItemDiscount2,
            ItemDiscount3 = dto.ItemDiscount3,
            ItemDiscount4 = dto.ItemDiscount4,
            ItemDiscount5 = dto.ItemDiscount5,
            ItemDiscount6 = dto.ItemDiscount6,
            ItemDiscAmount = dto.ItemDiscAmount,
            ItemDiscAmount1 = dto.ItemDiscAmount1,
            IsInclusive = dto.IsInclusive,
            TaxGrCode = dto.TaxGroup,
            Amount = dto.Amount,
            TaxAmt = dto.TaxAmt,
            NetAmount = dto.NetAmount,
            StockControl = dto.StockControl,
            ShipmentComplete = !dto.StockControl,
            Remarks = dto.Remarks
        };
}

public sealed record SaSoPickerOption(string SoNo, DateTime SoDate, string Status, string? CustPo)
{
    public string DisplayText =>
        string.IsNullOrWhiteSpace(CustPo)
            ? $"{SoNo} · {SoDate:dd/MM/yyyy} · {Status}"
            : $"{SoNo} · {SoDate:dd/MM/yyyy} · {Status} · PO {CustPo}";
}
