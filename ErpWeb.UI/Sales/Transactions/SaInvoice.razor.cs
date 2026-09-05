using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaInvoice : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? InvNo { get; set; }

    [Inject] private ISaInvoiceService Invoices { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCustChangeVisible;
    protected bool ConfirmShipOverwriteVisible;
    protected bool ShipEditorVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected bool CanEditPermission;
    protected string InvNoDisplay = "AUTO";
    protected string DoNoDisplay = "AUTO";
    protected string StatusDisplay = SaInvoiceStatuses.New;
    protected DateTime InvDate;
    protected string? CustCode;
    protected string? CustName;
    protected string? InvPrefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? PayCode;
    protected string? TaxGrCode;
    protected string? SalesmanCode;
    protected string? PoNo;
    protected string? Remark;
    protected string? InvName;
    protected string? InvAddress1;
    protected string? InvAddress2;
    protected string? InvAddress3;
    protected string? InvAddress4;
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
    protected int ActiveTabIndex;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private SaInvoiceLineVm? _editingLine;
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
    private int _defaultsSeq;
    private CancellationTokenSource _cts = new();
    private string? _shipConfirmMessage;
    private byte[]? _shipConfirmToken;
    private int? _shipEditLine;

    protected List<SaInvoiceLineVm> Lines { get; set; } = [];
    protected List<SaInvoiceCustomerLookupRow> Customers { get; set; } = [];
    protected List<SaInvoiceItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<SaInvoiceTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected SaInvoiceLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && string.Equals(StatusDisplay, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(InvNo);
    protected string PageHeading => IsNewMode ? "New invoice" : IsEditMode ? "Edit invoice" : "View invoice";
    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected bool HasCustomer => !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanMutateLines => CanEditDocument && HasCustomer && CurrRateValid && !IsSubmitting;
    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasCustomer
        && CurrRateValid
        && !string.IsNullOrWhiteSpace(PayCode);
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
        var key = $"{Mode}:{InvNo}";
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
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Edit);
        var lookups = await Invoices.GetLookupsAsync(_cts.Token);
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
        }

        Countries = await Lookups.ListCountriesForAssignmentAsync(_cts.Token);
        States = await Lookups.ListStatesForAssignmentAsync(_cts.Token);
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

        var result = await Invoices.GetAsync(InvNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Invoice was not found.";
            if (result.ErrorKind == SaInvoiceErrorKind.NotFound)
            {
                Navigation.NavigateTo("/sales/invoices");
            }

            IsLoading = false;
            return;
        }

        ApplyDocument(result.Document);
        await CaptureCustomerFlagsAsync(result.Document.CustCode);
        RecalcDocument();
        IsLoading = false;
    }

    private void ResetNewDocument()
    {
        InvNo = null;
        InvNoDisplay = "AUTO";
        DoNoDisplay = "AUTO";
        StatusDisplay = SaInvoiceStatuses.New;
        InvDate = Dates.Today.Date;
        CustCode = null;
        CustName = null;
        InvPrefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        PayCode = null;
        TaxGrCode = null;
        SalesmanCode = null;
        PoNo = null;
        Remark = null;
        ClearAddresses();
        Lines = [];
        GrossAmnt = 0;
        Taxes = 0;
        TotAmnt = 0;
        ShipmentComplete = true;
        _hasShipment = false;
        _rowVersion = [];
        _discountMethod = null;
        _decPoint = false;
        _taxable = null;
    }

    private void ClearAddresses()
    {
        InvName = InvAddress1 = InvAddress2 = InvAddress3 = InvAddress4 = null;
        InvCity = InvState = InvPostalCode = InvCountry = InvTel = InvFax = null;
        ShipName = ShipAddress1 = ShipAddress2 = ShipAddress3 = null;
        ShipCity = ShipState = ShipPostalCode = ShipCountry = ShipTel = ShipFax = null;
    }

    private void ApplyDocument(SaInvoiceDocument doc)
    {
        InvNo = doc.InvNo;
        InvNoDisplay = doc.InvNo;
        DoNoDisplay = string.IsNullOrWhiteSpace(doc.DoNo) ? doc.InvNo : doc.DoNo;
        InvDate = doc.InvDate;
        StatusDisplay = doc.Status;
        CustCode = doc.CustCode;
        CustName = doc.CustName;
        InvPrefix = doc.InvPrefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = doc.CurrRate > 0m;
        PayCode = doc.PayCode;
        TaxGrCode = doc.TaxGrCode;
        SalesmanCode = doc.SalesmanCode;
        PoNo = doc.PoNo;
        Remark = doc.Remark;
        InvName = doc.InvName;
        InvAddress1 = doc.InvAddress1;
        InvAddress2 = doc.InvAddress2;
        InvAddress3 = doc.InvAddress3;
        InvAddress4 = doc.InvAddress4;
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
        _hasShipment = doc.SpBatchNo is not null || doc.Shipment.Count > 0;
        _rowVersion = doc.RowVersion ?? [];
        Lines = doc.Lines.Select(SaInvoiceLineVm.FromDto).ToList();
        foreach (var line in Lines)
        {
            RefreshPackFromItem(line);
        }
    }

    private async Task CaptureCustomerFlagsAsync(string? custCode)
    {
        if (string.IsNullOrWhiteSpace(custCode))
        {
            return;
        }

        var lookup = Customers.FirstOrDefault(x => string.Equals(x.CustCode, custCode, StringComparison.OrdinalIgnoreCase));
        _discountMethod = lookup?.DiscountMethod;
        _decPoint = lookup?.DecPoint == true;

        var defaults = await Invoices.GetCustomerDefaultsAsync(custCode, InvDate, _cts.Token);
        if (_disposed || !defaults.Succeeded || defaults.CustomerDefaults is null)
        {
            return;
        }

        _taxable = defaults.CustomerDefaults.Taxable;
        _discountMethod = defaults.CustomerDefaults.DiscountMethod ?? _discountMethod;
        _decPoint = defaults.CustomerDefaults.DecPoint == true;
    }

    protected async Task OnCustCodeChanged(string? value)
    {
        if (_isApplyingDefaults || _disposed || !CanEditDocument)
        {
            return;
        }

        var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (string.Equals(CustCode, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Lines.Count > 0)
        {
            _pendingCustCode = next;
            ConfirmCustChangeVisible = true;
            return;
        }

        await ApplyCustomerAsync(next);
    }

    protected async Task ConfirmCustChangeAsync()
    {
        ConfirmCustChangeVisible = false;
        var next = _pendingCustCode;
        _pendingCustCode = null;
        Lines = [];
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
        _isApplyingDefaults = true;
        try
        {
            CustCode = custCode;
            if (string.IsNullOrWhiteSpace(custCode))
            {
                CustName = null;
                InvPrefix = null;
                Currency = "MYR";
                CurrRate = 1m;
                CurrRateValid = false;
                PayCode = null;
                TaxGrCode = null;
                SalesmanCode = null;
                ClearAddresses();
                _taxable = null;
                _discountMethod = null;
                _decPoint = false;
                MarkDirty();
                return;
            }

            var seq = Interlocked.Increment(ref _defaultsSeq);
            var result = await Invoices.GetCustomerDefaultsAsync(custCode, InvDate, _cts.Token);
            if (_disposed || seq != _defaultsSeq)
            {
                return;
            }

            if (!result.Succeeded || result.CustomerDefaults is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load customer defaults.";
                return;
            }

            ApplyDefaults(result.CustomerDefaults);
            MarkDirty();
        }
        finally
        {
            _isApplyingDefaults = false;
        }
    }

    private void ApplyDefaults(SaInvoiceCustomerDefaults d)
    {
        CustCode = d.CustCode;
        CustName = d.CustName;
        InvPrefix = d.InvPrefix;
        Currency = d.Currency ?? "MYR";
        CurrRate = d.CurrRate;
        CurrRateValid = d.CurrRateValid;
        PayCode = d.PayCode;
        TaxGrCode = d.TaxGrCode;
        SalesmanCode = d.SalesmanCode;
        _taxable = d.Taxable;
        _discountMethod = d.DiscountMethod;
        _decPoint = d.DecPoint == true;
        InvName = d.InvName;
        InvAddress1 = d.InvAddress1;
        InvAddress2 = d.InvAddress2;
        InvAddress3 = d.InvAddress3;
        InvAddress4 = d.InvAddress4;
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
    }

    protected async Task OnInvDateChanged(DateTime newDate)
    {
        if (!CanEditDocument)
        {
            return;
        }

        var previous = InvDate;
        InvDate = newDate.Date;
        if (previous.Date != InvDate.Date && _hasShipment)
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

        var result = await Invoices.ResolveCurrencyRateAsync(Currency, InvDate, _cts.Token);
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

    protected Task OnPayCodeChanged(string? value)
    {
        PayCode = value;
        OnHeaderFieldChanged();
        return Task.CompletedTask;
    }

    protected Task OnTaxGrCodeChanged(string? value)
    {
        TaxGrCode = value;
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
        Popup = new SaInvoiceLineVm
        {
            FrWarehouse = Warehouses.FirstOrDefault()?.WarehouseCode,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? false
        };
        PopupDiscountIsAmount = false;
        PopupError = null;
        PopupVisible = true;
    }

    protected void EditLine(SaInvoiceLineVm line)
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

    protected void RemoveLine(SaInvoiceLineVm line)
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
                ? await Invoices.SaveNewAsync(request, _cts.Token)
                : await Invoices.UpdateAsync(InvNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result, stayOnPage: true))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo($"/sales/invoices/edit/{result.InvNo}");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnAddShipmentAsync()
    {
        if (IsNewMode || string.IsNullOrWhiteSpace(InvNo))
        {
            ErrorMessage = "Save the invoice before adding shipment.";
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
                var saved = await Invoices.UpdateAsync(InvNo, ToRequest(), _cts.Token);
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

            var ship = await Invoices.AddShipmentAsync(InvNo, overwriteExisting: false, _rowVersion, _cts.Token);
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
                    ? "ST000051: No eligible stock found. Check warehouse, tenant location, ACTIVE status, and lot date versus invoice date."
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
        if (string.IsNullOrWhiteSpace(InvNo) || IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var token = _shipConfirmToken ?? _rowVersion;
            var ship = await Invoices.AddShipmentAsync(InvNo, overwriteExisting: true, token, _cts.Token);
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
        if (string.IsNullOrWhiteSpace(InvNo) || !_hasShipment)
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

    protected Task OnShipmentAppliedAsync(SaInvoiceDocument document)
    {
        ApplyDocument(document);
        ShipEditorVisible = false;
        _shipEditLine = null;
        StatusMessage = "Shipment line updated.";
        return Task.CompletedTask;
    }

    protected void OnShipmentApplyFailed((string Message, SaInvoiceDocument? Document) args)
    {
        ErrorMessage = args.Message;
        // Do not wipe editor input; SpShipmentEditor keeps submitted IssueQty.
    }

    protected Task OnCancelAsync()
    {
        if (IsEditMode || _isDirty || (IsNewMode && (Lines.Count > 0 || HasCustomer)))
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/sales/invoices");
        return Task.CompletedTask;
    }

    protected void OnClose() => Navigation.NavigateTo("/sales/invoices");

    protected void OnEditFromView() => Navigation.NavigateTo($"/sales/invoices/edit/{InvNo}");

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/sales/invoices");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || string.IsNullOrWhiteSpace(InvNo))
        {
            return;
        }

        var result = await Invoices.GetAsync(InvNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload invoice.";
            return;
        }

        ApplyDocument(result.Document);
        await CaptureCustomerFlagsAsync(result.Document.CustCode);
        RecalcDocument();
        _isDirty = false;
        ValidationErrors.Clear();
        StatusMessage = "Loaded latest version.";
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private bool HandleOperationResult(SaInvoiceOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        switch (result.ErrorKind)
        {
            case SaInvoiceErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                ErrorMessage = result.ErrorMessage ?? "Validation failed.";
                break;
            case SaInvoiceErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This invoice was changed by another user.";
                break;
            case SaInvoiceErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Invoice was not found.";
                if (!stayOnPage)
                {
                    Navigation.NavigateTo("/sales/invoices");
                }

                break;
            case SaInvoiceErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private SaInvoiceSaveRequest ToRequest() =>
        new()
        {
            InvDate = InvDate,
            CustCode = CustCode ?? string.Empty,
            Currency = Currency,
            PayCode = PayCode,
            TaxGrCode = TaxGrCode,
            SalesmanCode = SalesmanCode,
            PoNo = PoNo,
            Remark = Remark,
            InvName = InvName,
            InvAddress1 = InvAddress1,
            InvAddress2 = InvAddress2,
            InvAddress3 = InvAddress3,
            InvAddress4 = InvAddress4,
            InvCity = InvCity,
            InvState = InvState,
            InvPostalCode = InvPostalCode,
            InvCountry = InvCountry,
            InvTel = InvTel,
            InvFax = InvFax,
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

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(states, 0m);
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

    private void RefreshPackFromItem(SaInvoiceLineVm line)
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
}

public sealed class SaInvoiceLineVm
{
    public int Line { get; set; }
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

    public SaInvoiceLineVm Clone() => new()
    {
        Line = Line,
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

    public SaInvoiceLineRequest ToRequest() =>
        new()
        {
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

    public static SaInvoiceLineVm FromDto(SaInvoiceLineDto dto) =>
        new()
        {
            Line = dto.Line,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.Qty,
            StdPackSize = dto.StdPackSize,
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
            TaxGrCode = dto.TaxGrCode,
            Amount = dto.Amount,
            TaxAmt = dto.TaxAmt,
            NetAmount = dto.NetAmount,
            StockControl = dto.StockControl,
            ShipmentComplete = dto.ShipmentComplete,
            Remarks = dto.Remarks
        };
}
