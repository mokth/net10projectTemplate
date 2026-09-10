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
    [Inject] private ISaSoService Sos { get; set; } = default!;
    [Inject] private ISaDoService Dos { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool SoPickerVisible;
    protected bool SoPickerLoading;
    protected bool DoPickerVisible;
    protected bool DoPickerLoading;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCustChangeVisible;
    protected bool ConfirmShipOverwriteVisible;
    protected bool ShipEditorVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected string? SoPickerError;
    protected bool CanEditPermission;
    protected string InvNoDisplay = "AUTO";
    protected string DoNoDisplay = string.Empty;
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
    protected string? InvEmail;
    protected DateTime? DueDate;
    protected string? BuyerTin;
    protected string? BuyerBrn;
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
    protected List<string> PostWarnings { get; set; } = [];
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
    private int _customerApplySeq;
    private CancellationTokenSource _cts = new();
    private string? _shipConfirmMessage;
    private byte[]? _shipConfirmToken;
    private int? _shipEditLine;
    private int? _shipToLine;
    private IReadOnlyList<SaCustAddressVm> _shipToOptions = [];

    protected List<SaInvoiceLineVm> Lines { get; set; } = [];
    protected List<SaInvoiceCustomerLookupRow> Customers { get; set; } = [];
    protected List<SaInvoiceItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<SaInvoiceTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<IvCodeLookupRow> SalesReps { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classifications { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected IReadOnlyList<SaCustAddressVm> ShipToOptions => _shipToOptions;
    protected int? ShipToLine => _shipToLine;
    protected SaInvoiceLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }
    protected List<SaSoPickerOption> SoPickerOptions { get; set; } = [];
    protected List<SaSoLineDto> SoPickerLines { get; set; } = [];
    protected IReadOnlyList<SaSoLineDto> SelectedSoPickerLines { get; set; } = [];
    protected string? SelectedSourceSoNo { get; set; }
    protected List<SaDoBillablePickerRow> DoPickerLines { get; set; } = [];
    protected IReadOnlyList<SaDoBillablePickerRow> SelectedDoPickerLines { get; set; } = [];
    protected string? DoPickerError { get; set; }

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;

    /// <summary>
    /// New/Edit always show Source. View shows it when DoNo display value is non-blank.
    /// </summary>
    protected bool ShowSourceCard =>
        !IsViewMode || !string.IsNullOrWhiteSpace(DoNoDisplay);

    protected bool CanEditAddresses =>
        CanEditDocument
        && string.Equals(StatusDisplay, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CustCode);
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
    protected bool CanOpenSoPicker => CanMutateLines && HasCustomer;
    protected bool CanOpenDoPicker => CanMutateLines && HasCustomer;
    protected bool TaxGroupRequired => _taxable == true;
    protected bool HasContact =>
        !string.IsNullOrWhiteSpace(InvTel) || !string.IsNullOrWhiteSpace(InvEmail);
    protected bool HasBuyerId =>
        !string.IsNullOrWhiteSpace(BuyerTin) || !string.IsNullOrWhiteSpace(BuyerBrn);
    protected string ContactRequiredCss => HasContact ? string.Empty : "required-field";
    protected string BuyerIdRequiredCss => HasBuyerId ? string.Empty : "required-field";
    /// <summary>
    /// Local UX only. Must not gate on master-derived AR GL / selling GL / tax GL / classification.
    /// </summary>
    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasCustomer
        && CurrRateValid
        && !string.IsNullOrWhiteSpace(PayCode)
        && (!TaxGroupRequired || !string.IsNullOrWhiteSpace(TaxGrCode))
        && !string.IsNullOrWhiteSpace(SalesmanCode)
        && !string.IsNullOrWhiteSpace(InvName)
        && !string.IsNullOrWhiteSpace(InvAddress1)
        && !string.IsNullOrWhiteSpace(InvCity)
        && !string.IsNullOrWhiteSpace(InvPostalCode)
        && !string.IsNullOrWhiteSpace(InvCountry)
        && HasContact;
    // BuyerTin/BuyerBrn intentionally omitted: server snapshots from customer master.
    protected string FooterHint
    {
        get
        {
            if (IsViewMode || CanSave)
            {
                return $"{(_isDirty ? "Unsaved changes" : "Ready")} · {LineCountLabel}";
            }

            if (IsSubmitting)
            {
                return "Saving…";
            }

            if (!HasCustomer)
            {
                return "Select a customer";
            }

            if (!CurrRateValid)
            {
                return "Valid FX rate is required";
            }

            if (string.IsNullOrWhiteSpace(PayCode))
            {
                return "Payment term is required";
            }

            if (TaxGroupRequired && string.IsNullOrWhiteSpace(TaxGrCode))
            {
                return "Tax group is required";
            }

            if (string.IsNullOrWhiteSpace(SalesmanCode))
            {
                return "Salesman is required";
            }

            if (string.IsNullOrWhiteSpace(InvName)
                || string.IsNullOrWhiteSpace(InvAddress1)
                || string.IsNullOrWhiteSpace(InvCity)
                || string.IsNullOrWhiteSpace(InvPostalCode)
                || string.IsNullOrWhiteSpace(InvCountry))
            {
                return "Complete billing address";
            }

            if (!HasContact)
            {
                return "Telephone or email is required";
            }

            if (Lines.Count == 0)
            {
                return "Add at least one line";
            }

            return $"{(_isDirty ? "Unsaved changes" : "Ready")} · {LineCountLabel}";
        }
    }
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
        PostWarnings.Clear();
        ConfirmDiscardVisible = false;
        ConfirmCustChangeVisible = false;
        ConfirmShipOverwriteVisible = false;
        ConcurrencyVisible = false;
        DateShipmentWarning = false;
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;
        ResetSoPicker();
        ResetDoPicker();

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
            SalesReps = lookups.SalesReps.ToList();
        }

        Countries = await Lookups.ListCountriesForAssignmentAsync(_cts.Token);
        States = await Lookups.ListStatesForAssignmentAsync(_cts.Token);
        var classifications = await InventoryLookups.ListClassificationsAsync(_cts.Token);
        Classifications = classifications.Succeeded ? classifications.Rows : [];
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
        PostWarnings = result.PostWarnings.ToList();
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        IsLoading = false;
    }

    private void ResetNewDocument()
    {
        InvNo = null;
        InvNoDisplay = "AUTO";
        DoNoDisplay = string.Empty;
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
        InvEmail = null;
        DueDate = null;
        BuyerTin = null;
        BuyerBrn = null;
        ClearAddresses();
        ClearShipToState();
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
        ResetSoPicker();
        ResetDoPicker();
    }

    private void ClearAddresses()
    {
        InvName = InvAddress1 = InvAddress2 = InvAddress3 = InvAddress4 = null;
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
        InvPrefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        PayCode = null;
        TaxGrCode = null;
        SalesmanCode = null;
        Remark = null;
        ClearAddresses();
        ClearShipToState();
        _taxable = null;
        _discountMethod = null;
        _decPoint = false;
        ResetSoPicker();
        ResetDoPicker();
    }

    private static string NormCustCode(string? custCode) => (custCode ?? string.Empty).Trim();

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
        InvEmail = doc.InvEmail;
        DueDate = doc.DueDate;
        BuyerTin = doc.BuyerTin;
        BuyerBrn = doc.BuyerBrn;
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

        var result = await Invoices.GetCustomerDefaultsAsync(code, InvDate, _cts.Token);
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
            _shipToLine = null; // combo empty; keep saved Inv*/Ship*
            return;
        }

        if (seq != _customerApplySeq)
        {
            return;
        }

        ApplyDefaults(d);
        _shipToLine = null;
        Remark = null;
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
        ResetDoPicker();
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
        if (string.IsNullOrWhiteSpace(InvEmail))
        {
            InvEmail = d.InvEmail;
        }

        if (string.IsNullOrWhiteSpace(BuyerTin))
        {
            BuyerTin = d.BuyerTin;
        }

        if (string.IsNullOrWhiteSpace(BuyerBrn))
        {
            BuyerBrn = d.BuyerBrn;
        }

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
            // Combo cleared — Ship* unchanged (no AppShip restore)
            return;
        }

        var row = _shipToOptions.FirstOrDefault(x => x.Line == line);
        if (row is null)
        {
            _shipToLine = null; // stale Line after list refresh
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
        // Address4 deliberately omitted — no ShipAddress4 on invoice
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
            var result = await Sos.GetBillableLinesAsync(requestedSoNo, _cts.Token);
            if (_disposed)
            {
                return;
            }

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
                .Where(x => !x.LinkDo && !string.IsNullOrWhiteSpace(x.SoNo) && x.SoLine is not null)
                .Select(x => (SoNo: x.SoNo!, SoLine: (int)x.SoLine!.Value));
            // Grid holds exactly one selected SO; KeyFieldName=Line is unique within this collection.
            SoPickerLines = SaDocPickerLines.FilterRemainingSoLines(
                result.RemainingLines,
                requestedSoNo,
                existingKeys).ToList();
            SelectedSoPickerLines = SaDocPickerLines.SelectAllCurrent(SoPickerLines);
            if (SoPickerLines.Count == 0)
            {
                SoPickerError = "Selected sales order has no remaining billable lines (qty may be reserved on delivery orders or other draft invoices).";
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

        if (Lines.Any(x => x.LinkDo))
        {
            SoPickerError = "This invoice already has Delivery Order lines. Mixed invoices are not allowed.";
            return;
        }

        var added = 0;
        foreach (var source in SelectedSoPickerLines)
        {
            var keyExists = Lines.Any(x =>
                string.Equals(x.SoNo, SelectedSourceSoNo, StringComparison.OrdinalIgnoreCase)
                && x.SoLine == source.Line);
            if (keyExists)
            {
                continue;
            }

            Lines.Add(ApplyItemClassification(SaInvoiceLineVm.FromSalesOrder(source, SelectedSourceSoNo)));
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
        ResetDoPicker();
    }

    protected async Task OpenDoPickerAsync()
    {
        if (!CanOpenDoPicker)
        {
            return;
        }

        DoPickerVisible = true;
        DoPickerError = null;
        SelectedDoPickerLines = [];
        DoPickerLines = [];
        DoPickerLoading = true;
        try
        {
            var result = await Dos.GetBillableLinesAsync(CustCode!, Currency, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                DoPickerError = result.ErrorMessage ?? "Unable to load posted delivery orders.";
                DoPickerLines = [];
                SelectedDoPickerLines = [];
                return;
            }

            var existingKeys = Lines
                .Where(x => x.LinkDo && !string.IsNullOrWhiteSpace(x.DoNo) && x.DoLine is not null)
                .Select(x => (DoNo: x.DoNo!, DoLine: (int)x.DoLine!.Value));
            var filtered = SaDocPickerLines.FilterRemainingDoLines(result.BillableLines, existingKeys);
            DoPickerLines = filtered
                .Select(x => new SaDoBillablePickerRow { Source = x })
                .ToList();
            SelectedDoPickerLines = SaDocPickerLines.SelectAllCurrent(DoPickerLines);
            if (DoPickerLines.Count == 0)
            {
                DoPickerError = "No remaining posted delivery order lines for this customer.";
            }
        }
        finally
        {
            DoPickerLoading = false;
        }
    }

    protected void OnSelectedDoPickerLinesChanged(IReadOnlyList<object> selected)
    {
        SelectedDoPickerLines = selected.OfType<SaDoBillablePickerRow>().ToList();
    }

    protected void CloseDoPicker()
    {
        DoPickerVisible = false;
    }

    protected void AddFromDo()
    {
        if (SelectedDoPickerLines.Count == 0)
        {
            DoPickerError = "Select at least one delivery order line.";
            return;
        }

        if (Lines.Any(x => !x.LinkDo && !string.IsNullOrWhiteSpace(x.SoNo)))
        {
            DoPickerError = "This invoice already has direct Sales Order lines. Mixed invoices are not allowed.";
            return;
        }

        var added = 0;
        foreach (var row in SelectedDoPickerLines)
        {
            var source = row.Source;
            var keyExists = Lines.Any(x =>
                x.LinkDo
                && string.Equals(x.DoNo, source.DoNo, StringComparison.OrdinalIgnoreCase)
                && x.DoLine == source.Line);
            if (keyExists)
            {
                continue;
            }

            Lines.Add(ApplyItemClassification(SaInvoiceLineVm.FromDeliveryOrder(source)));
            added++;
        }

        if (added == 0)
        {
            DoPickerError = "All remaining delivery order lines are already added.";
            return;
        }

        Renumber();
        Lines = Lines.ToList();
        RecalcDocument();
        MarkDirty();
        StatusMessage = added == 1 ? "1 delivery order line added." : $"{added} delivery order lines added.";
        ResetDoPicker();
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
        Popup.Classification = item.Classification;
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

        if (string.IsNullOrWhiteSpace(Popup.Classification))
        {
            PopupError = "Classification is required.";
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
            if (result.PostWarnings.Count > 0)
            {
                StatusMessage = "Invoice saved.";
                PostWarnings = result.PostWarnings.ToList();
                if (IsNewMode)
                {
                    Navigation.NavigateTo($"/sales/invoices/edit/{result.InvNo}");
                    return;
                }

                if (result.Document is not null)
                {
                    ApplyDocument(result.Document);
                    RecalcDocument();
                }

                return;
            }

            Navigation.NavigateTo("/sales/invoices");
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
        if (_isDirty)
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
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
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
            InvEmail = InvEmail,
            BuyerTin = BuyerTin,
            BuyerBrn = BuyerBrn,
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
            ValidationErrors.Clear();
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

    private void ResetDoPicker()
    {
        DoPickerVisible = false;
        DoPickerLoading = false;
        DoPickerError = null;
        DoPickerLines = [];
        SelectedDoPickerLines = [];
    }

    private SaInvoiceLineVm ApplyItemClassification(SaInvoiceLineVm line)
    {
        if (string.IsNullOrWhiteSpace(line.Classification))
        {
            line.Classification = ClassificationFromItem(line.ICode);
        }

        return line;
    }

    private string? ClassificationFromItem(string? iCode) =>
        Items.FirstOrDefault(x => string.Equals(x.ICode, iCode, StringComparison.OrdinalIgnoreCase))
            ?.Classification;
}

public sealed class SaInvoiceLineVm
{
    public int Line { get; set; }
    public string? SoNo { get; set; }
    public short? SoLine { get; set; }
    public short? CustRel { get; set; }
    public string? CustPo { get; set; }
    public bool HasSource => !string.IsNullOrWhiteSpace(SoNo) || LinkDo;
    public string? SoRevDisplay =>
        string.IsNullOrWhiteSpace(SoNo) ? null : (CustRel is > 0 ? CustRel.Value.ToString() : "1");
    public string? SoLineDisplay => SoLine is > 0 ? SoLine.Value.ToString() : null;
    public string? SourceDoNo => LinkDo && !string.IsNullOrWhiteSpace(DoNo) ? DoNo : null;
    public string? SourceDoLine => LinkDo && DoLine is > 0 ? DoLine.Value.ToString() : null;
    public bool LinkDo { get; set; }
    public string? DoNo { get; set; }
    public short? DoLine { get; set; }
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
    public string? Classification { get; set; }
    public string? Remarks { get; set; }

    public SaInvoiceLineVm Clone() => new()
    {
        Line = Line,
        SoNo = SoNo,
        SoLine = SoLine,
        CustRel = CustRel,
        CustPo = CustPo,
        LinkDo = LinkDo,
        DoNo = DoNo,
        DoLine = DoLine,
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
            Classification = Classification,
            Remarks = Remarks
        };

    public SaInvoiceLineRequest ToRequest() =>
        new()
        {
            SoNo = SoNo,
            SoLine = SoLine,
            CustRel = CustRel,
            LinkDo = LinkDo,
            DoNo = DoNo,
            DoLine = DoLine,
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
            Classification = Classification,
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
            SoNo = dto.SoNo,
            SoLine = dto.SoLine,
            CustRel = dto.CustRel,
            CustPo = dto.CustPo,
            LinkDo = dto.LinkDo,
            DoNo = dto.DoNo,
            DoLine = dto.DoLine,
            SoConsumedQty = dto.SoConsumedQty,
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
            Classification = dto.Classification,
            Remarks = dto.Remarks
        };

    public static SaInvoiceLineVm FromSalesOrder(SaSoLineDto dto, string soNo) =>
        new()
        {
            SoNo = soNo,
            SoLine = checked((short)dto.Line),
            CustRel = dto.CustRel,
            CustPo = dto.CustPo,
            LinkDo = false,
            DoNo = null,
            DoLine = null,
            SoConsumedQty = 0m,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.RemainingBillableQty > 0m ? dto.RemainingBillableQty : dto.BalanceQty,
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
            Classification = dto.Classification,
            Remarks = dto.Remarks
        };

    public static SaInvoiceLineVm FromDeliveryOrder(SaDoBillableLineDto dto) =>
        new()
        {
            SoNo = !string.IsNullOrWhiteSpace(dto.SoNo) && dto.SoLine > 0 ? dto.SoNo : null,
            SoLine = !string.IsNullOrWhiteSpace(dto.SoNo) && dto.SoLine > 0 ? dto.SoLine : null,
            CustRel = !string.IsNullOrWhiteSpace(dto.SoNo) && dto.SoLine > 0 ? dto.CustRel : null,
            CustPo = dto.CustPo,
            LinkDo = true,
            DoNo = dto.DoNo,
            DoLine = dto.Line,
            SoConsumedQty = 0m,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.RemainingBillableQty,
            FrWarehouse = dto.FrWarehouse,
            UnitPrice = dto.UnitPrice,
            StockControl = dto.StockControl,
            ShipmentComplete = true,
            Classification = null,
            Remarks = null
        };
}
