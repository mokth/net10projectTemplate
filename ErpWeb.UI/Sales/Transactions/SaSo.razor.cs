using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaSo : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? SoNo { get; set; }
    [Parameter] public int? CustRel { get; set; }

    [Inject] private ISaSoService Sos { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private ISaSalesRefService SalesRefService { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmCustChangeVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected bool CanEditPermission;
    protected string SoNoDisplay = "AUTO";
    protected short CustRelDisplay = 1;
    protected short LastCustRelDisplay = 1;
    protected bool IsCurrentDocument = true;
    protected string StatusDisplay = SaSoStatuses.New;
    protected string FulfillmentStatusDisplay = SaDualStatuses.None;
    protected string BillingStatusDisplay = SaDualStatuses.None;
    protected DateTime SoDate;
    protected string? CustCode;
    protected string? CustName;
    protected string? Prefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? TaxGrCode;
    protected string? SalesRep;
    protected string? PayCode;
    protected string? CustPo;
    protected string? Ref1;
    protected string? ProjId;
    protected string? Remarks;
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

    private SaSoLineVm? _editingLine;
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

    /// <summary>Sequence guard for price resolution; a slow first response must not win over a newer one.</summary>
    private int _priceApplySeq;

    /// <summary>
    /// Non-null when the engine refused to price the line. While it is set the popup Save is blocked,
    /// because the alternative — the legacy <c>?? 0m</c> seed — silently sells the item for RM 0.00.
    /// </summary>
    private string? _priceBlockMessage;

    /// <summary>Operator-facing provenance of the resolved price, e.g. "Customer item price (MOQ=100)".</summary>
    private string? _priceHint;

    /// <summary>
    /// The discount slots the ENGINE last wrote. Used to tell an untouched auto-assigned discount from
    /// one the user has since edited, so re-resolving never clobbers a manual entry.
    /// </summary>
    private (decimal P1, decimal P2, decimal A1, decimal A2)? _autoDiscountSlots;
    private int? _shipToLine;
    private IReadOnlyList<SaCustAddressVm> _shipToOptions = [];
    private CancellationTokenSource _cts = new();

    protected List<SaSoLineVm> Lines { get; set; } = [];
    protected List<SaSoRevisionHistoryRow> Revisions { get; set; } = [];
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
    protected SaSoLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsReviseMode => string.Equals(Mode, "revise", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsShippedStatus => string.Equals(StatusDisplay, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase);
    protected bool IsClosedStatus => string.Equals(StatusDisplay, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase);
    protected bool IsSupersededStatus => string.Equals(StatusDisplay, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase);
    protected bool IsHistoricalRevision => !IsCurrentDocument || IsSupersededStatus;
    protected bool IsReadOnlyPresentation => IsViewMode || IsClosedStatus;
    protected bool CanEditDocument => (IsNewMode || IsEditMode || IsReviseMode) && !IsViewMode && !IsClosedStatus && !IsHistoricalRevision;

    /// <summary>
    /// Phase 4: may this user move a line price away from the price the engine resolved? Without it the
    /// price control renders READ-ONLY. The server enforces the same rule, so this only governs what
    /// the operator can attempt, never what is accepted.
    /// </summary>
    protected bool CanOverridePrice { get; set; }

    protected bool CanEditCustomer => CanEditDocument && !IsShippedStatus;
    protected bool CanEditAddresses => CanEditDocument && !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && !IsClosedStatus
        && !IsHistoricalRevision
        && !string.IsNullOrWhiteSpace(SoNo);
    protected string PageHeading => IsNewMode ? "New sales order" : IsEditMode ? "Edit sales order" : IsReviseMode ? "Revise sales order" : "View sales order";
    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : IsReviseMode ? "Revise" : "View";
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
        && !string.IsNullOrWhiteSpace(CustPo)
        && (!TaxGroupRequired || !string.IsNullOrWhiteSpace(TaxGrCode));
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit line" : "Add line";
    protected string PopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected decimal PopupAllocatedFloor =>
        _editingLine is null
            ? 0m
            : Math.Max(
                _editingLine.DeliveredQty,
                Math.Max(_editingLine.InvoicedQty, _editingLine.ShippedQty));
    protected bool PopupInclusiveLocked =>
        Lines.Count > 1 || (_editingLine is null && Lines.Count > 0);
    protected SaInvoiceLineCalcState PopupCalc => BuildPopupCalc();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{SoNo}:{CustRel}";
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
        ConcurrencyVisible = false;
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.Edit);
        // Phase 4: without this the price control is read-only. The SERVER enforces the same rule, so
        // this only governs what the operator can attempt, not what is accepted.
        CanOverridePrice = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.PriceOverride);
        var lookups = await Sos.GetLookupsAsync(_cts.Token);
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

        var result = IsReviseMode
            ? await Sos.GetReviseDraftAsync(SoNo ?? string.Empty, _cts.Token)
            : IsViewMode && CustRel is { } revision
                ? await Sos.GetAsync(SoNo ?? string.Empty, checked((short)revision), _cts.Token)
                : await Sos.GetAsync(SoNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Sales order was not found.";
            if (result.ErrorKind == SaSoErrorKind.NotFound)
            {
                Navigation.NavigateTo("/sales/sales-orders");
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
        SoNo = null;
        SoNoDisplay = "AUTO";
        CustRelDisplay = 1;
        LastCustRelDisplay = 1;
        IsCurrentDocument = true;
        StatusDisplay = SaSoStatuses.New;
        FulfillmentStatusDisplay = SaDualStatuses.None;
        BillingStatusDisplay = SaDualStatuses.None;
        SoDate = Dates.Today.Date;
        CustCode = null;
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

    private void ApplyDocument(SaSoDocument doc)
    {
        SoNo = doc.SoNo;
        SoNoDisplay = doc.SoNo;
        CustRelDisplay = doc.CustRel;
        LastCustRelDisplay = doc.LastCustRel;
        IsCurrentDocument = doc.IsCurrent;
        SoDate = doc.SoDate;
        StatusDisplay = doc.Status;
        FulfillmentStatusDisplay = string.IsNullOrWhiteSpace(doc.FulfillmentStatus)
            ? SaDualStatuses.None
            : doc.FulfillmentStatus;
        BillingStatusDisplay = string.IsNullOrWhiteSpace(doc.BillingStatus)
            ? SaDualStatuses.None
            : doc.BillingStatus;
        CustCode = doc.CustCode;
        CustName = doc.CustName;
        Prefix = doc.Prefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = doc.CurrRate > 0m;
        TaxGrCode = doc.TaxGrCode;
        SalesRep = doc.SalesRep;
        PayCode = doc.PayCode;
        CustPo = doc.CustPo;
        Ref1 = doc.Ref1;
        ProjId = doc.ProjId;
        Remarks = doc.Remarks;
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
        Lines = doc.Lines.Select(SaSoLineVm.FromDto).ToList();
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

        var result = await Sos.GetCustomerDefaultsAsync(code, SoDate, _cts.Token);
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

    protected async Task OnSoDateChanged(DateTime newDate)
    {
        if (!CanEditDocument)
        {
            return;
        }

        SoDate = newDate.Date;
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

        var result = await Sos.ResolveCurrencyRateAsync(Currency, SoDate, _cts.Token);
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
        Popup = new SaSoLineVm
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

    protected void EditLine(SaSoLineVm line)
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = line;
        Popup = line.Clone();

        // Phase 4: an EXISTING line has no stored engine baseline unless it was ALREADY overridden, so
        // adopt the price the document already carried as the baseline. Without this, changing the price
        // on a reopened line would not be an override at all — no reason demanded, nothing recorded.
        // `??=` keeps a real prior engine price when one exists, instead of overwriting it with the
        // overridden value and losing the original.
        Popup.OriginalUnitPrice ??= line.UnitPrice;
        RefreshPackFromItem(Popup);
        PopupDiscountIsAmount = Popup.ItemDiscAmount != 0m || Popup.ItemDiscAmount1 != 0m;
        PopupError = null;
        _priceBlockMessage = null;
        _priceHint = null;

        // Re-opening a DRAFT line deliberately does NOT re-price it: the price is re-resolved only when
        // a pricing input actually changes, so editing an unrelated field can never silently move the
        // line to today's price. The stored slots become the baseline for that later comparison.
        _autoDiscountSlots = (Popup.ItemDiscount, Popup.ItemDiscount2, Popup.ItemDiscAmount, Popup.ItemDiscAmount1);
        PopupVisible = true;
    }

    protected void RemoveLine(SaSoLineVm line)
    {
        if (!CanDeleteLine(line))
        {
            return;
        }

        Lines.Remove(line);
        Renumber();
        RecalcDocument();
        MarkDirty();
    }

    protected bool CanDeleteLine(SaSoLineVm line) =>
        CanMutateLines
        && !(IsShippedStatus && line.ShippedQty > 0m);

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

        // A new item means new discount rules: drop the previous auto-assignment so the engine may
        // repopulate the slots for THIS item.
        _autoDiscountSlots = null;
        Popup.ItemDiscount = Popup.ItemDiscount2 = Popup.ItemDiscount3 = 0m;
        Popup.ItemDiscount4 = Popup.ItemDiscount5 = Popup.ItemDiscount6 = 0m;
        Popup.ItemDiscAmount = Popup.ItemDiscAmount1 = 0m;
        PopupDiscountIsAmount = false;

        // NOTE: UnitPrice is deliberately NOT seeded from item.SellingPrice here. The server engine
        // decides it, and a missing price must block rather than become RM 0.00.
        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    protected async Task OnPopupQuantityChangedAsync(decimal qty)
    {
        Popup.OrderQty = qty;
        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    /// <summary>
    /// Toggling the basis changes the STORED price, so the price is re-resolved. The discount slots are
    /// left untouched: the user may have edited them, and the document engine recomputes the effective
    /// discount from the slots on every recalculation anyway.
    /// </summary>
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
    /// The single call site for line pricing. Stages 1-3 run server-side; stage 4 stays in
    /// <see cref="RecalcDocument"/>. Every trigger (item, quantity, basis, tax) funnels through here.
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
                DocDate = SoDate,
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
            // A blocked line must not keep a stale provenance from an earlier successful resolve, and
            // it must not keep a stale override baseline either — there is nothing left to compare to.
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
        // Phase 4: a fresh resolution REBASES the override. The engine has just produced an
        // authoritative price, so any override recorded against the previous one is meaningless.
        Popup.OriginalUnitPrice = priced.UnitPrice;
        Popup.OverrideReason = null;
        Popup.PricingSource = priced.PricingSourceToken;
        Popup.PricingRef = priced.PricingRef;
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

        // Clear a previous engine block, but never a user-facing validation message.
        if (!string.IsNullOrWhiteSpace(_priceBlockMessage) && PopupError == _priceBlockMessage)
        {
            PopupError = null;
        }

        _priceBlockMessage = null;
    }

    /// <summary>True when the discount fields still hold exactly what the engine last assigned.</summary>
    private bool DiscountSlotsAreUntouched() =>
        _autoDiscountSlots is not { } last
        || (Popup.ItemDiscount == last.P1
            && Popup.ItemDiscount2 == last.P2
            && Popup.ItemDiscAmount == last.A1
            && Popup.ItemDiscAmount1 == last.A2);

    /// <summary>
    /// Phase 4: has the operator moved the price away from what the engine resolved? A line with no
    /// recorded engine price is NOT an override — there is nothing to compare against, and inventing a
    /// requirement there would block ordinary entry (an older client, or a line typed without a resolve).
    /// </summary>
    private bool IsPriceOverridden() =>
        Popup.OriginalUnitPrice is { } resolved && resolved != Popup.UnitPrice;

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

        // Phase 4: a price moved away from the resolved one is an override and needs a reason. Checked
        // here for a clear message; the SERVER enforces the same rule and the permission.
        if (IsPriceOverridden() && string.IsNullOrWhiteSpace(Popup.OverrideReason))
        {
            PopupError = SaPriceOverridePolicy.ReasonRequiredMessage;
            return;
        }

        if (_editingLine is not null && Popup.OrderQty < PopupAllocatedFloor)
        {
            PopupError = $"Order quantity cannot be lower than allocated quantity {PopupAllocatedFloor:n4}.";
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
                ? await Sos.SaveNewAsync(request, _cts.Token)
                : IsReviseMode
                    ? await Sos.ReviseAsync(SoNo!, request, _cts.Token)
                    : await Sos.UpdateAsync(SoNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result, stayOnPage: true))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo("/sales/sales-orders");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (_isDirty && CanEditDocument)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/sales/sales-orders");
        return Task.CompletedTask;
    }

    protected void OnClose() => Navigation.NavigateTo("/sales/sales-orders");

    protected void OnEditFromView() => Navigation.NavigateTo($"/sales/sales-orders/edit/{SoNo}");

    protected void NavigateRevisionView(short custRel) =>
        Navigation.NavigateTo($"/sales/sales-orders/view/{SoNoDisplay}/{custRel}");

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/sales/sales-orders");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || string.IsNullOrWhiteSpace(SoNo))
        {
            return;
        }

        var result = await Sos.GetAsync(SoNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload sales order.";
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

    private bool HandleOperationResult(SaSoOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        // A previous attempt's field errors must never linger next to a different failure kind.
        if (result.ErrorKind != SaSoErrorKind.Validation)
        {
            ValidationErrors.Clear();
        }

        switch (result.ErrorKind)
        {
            case SaSoErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
                ErrorMessage = BuildValidationMessage(ValidationErrors, result.ErrorMessage);
                break;
            case SaSoErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This sales order was changed by another user.";
                break;
            case SaSoErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Sales order was not found.";
                if (!stayOnPage)
                {
                    Navigation.NavigateTo("/sales/sales-orders");
                }

                break;
            case SaSoErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private SaSoSaveRequest ToRequest() =>
        new()
        {
            SoDate = SoDate,
            CustCode = CustCode ?? string.Empty,
            CustPo = CustPo,
            Ref1 = Ref1,
            ProjId = ProjId,
            Currency = Currency,
            PayCode = PayCode,
            TaxGrCode = TaxGrCode,
            SalesRep = SalesRep,
            Remarks = Remarks,
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
            // Edit docs: keep persisted Line values for update matching.
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

    private void RefreshPackFromItem(SaSoLineVm line)
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

public sealed class SaSoLineVm
{
    public int Line { get; set; }
    public short CustRel { get; set; } = 1;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? CustICode { get; set; }
    public decimal OrderQty { get; set; } = 1m;
    public decimal ShippedQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    /// <summary>R3: written off by a DO force-close.</summary>
    public decimal WrittenOffQty { get; set; }
    public decimal? StdPackSize { get; set; }
    public string? SellingUom { get; set; }
    public string? StdUom { get; set; }
    public string? Warehouse { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The engine's pricing provenance for this line (plan Phase 2). Set when the price is resolved,
    /// persisted on save, and never used to re-derive <see cref="UnitPrice"/>.
    /// </summary>
    public string? PricingSource { get; set; }

    public string? PricingRef { get; set; }

    /// <summary>
    /// Phase 4: the price the ENGINE resolved, i.e. what <see cref="UnitPrice"/> was immediately after
    /// resolution. If the saved <see cref="UnitPrice"/> differs, that is an OVERRIDE and the server
    /// refuses it without <c>PRICE_OVERRIDE</c> and a reason. NULL means "no override declared".
    /// </summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>Phase 4: why an operator moved the price away from the resolved one.</summary>
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

    public SaSoLineVm Clone() => new()
    {
        Line = Line,
        CustRel = CustRel,
        ICode = ICode,
        IDesc = IDesc,
        CustICode = CustICode,
        OrderQty = OrderQty,
        ShippedQty = ShippedQty,
        BalanceQty = BalanceQty,
        DeliveredQty = DeliveredQty,
        InvoicedQty = InvoicedQty,
        WrittenOffQty = WrittenOffQty,
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

    public SaSoLineRequest ToRequest() =>
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

    public static SaSoLineVm FromDto(SaSoLineDto dto) =>
        new()
        {
            Line = dto.Line,
            CustRel = dto.CustRel,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            CustICode = dto.CustICode,
            OrderQty = dto.OrderQty,
            ShippedQty = dto.ShippedQty,
            BalanceQty = dto.BalanceQty,
            DeliveredQty = dto.DeliveredQty,
            InvoicedQty = dto.InvoicedQty,
            WrittenOffQty = dto.WrittenOffQty,
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
