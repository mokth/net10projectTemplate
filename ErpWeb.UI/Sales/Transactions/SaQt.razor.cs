using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// Sales Quotation entry / revise / view. Mirrors the Sales Order screen's shape (header form,
/// address tabs, line grid, totals, flow panel) and deliberately drops everything an SO needs but a
/// quotation does not: no fulfilment or billing status, no shipped/delivered/invoiced quantity, no
/// stock reservation, no force-close.
/// <para>
/// Quotation-only additions: <c>Valid until</c>, <c>Internal remarks</c>, <c>Ship via</c>,
/// <c>Delivery terms</c>, the lifecycle actions (Send / Accept / Lose / Cancel / Convert) and the
/// converted-Sales-Order link.
/// </para>
/// </summary>
public partial class SaQt : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? QtNo { get; set; }
    [Parameter] public int? CustRel { get; set; }

    [Inject] private ISaQtService Qts { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private ISaSalesRefService SalesRefService { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ErpWeb.Core.Settings.IAppSettingService AppSettings { get; set; } = default!;

    /// <summary>
    /// The offer window applied to a NEW quotation, from the company's SALES.QUOTE_VALID_DAYS setting.
    /// Seeded with the shipped default so the screen is correct even before the read completes.
    /// </summary>
    private int _validityDays = SaQtValidity.DefaultValidityDays;

    protected string? StatusMessage;
    protected string? WarningMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool LoseVisible;
    protected bool ConvertVisible;
    protected bool CancelVisible;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCustChangeVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected string? LoseError;
    protected string LostReasonDraft = string.Empty;

    protected bool CanEditPermission;
    protected bool CanSendPermission;
    protected bool CanAcceptPermission;
    protected bool CanLosePermission;
    protected bool CanCancelPermission;
    protected bool CanConvertPermission;
    protected bool CanOverridePrice;

    protected string QtNoDisplay = "AUTO";
    protected short CustRelDisplay = 1;
    protected short LastCustRelDisplay = 1;
    protected bool IsCurrentDocument = true;
    protected string StatusDisplay = SaQtStatuses.New;
    protected string ConversionStatusDisplay = SaQtConversionStatuses.None;
    protected string? ConvertedSoNo;
    protected bool IsExpiredDisplay;
    protected DateTime QtDate;
    protected DateTime ValidUntil;
    protected string? CustCode;
    protected string? CustName;
    protected string? Prefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? ContactPerson;
    protected string? TaxGrCode;
    protected string? SalesRep;
    protected string? PayCode;
    protected string? CustPo;
    protected string? Ref1;
    protected string? ProjId;
    protected string? Remarks;
    protected string? InternalRemarks;
    protected string? ShipVia;
    protected string? DeliveryTerms;
    protected string? RevisionReason;
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
    protected string? ShipAddress4;
    protected string? ShipCity;
    protected string? ShipState;
    protected string? ShipPostalCode;
    protected string? ShipCountry;
    protected string? ShipTel;
    protected string? ShipFax;
    protected decimal GrossAmnt;
    protected decimal Taxes;
    protected decimal TotAmnt;
    protected int ActiveTabIndex;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private SaQtLineVm? _editingLine;
    private string? _loadedKey;
    private bool _isDirty;
    private bool _disposed;
    private bool _isApplyingDefaults;
    private string? _pendingCustCode;
    private string? _discountMethod;
    private bool _decPoint;
    private bool? _taxable;
    private byte[] _rowVersion = [];
    private int _customerApplySeq;
    private int _priceApplySeq;
    private string? _priceBlockMessage;
    private string? _priceHint;
    private (decimal P1, decimal P2, decimal A1, decimal A2)? _autoDiscountSlots;
    private int? _shipToLine;
    private IReadOnlyList<SaCustAddressVm> _shipToOptions = [];
    private readonly CancellationTokenSource _cts = new();

    protected List<SaQtLineVm> Lines { get; set; } = [];
    protected List<SaQtRevisionHistoryRow> Revisions { get; set; } = [];
    protected List<SaSoCustomerLookupRow> Customers { get; set; } = [];
    protected List<SaSoItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<SaSoTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> SalesReps { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<IvCodeLookupRow> Projects { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected IReadOnlyList<SaCustAddressVm> ShipToOptions => _shipToOptions;
    protected int? ShipToLine => _shipToLine;
    protected SaQtLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsReviseMode => string.Equals(Mode, "revise", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsClosedStatus => string.Equals(StatusDisplay, SaQtStatuses.Closed, StringComparison.OrdinalIgnoreCase);
    protected bool IsSupersededStatus => string.Equals(StatusDisplay, SaQtStatuses.Superseded, StringComparison.OrdinalIgnoreCase);
    protected bool IsTerminalStatus =>
        IsClosedStatus
        || string.Equals(StatusDisplay, SaQtStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
        || string.Equals(StatusDisplay, SaQtStatuses.Lost, StringComparison.OrdinalIgnoreCase);
    protected bool IsHistoricalRevision => !IsCurrentDocument || IsSupersededStatus;
    protected bool IsReadOnlyPresentation => IsViewMode || IsTerminalStatus;
    protected bool IsConverted => string.Equals(ConversionStatusDisplay, SaQtConversionStatuses.Full, StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument =>
        (IsNewMode || IsEditMode || IsReviseMode)
        && !IsViewMode
        && !IsTerminalStatus
        && !IsHistoricalRevision
        && string.Equals(StatusDisplay, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Plan Phase 4: the line price is editable only for an operator holding PRICE_OVERRIDE. Without it
    /// the field is shown but locked, so the resolved price is visible rather than hidden.
    /// </summary>
    protected bool CanEditLinePrice => CanEditDocument && CanOverridePrice;
    protected bool CanEditCustomer => CanEditDocument;
    protected bool CanEditAddresses => CanEditDocument && !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && CanEditDocument is false
        && string.Equals(StatusDisplay, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase)
        && IsCurrentDocument
        && !string.IsNullOrWhiteSpace(QtNo);

    // ── Lifecycle action gates. UX only — the service re-checks every one of them server-side. ──
    protected bool CanSendAction =>
        IsCurrentDocument && !IsExpiredDisplay && !IsConverted
        && string.Equals(StatusDisplay, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase)
        && CanSendPermission;
    protected bool CanAcceptAction =>
        IsCurrentDocument && !IsExpiredDisplay && !IsConverted
        && string.Equals(StatusDisplay, SaQtStatuses.Sent, StringComparison.OrdinalIgnoreCase)
        && CanAcceptPermission;
    protected bool CanLoseAction =>
        IsCurrentDocument && !IsConverted
        && StatusDisplay is SaQtStatuses.New or SaQtStatuses.Sent
        && CanLosePermission;
    protected bool CanCancelAction =>
        IsCurrentDocument && !IsConverted
        && StatusDisplay is SaQtStatuses.New or SaQtStatuses.Sent
        && CanCancelPermission;
    protected bool CanConvertAction =>
        IsCurrentDocument
        && !IsExpiredDisplay
        && !IsConverted
        && string.Equals(StatusDisplay, SaQtStatuses.Accepted, StringComparison.OrdinalIgnoreCase)
        && CanConvertPermission;
    protected bool CanReviseAction =>
        IsCurrentDocument
        && !IsConverted
        && SaQtStatuses.Revisable.Contains(StatusDisplay)
        && string.IsNullOrWhiteSpace(ConvertedSoNo);

    protected string PageHeading => IsNewMode ? "New sales quotation" : IsEditMode ? "Edit sales quotation" : IsReviseMode ? "Revise sales quotation" : "View sales quotation";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected bool HasCustomer => !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanMutateLines => CanEditDocument && HasCustomer && CurrRateValid && !IsSubmitting;
    protected bool TaxGroupRequired => _taxable == true;
    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasCustomer
        && CurrRateValid
        && !string.IsNullOrWhiteSpace(PayCode)
        && ValidUntil >= QtDate.Date
        && (!TaxGroupRequired || !string.IsNullOrWhiteSpace(TaxGrCode));
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit line" : "Add line";
    protected string PopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected bool PopupInclusiveLocked => Lines.Count > 1 || (_editingLine is null && Lines.Count > 0);
    protected SaInvoiceLineCalcState PopupCalc => BuildPopupCalc();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{QtNo}:{CustRel}";
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
        WarningMessage = null;
        ValidationErrors.Clear();
        ConfirmDiscardVisible = false;
        ConfirmCustChangeVisible = false;
        ConcurrencyVisible = false;
        LoseVisible = false;
        ConvertVisible = false;
        CancelVisible = false;
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Edit);
        CanSendPermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Submit);
        CanAcceptPermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Approve);
        CanLosePermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Reject);
        CanCancelPermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Cancel);
        CanConvertPermission = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Close);
        // Plan Phase 4. The service enforces this too - the page is UX, not the execution point.
        CanOverridePrice = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.PriceOverride);

        var lookups = await Qts.GetLookupsAsync(_cts.Token);
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
            await LoadValidityDaysAsync();
            ResetNewDocument();
            IsLoading = false;
            return;
        }

        var result = IsReviseMode
            ? await Qts.GetReviseDraftAsync(QtNo ?? string.Empty, _cts.Token)
            : IsViewMode && CustRel is { } revision
                ? await Qts.GetAsync(QtNo ?? string.Empty, checked((short)revision), _cts.Token)
                : await Qts.GetAsync(QtNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Quotation was not found.";
            if (result.ErrorKind == SaQtErrorKind.NotFound)
            {
                Navigation.NavigateTo("/sales/quotations");
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
        QtNo = null;
        QtNoDisplay = "AUTO";
        CustRelDisplay = 1;
        LastCustRelDisplay = 1;
        IsCurrentDocument = true;
        StatusDisplay = SaQtStatuses.New;
        ConversionStatusDisplay = SaQtConversionStatuses.None;
        ConvertedSoNo = null;
        IsExpiredDisplay = false;
        QtDate = Dates.Today.Date;
        ValidUntil = SaQtValidity.DefaultValidUntil(QtDate, _validityDays);
        CustCode = null;
        CustName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        ContactPerson = null;
        TaxGrCode = null;
        SalesRep = null;
        PayCode = null;
        CustPo = null;
        Ref1 = null;
        ProjId = null;
        Remarks = null;
        InternalRemarks = null;
        ShipVia = null;
        DeliveryTerms = null;
        RevisionReason = null;
        ClearAddresses();
        ClearShipToState();
        Lines = [];
        Revisions = [];
        GrossAmnt = 0m;
        Taxes = 0m;
        TotAmnt = 0m;
        _rowVersion = [];
        _discountMethod = null;
        _decPoint = false;
        _taxable = null;
    }

    private void ClearAddresses()
    {
        InvName = InvAddress1 = InvAddress2 = InvAddress3 = InvAddress4 = null;
        InvCity = InvState = InvPostalCode = InvCountry = InvTel = InvFax = null;
        ShipName = ShipAddress1 = ShipAddress2 = ShipAddress3 = ShipAddress4 = null;
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
        PayCode = null;
        CustPo = null;
        Ref1 = null;
        ProjId = null;
        Remarks = null;
        ClearAddresses();
        ClearShipToState();
        _taxable = null;
        _discountMethod = null;
        _decPoint = false;
    }

    private static string NormCustCode(string? custCode) => (custCode ?? string.Empty).Trim();

    private void ApplyDocument(SaQtDocument doc)
    {
        QtNo = doc.QtNo;
        QtNoDisplay = doc.QtNo;
        CustRelDisplay = doc.CustRel;
        LastCustRelDisplay = doc.LastCustRel;
        IsCurrentDocument = doc.IsCurrent;
        QtDate = doc.QtDate;
        ValidUntil = doc.ValidUntil;
        StatusDisplay = doc.Status;
        ConversionStatusDisplay = string.IsNullOrWhiteSpace(doc.ConversionStatus)
            ? SaQtConversionStatuses.None
            : doc.ConversionStatus;
        ConvertedSoNo = doc.ConvertedSoNo;
        IsExpiredDisplay = doc.IsExpired;
        CustCode = doc.CustCode;
        CustName = doc.CustName;
        Prefix = doc.Prefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = doc.CurrRate > 0m;
        ContactPerson = doc.ContactPerson;
        TaxGrCode = doc.TaxGrCode;
        SalesRep = doc.SalesRep;
        PayCode = doc.PayCode;
        CustPo = doc.CustPo;
        Ref1 = doc.Ref1;
        ProjId = doc.ProjId;
        Remarks = doc.Remarks;
        InternalRemarks = doc.InternalRemarks;
        ShipVia = doc.ShipVia;
        DeliveryTerms = doc.DeliveryTerms;
        RevisionReason = doc.RevisionReason;
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
        ShipAddress4 = doc.ShipAddress4;
        ShipCity = doc.ShipCity;
        ShipState = doc.ShipState;
        ShipPostalCode = doc.ShipPostalCode;
        ShipCountry = doc.ShipCountry;
        ShipTel = doc.ShipTel;
        ShipFax = doc.ShipFax;
        GrossAmnt = doc.GrossAmnt;
        Taxes = doc.Taxes;
        TotAmnt = doc.TotAmnt;
        _rowVersion = doc.RowVersion ?? [];
        Revisions = doc.Revisions.ToList();
        Lines = doc.Lines.Select(SaQtLineVm.FromDto).ToList();
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

        var result = await Qts.GetCustomerDefaultsAsync(code, QtDate, _cts.Token);
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
        if (_isApplyingDefaults || _disposed || !CanEditCustomer)
        {
            return;
        }

        var next = NormCustCode(value);
        next = string.IsNullOrEmpty(next) ? null : next;
        if (string.Equals(CustCode, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Lines.Count > 0 && next is not null)
        {
            _pendingCustCode = next;
            ConfirmCustChangeVisible = true;
            return;
        }

        if (Lines.Count > 0 && next is null)
        {
            Lines.Clear();
        }

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

    private void ApplyDefaults(SaSoCustomerDefaults d)
    {
        CustCode = d.CustCode;
        CustName = d.CustName;
        Prefix = null;
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
        ShipAddress4 = d.ShipAddress4;
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
        ShipAddress4 = row.Address4;
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

    protected async Task OnQtDateChanged(DateTime newDate)
    {
        if (!CanEditDocument)
        {
            return;
        }

        var previousDefault = SaQtValidity.DefaultValidUntil(QtDate, _validityDays);
        QtDate = newDate.Date;
        // Only follow the date when ValidUntil was still sitting on the previous default, so a
        // deliberately chosen validity is never silently moved.
        if (ValidUntil == previousDefault)
        {
            ValidUntil = SaQtValidity.DefaultValidUntil(QtDate, _validityDays);
        }

        await RefreshFxAsync();
        MarkDirty();
        RecalcDocument();
    }

    /// <summary>
    /// Reads the company's quotation validity window (SALES.QUOTE_VALID_DAYS).
    ///
    /// <para>
    /// The read goes through the settings service, so the value is the company's, not the browser's. A read
    /// that fails leaves the shipped default in place: the screen still works, and the save path
    /// independently applies the same fallback.
    /// </para>
    /// </summary>
    private async Task LoadValidityDaysAsync()
    {
        var result = await AppSettings.GetValueAsync(
            ErpWeb.Core.Settings.AppSettingModules.Sales,
            ErpWeb.Core.Settings.AppSettingCatalogue.SalesKeys.QuoteValidDays,
            ErpWeb.Core.Settings.AppSettingScope.Company,
            CurrentUser.CompanyCode,
            branchCode: null,
            _cts.Token);

        var parsed = decimal.TryParse(
            result.Data,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var days);

        _validityDays = result.Succeeded && parsed && days > 0m
            ? (int)days
            : SaQtValidity.DefaultValidityDays;
    }

    protected Task OnValidUntilChanged(DateTime newDate)
    {
        if (!CanEditDocument)
        {
            return Task.CompletedTask;
        }

        ValidUntil = newDate.Date;
        ValidationErrors.Remove("ValidUntil");
        MarkDirty();
        return Task.CompletedTask;
    }

    private async Task RefreshFxAsync()
    {
        if (string.IsNullOrWhiteSpace(Currency))
        {
            CurrRateValid = false;
            return;
        }

        var result = await Qts.ResolveCurrencyRateAsync(Currency, QtDate, _cts.Token);
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

    // ─────────────────────────────────────────────────────────────────────────────
    // Lines
    // ─────────────────────────────────────────────────────────────────────────────

    protected void OnNewLineClick()
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = null;
        Popup = new SaQtLineVm
        {
            Warehouse = Warehouses.FirstOrDefault()?.WarehouseCode,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? false
        };
        PopupDiscountIsAmount = false;
        PopupError = null;
        _priceBlockMessage = null;
        _priceHint = null;
        _autoDiscountSlots = null;
        PopupVisible = true;
    }

    protected void EditLine(SaQtLineVm line)
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = line;
        Popup = line.Clone();
        // Re-opening a saved line re-establishes the override baseline. A line written before the feature
        // existed has no recorded engine price, so its stored price is the best available baseline.
        Popup.OriginalUnitPrice ??= line.UnitPrice;
        RefreshPackFromItem(Popup);
        PopupDiscountIsAmount = Popup.ItemDiscAmount != 0m || Popup.ItemDiscAmount1 != 0m;
        PopupError = null;
        _priceBlockMessage = null;
        _priceHint = null;

        // Re-opening a draft line deliberately does NOT re-price it: the price moves only when a
        // pricing input actually changes, so editing an unrelated field can never silently move the
        // line to today's price.
        _autoDiscountSlots = (Popup.ItemDiscount, Popup.ItemDiscount2, Popup.ItemDiscAmount, Popup.ItemDiscAmount1);
        PopupVisible = true;
    }

    protected void RemoveLine(SaQtLineVm line)
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

    protected async Task OnPopupItemChangedAsync(string? iCode)
    {
        Popup.ICode = iCode ?? string.Empty;
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, Popup.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            _priceHint = null;
            return;
        }

        Popup.IDesc = item.IDesc;
        Popup.StdUom = item.StdUom;
        Popup.SellingUom = item.SellingUom;
        Popup.StdPackSize = item.StdPackSize;
        Popup.StockControl = item.StockControl;

        if (!string.IsNullOrWhiteSpace(item.TaxGroup)
            && TaxGroups.Any(x => string.Equals(x.TaxGrCode, item.TaxGroup, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.TaxGrCode = item.TaxGroup;
        }

        if (string.IsNullOrWhiteSpace(Popup.Warehouse))
        {
            Popup.Warehouse = item.DefWarehouse ?? Warehouses.FirstOrDefault()?.WarehouseCode;
        }

        _autoDiscountSlots = null;
        Popup.ItemDiscount = Popup.ItemDiscount2 = Popup.ItemDiscount3 = 0m;
        Popup.ItemDiscount4 = Popup.ItemDiscount5 = Popup.ItemDiscount6 = 0m;
        Popup.ItemDiscAmount = Popup.ItemDiscAmount1 = 0m;
        PopupDiscountIsAmount = false;

        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    protected async Task OnPopupQuantityChangedAsync(decimal qty)
    {
        Popup.OrderQty = qty;
        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    protected async Task OnPopupBasisChangedAsync(bool inclusive)
    {
        Popup.IsInclusive = inclusive;
        await ResolvePopupPriceAsync(assignDiscountSlots: false);
    }

    protected async Task OnPopupTaxChangedAsync(string? taxGrCode)
    {
        Popup.TaxGrCode = taxGrCode;
        await ResolvePopupPriceAsync(assignDiscountSlots: false);
    }

    /// <summary>
    /// The single call site for quotation-line pricing. The engine is reused unchanged from Sales
    /// Order entry, because a quotation is priced by exactly the same rules as the order it may
    /// later become.
    /// </summary>
    private async Task ResolvePopupPriceAsync(bool assignDiscountSlots)
    {
        if (string.IsNullOrWhiteSpace(Popup.ICode) || string.IsNullOrWhiteSpace(CustCode))
        {
            return;
        }

        var seq = Interlocked.Increment(ref _priceApplySeq);

        var uom = !string.IsNullOrWhiteSpace(Popup.SellingUom)
            ? Popup.SellingUom!
            : Popup.StdUom ?? string.Empty;

        var result = await SalesRefService.ResolveLinePricingAsync(
            new SaLinePricingRequest
            {
                CustCode = CustCode!,
                ICode = Popup.ICode,
                UOM = uom,
                Qty = Popup.OrderQty,
                DocDate = QtDate,
                DocumentCurrency = Currency,
                IClass = Popup.Classification,
                TaxPercent = ResolveTaxPercent(Popup.TaxGrCode),
                IsInclusive = Popup.IsInclusive,
                DiscountMethod = _discountMethod
            },
            _cts.Token);

        if (seq != _priceApplySeq || _disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Data is null)
        {
            _priceHint = null;
            Popup.PricingSource = null;
            Popup.PricingRef = null;
            Popup.OriginalUnitPrice = null;
            Popup.OverrideReason = null;
            _priceBlockMessage = result.Message ?? "No price could be resolved for this line.";
            PopupError = _priceBlockMessage;
            return;
        }

        var priced = result.Data;

        Popup.UnitPrice = priced.UnitPrice;
        Popup.PricingSource = priced.PricingSourceToken;
        Popup.PricingRef = priced.PricingRef;
        // Re-baseline: the price just resolved IS the new "engine price", so the line reads as un-overridden
        // and any earlier reason is dropped. Without this a stale baseline would flag a false override.
        Popup.OriginalUnitPrice = priced.UnitPrice;
        Popup.OverrideReason = null;
        _priceHint = priced.Describe();

        if (assignDiscountSlots && DiscountSlotsAreUntouched())
        {
            Popup.ItemDiscount = priced.ItemDiscount;
            Popup.ItemDiscount2 = priced.ItemDiscount2;
            Popup.ItemDiscAmount = priced.ItemDiscAmount;
            Popup.ItemDiscAmount1 = priced.ItemDiscAmount1;
            PopupDiscountIsAmount = priced.ItemDiscAmount != 0m || priced.ItemDiscAmount1 != 0m;
            _autoDiscountSlots = (priced.ItemDiscount, priced.ItemDiscount2, priced.ItemDiscAmount, priced.ItemDiscAmount1);
        }

        if (!string.IsNullOrWhiteSpace(_priceBlockMessage) && PopupError == _priceBlockMessage)
        {
            PopupError = null;
        }

        _priceBlockMessage = null;
    }

    private bool IsPriceOverridden() =>
        Popup.OriginalUnitPrice is { } resolved && resolved != Popup.UnitPrice;

    private bool DiscountSlotsAreUntouched() =>
        _autoDiscountSlots is not { } last
        || (Popup.ItemDiscount == last.P1
            && Popup.ItemDiscount2 == last.P2
            && Popup.ItemDiscAmount == last.A1
            && Popup.ItemDiscAmount1 == last.A2);

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

        if (Popup.OrderQty <= 0m)
        {
            PopupError = "Order quantity must be greater than zero.";
            return;
        }

        // The engine refused to price this line. Saving it would persist a price the pricing rules
        // never produced (the legacy defect was RM 0.00).
        if (!string.IsNullOrWhiteSpace(_priceBlockMessage))
        {
            PopupError = _priceBlockMessage;
            return;
        }

        if (Popup.UnitPrice < 0m)
        {
            PopupError = "Unit price cannot be negative.";
            return;
        }

        // Plan Phase 4: an override must carry a reason. Checked here for a decent message; the service
        // re-checks it (including the PRICE_OVERRIDE permission) because the page is not the gate.
        if (IsPriceOverridden() && string.IsNullOrWhiteSpace(Popup.OverrideReason))
        {
            PopupError = SaPriceOverridePolicy.ReasonRequiredMessage;
            return;
        }

        if (Popup.Etd is { } etd && Popup.Eta is { } eta && etd.Date > eta.Date)
        {
            PopupError = "ETD cannot be after ETA.";
            return;
        }

        if (Popup.Eta is { } etaDate && Popup.DeliveryDate is { } deliveryDate && etaDate.Date > deliveryDate.Date)
        {
            PopupError = "ETA cannot be after delivery date.";
            return;
        }

        Popup.DeliveryDate = Popup.DeliveryDate?.Date;
        Popup.Etd = Popup.Etd?.Date;
        Popup.Eta = Popup.Eta?.Date;

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

    // ─────────────────────────────────────────────────────────────────────────────
    // Save / lifecycle
    // ─────────────────────────────────────────────────────────────────────────────

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
                ? await Qts.SaveNewAsync(request, _cts.Token)
                : IsReviseMode
                    ? await Qts.ReviseAsync(QtNo!, request, _cts.Token)
                    : await Qts.UpdateAsync(QtNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result, stayOnPage: true))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo("/sales/quotations");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnSendAsync()
    {
        var result = await RunKeyedAsync(req => Qts.SendAsync(req, _cts.Token));
        if (result)
        {
            StatusMessage = "Quotation sent.";
            await ReloadAfterLifecycleAsync();
        }
    }

    protected async Task OnAcceptAsync()
    {
        var result = await RunKeyedAsync(req => Qts.AcceptAsync(req, _cts.Token));
        if (result)
        {
            StatusMessage = "Quotation accepted.";
            await ReloadAfterLifecycleAsync();
        }
    }

    protected void OpenLosePopup()
    {
        LostReasonDraft = string.Empty;
        LoseError = null;
        LoseVisible = true;
    }

    protected async Task OnLoseAsync()
    {
        if (string.IsNullOrWhiteSpace(LostReasonDraft))
        {
            LoseError = SaQtReasonCodes.LostReasonRequired;
            return;
        }

        LoseError = null;
        var ok = await RunKeyedAsync(req => Qts.LoseAsync(req, LostReasonDraft, _cts.Token));
        if (ok)
        {
            LoseVisible = false;
            StatusMessage = "Quotation marked lost.";
            await ReloadAfterLifecycleAsync();
        }
    }

    protected async Task OnCancelDocumentAsync()
    {
        var ok = await RunKeyedAsync(req => Qts.CancelAsync(req, _cts.Token));
        if (ok)
        {
            CancelVisible = false;
            StatusMessage = "Quotation cancelled.";
            await ReloadAfterLifecycleAsync();
        }
    }

    protected async Task OnConvertAsync()
    {
        ConvertVisible = false;
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var request = new SaQtKeyedRequest
            {
                QtNo = QtNoDisplay,
                CustRel = CustRelDisplay,
                RowVersion = _rowVersion
            };
            var result = await Qts.ConvertToSoAsync(request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result, stayOnPage: true))
            {
                return;
            }

            StatusMessage = $"Converted to Sales Order {result.ConvertedSoNo}.";
            await ReloadAfterLifecycleAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>
    /// Runs a lifecycle action that only needs the document identity + RowVersion, and surfaces any
    /// failure through the shared result handler.
    /// </summary>
    private async Task<bool> RunKeyedAsync(Func<SaQtKeyedRequest, Task<SaQtOperationResult>> action)
    {
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        try
        {
            var request = new SaQtKeyedRequest
            {
                QtNo = QtNoDisplay,
                CustRel = CustRelDisplay,
                RowVersion = _rowVersion
            };
            var result = await action(request);
            if (_disposed)
            {
                return false;
            }

            return HandleOperationResult(result, stayOnPage: true);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task ReloadAfterLifecycleAsync()
    {
        var result = await Qts.GetAsync(QtNoDisplay, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload the quotation.";
            return;
        }

        ApplyDocument(result.Document);
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        _isDirty = false;
        ValidationErrors.Clear();
    }

    protected Task OnCancelAsync()
    {
        if (_isDirty && CanEditDocument)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/sales/quotations");
        return Task.CompletedTask;
    }

    protected void OnClose() => Navigation.NavigateTo("/sales/quotations");

    protected void OnEditFromView() => Navigation.NavigateTo($"/sales/quotations/edit/{QtNo}");

    protected void OnReviseFromView() => Navigation.NavigateTo($"/sales/quotations/revise/{QtNo}");

    protected void OpenConvertedSo()
    {
        if (!string.IsNullOrWhiteSpace(ConvertedSoNo))
        {
            Navigation.NavigateTo($"/sales/sales-orders/view/{ConvertedSoNo}");
        }
    }

    protected void NavigateRevisionView(short custRel) =>
        Navigation.NavigateTo($"/sales/quotations/view/{QtNoDisplay}/{custRel}");

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/sales/quotations");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || string.IsNullOrWhiteSpace(QtNo))
        {
            return;
        }

        var result = await Qts.GetAsync(QtNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload the quotation.";
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

    private bool HandleOperationResult(SaQtOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        // A previous attempt's field errors must never linger next to a different failure kind.
        if (result.ErrorKind != SaQtErrorKind.Validation)
        {
            ValidationErrors.Clear();
        }

        switch (result.ErrorKind)
        {
            case SaQtErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
                ErrorMessage = BuildValidationMessage(ValidationErrors, result.ErrorMessage);
                break;
            case SaQtErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This quotation was changed by another user.";
                break;
            case SaQtErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Quotation was not found.";
                if (!stayOnPage)
                {
                    Navigation.NavigateTo("/sales/quotations");
                }

                break;
            case SaQtErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private SaQtSaveRequest ToRequest() =>
        new()
        {
            QtDate = QtDate,
            ValidUntil = ValidUntil,
            CustCode = CustCode ?? string.Empty,
            CustPo = CustPo,
            ContactPerson = ContactPerson,
            Ref1 = Ref1,
            ProjId = ProjId,
            Currency = Currency,
            PayCode = PayCode,
            TaxGrCode = TaxGrCode,
            SalesRep = SalesRep,
            Remarks = Remarks,
            InternalRemarks = InternalRemarks,
            ShipVia = ShipVia,
            DeliveryTerms = DeliveryTerms,
            ShipName = ShipName,
            ShipAddress1 = ShipAddress1,
            ShipAddress2 = ShipAddress2,
            ShipAddress3 = ShipAddress3,
            ShipAddress4 = ShipAddress4,
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
            InvAddress4 = InvAddress4,
            InvCity = InvCity,
            InvState = InvState,
            InvPostalCode = InvPostalCode,
            InvCountry = InvCountry,
            InvTel = InvTel,
            InvFax = InvFax,
            RevisionReason = IsReviseMode ? RevisionReason : null,
            RowVersion = IsNewMode ? null : _rowVersion,
            // New docs: send Line=0 so create does not treat display row numbers as existing PKs.
            // Edit/revise docs: keep persisted Line values for update matching.
            Lines = Lines.Select(x =>
            {
                var line = x.ToRequest();
                if (IsNewMode)
                {
                    line.Line = 0;
                }

                return line;
            }).ToList()
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

    private void RefreshPackFromItem(SaQtLineVm line)
    {
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, line.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            line.StdPackSize = item.StdPackSize;
            line.StockControl = item.StockControl;
            line.StdUom = item.StdUom ?? line.StdUom;
            line.SellingUom = item.SellingUom ?? line.SellingUom;
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

public sealed class SaQtLineVm
{
    public int Line { get; set; }
    public short CustRel { get; set; } = 1;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; } = 1m;

    /// <summary>Quantity already turned into a Sales Order. Read-only on this screen.</summary>
    public decimal ConvertedQty { get; set; }

    /// <summary>Computed remainder. Display only.</summary>
    public decimal RemainingQty { get; set; }

    public decimal? StdPackSize { get; set; }
    public string? SellingUom { get; set; }
    public string? StdUom { get; set; }
    public string? Warehouse { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The engine's pricing provenance for this line. Set when the price is resolved, persisted on
    /// save, and never used to re-derive <see cref="UnitPrice"/>.
    /// </summary>
    public string? PricingSource { get; set; }

    public string? PricingRef { get; set; }

    /// <summary>Plan Phase 4: the price the engine resolved, retained only when an operator changed it.</summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>Plan Phase 4: why the resolved price was changed. Required on any override.</summary>
    public string? OverrideReason { get; set; }

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
    public string? OrderType { get; set; }
    public bool StockControl { get; set; } = true;
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public DateTime? Eta { get; set; }
    public DateTime? Etd { get; set; }

    public SaQtLineVm Clone() => new()
    {
        Line = Line,
        CustRel = CustRel,
        ICode = ICode,
        IDesc = IDesc,
        CustICode = CustICode,
        OrderQty = OrderQty,
        ConvertedQty = ConvertedQty,
        RemainingQty = RemainingQty,
        StdPackSize = StdPackSize,
        SellingUom = SellingUom,
        StdUom = StdUom,
        Warehouse = Warehouse,
        UnitPrice = UnitPrice,
        PricingSource = PricingSource,
        PricingRef = PricingRef,
        OriginalUnitPrice = OriginalUnitPrice,
        OverrideReason = OverrideReason,
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
        OrderType = OrderType,
        StockControl = StockControl,
        Classification = Classification,
        Remarks = Remarks,
        DeliveryDate = DeliveryDate,
        Eta = Eta,
        Etd = Etd
    };

    public SaQtLineRequest ToRequest() =>
        new()
        {
            Line = Line,
            ICode = ICode,
            IDesc = IDesc,
            CustICode = CustICode,
            OrderQty = OrderQty,
            Warehouse = Warehouse,
            UnitPrice = UnitPrice,
            PricingSource = PricingSource,
            PricingRef = PricingRef,
            OriginalUnitPrice = OriginalUnitPrice,
            OverrideReason = OverrideReason,
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
            OrderType = OrderType,
            Classification = Classification,
            Remarks = Remarks,
            DeliveryDate = DeliveryDate,
            Eta = Eta,
            Etd = Etd
        };

    public SaInvoiceLineCalcState ToCalcState() =>
        new()
        {
            Line = Line,
            Qty = OrderQty,
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

    public static SaQtLineVm FromDto(SaQtLineDto dto) =>
        new()
        {
            Line = dto.Line,
            CustRel = dto.CustRel,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            CustICode = dto.CustICode,
            OrderQty = dto.OrderQty,
            ConvertedQty = dto.ConvertedQty,
            RemainingQty = dto.RemainingQty,
            StdPackSize = dto.StdPsize > 0m ? dto.StdPsize : (decimal?)null,
            SellingUom = dto.SellingUom,
            StdUom = dto.StdUom,
            Warehouse = dto.Warehouse,
            UnitPrice = dto.UnitPrice,
            PricingSource = dto.PricingSource,
            PricingRef = dto.PricingRef,
            OriginalUnitPrice = dto.OriginalUnitPrice,
            OverrideReason = dto.OverrideReason,
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
            OrderType = dto.OrderType,
            StockControl = dto.StockControl,
            Classification = dto.Classification,
            Remarks = dto.Remarks,
            DeliveryDate = dto.DeliveryDate,
            Eta = dto.Eta,
            Etd = dto.Etd
        };
}
