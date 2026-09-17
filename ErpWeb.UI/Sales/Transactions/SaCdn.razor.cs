using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaCdn : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? DocNo { get; set; }

    [Inject] private ISaCdnService Cdns { get; set; } = default!;
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
    protected bool InvoicePickerVisible;
    protected bool InvoicePickerLoading;
    protected string? PopupError;
    protected string? InvoicePickerError;
    protected bool CanEditPermission;
    protected string DocNoDisplay = "AUTO";
    protected string StatusDisplay = "NEW";
    protected DateTime DocDate;
    protected string? CustCode;
    protected string? CustName;
    protected string? Prefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? PayCode;
    protected string? TaxGrCode;
    protected string? SalesmanCode;
    protected string? InvNo;
    protected string? DoNo;
    /// <summary>
    /// R9: "Reserved by draft CN(s) …" indicator. Read-only/advisory — the authoritative gate is
    /// still CDN_REMAINING on save and post.
    /// </summary>
    protected string? ReservationIndicator;
    protected bool ReturnStock;
    protected string? RefNo;
    protected string? ExternalDocNo;
    protected string? Dept;
    protected string? ProjId;
    protected string? Remarks;
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
    protected decimal GrossAmnt;
    protected decimal Taxes;
    protected decimal TotAmnt;
    protected int ActiveTabIndex;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    protected List<SaCdnInvoicePickerRow> InvoicePickerRows { get; set; } = [];

    private SaCdnLineVm? _editingLine;
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
    /// because the alternative — the legacy <c>?? 0m</c> seed — silently credits the item at RM 0.00.
    /// </summary>
    private string? _priceBlockMessage;

    /// <summary>Operator-facing provenance of the resolved price, e.g. "Customer item price (MOQ=100)".</summary>
    private string? _priceHint;

    /// <summary>
    /// The discount slots the ENGINE last wrote, so re-resolving never clobbers a manual entry.
    /// </summary>
    private (decimal P1, decimal P2, decimal A1, decimal A2)? _autoDiscountSlots;
    private CancellationTokenSource _cts = new();
    private string _type = "CN";

    protected List<SaCdnLineVm> Lines { get; set; } = [];
    protected List<SaCdnCustomerLookupRow> Customers { get; set; } = [];
    protected List<SaCdnItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<SaCdnTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<IvCodeLookupRow> Departments { get; set; } = [];
    protected List<IvCodeLookupRow> Projects { get; set; } = [];
    protected List<IvCodeLookupRow> SalesReps { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected SaCdnLineVm Popup { get; set; } = new();
    protected bool PopupDiscountIsAmount { get; set; }

    // ── Derived properties ───────────────────────────────────────────────────

    protected bool IsCreditNote => string.Equals(_type, "CN", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// New/Edit always show Source. View shows it only when lineage must be communicated.
    /// </summary>
    protected bool ShowSourceCard =>
        !IsViewMode
        || (IsCreditNote
            ? !string.IsNullOrWhiteSpace(InvNo) || !string.IsNullOrWhiteSpace(DoNo) || ReturnStock
            : !string.IsNullOrWhiteSpace(DoNo));

    protected string MenuCode =>
        IsCreditNote ? MenuCodes.SalesCreditNote : MenuCodes.SalesDebitNote;

    /// <summary>ERP document family for the e-Invoice façade and audit log: CN or DN.</summary>
    protected string DocumentTypeCode =>
        IsCreditNote ? EInvoiceDocumentTypes.CreditNote : EInvoiceDocumentTypes.DebitNote;

    /// <summary>
    /// e-Invoice actions are only offered for a saved, POSTED document with no pending edits. This
    /// is a convenience gate only - <c>SaEInvoiceService</c> re-checks every rule server-side.
    /// </summary>
    protected bool CanUseEInvoice =>
        !IsNewMode
        && !string.IsNullOrWhiteSpace(DocNo)
        && !_isDirty
        && !IsSubmitting
        && string.Equals(StatusDisplay, SaCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True while the e-Invoice status is SUBMITTING, SUBMITTED or VALID: the payload must not change
    /// under a submission. <c>SaCdnService</c> rejects the same edits server-side - this flag only stops
    /// the operator from typing changes that would be refused on save.
    /// </summary>
    protected bool EInvoiceLocked { get; private set; }

    /// <summary>
    /// The panel reports the status it just read: the page only needs to re-render and, when the
    /// document is now locked, stop offering structural edits.
    /// </summary>
    private Task OnEInvoiceStateChangedAsync(string? status)
    {
        EInvoiceLocked = EInvoiceStatuses.IsLocked(status);
        return InvokeAsync(StateHasChanged);
    }


    protected string HeroIcon =>
        IsCreditNote ? "fa-solid fa-file-minus" : "fa-solid fa-file-plus";

    protected string ListRoute =>
        IsCreditNote ? "/sales/credit-notes" : "/sales/debit-notes";

    protected string EditRouteFor(string docNo) =>
        IsCreditNote ? $"/sales/credit-notes/edit/{docNo}" : $"/sales/debit-notes/edit/{docNo}";

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode && !EInvoiceLocked;

    /// <summary>
    /// Phase 4: may this user move a line price away from the price the engine resolved? Without it the
    /// price control renders READ-ONLY. The server enforces the same rule.
    /// </summary>
    protected bool CanOverridePrice { get; set; }
    protected bool CanEditAddresses =>
        CanEditDocument
        && string.Equals(StatusDisplay, "NEW", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CustCode);
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && !EInvoiceLocked
        && string.Equals(StatusDisplay, "NEW", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(DocNo);

    protected string PageHeading =>
        IsNewMode
            ? $"New {(IsCreditNote ? "credit note" : "debit note")}"
            : IsEditMode
                ? $"Edit {(IsCreditNote ? "credit note" : "debit note")}"
                : $"View {(IsCreditNote ? "credit note" : "debit note")}";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
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
        && !string.IsNullOrWhiteSpace(SalesmanCode);
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
        var key = $"{Mode}:{DocNo}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void ResolveType()
    {
        _type = Navigation.Uri.Contains("/debit-notes", StringComparison.OrdinalIgnoreCase) ? "DN" : "CN";
    }

    private async Task LoadAsync()
    {
        ResolveType();
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConfirmDiscardVisible = false;
        ConfirmCustChangeVisible = false;
        ConcurrencyVisible = false;
        InvoicePickerVisible = false;
        _isDirty = false;
        _pendingCustCode = null;
        PopupVisible = false;

        CanEditPermission = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        // Phase 4: governs what the operator can ATTEMPT; the server enforces what is accepted.
        CanOverridePrice = await AccessRights.CanAsync(MenuCode, PermissionCodes.PriceOverride);
        var lookups = await Cdns.GetLookupsAsync(_type, _cts.Token);
        if (_disposed) return;

        if (lookups.Succeeded)
        {
            Customers = lookups.Customers.ToList();
            Items = lookups.Items.ToList();
            Warehouses = lookups.Warehouses.ToList();
            TaxGroups = lookups.TaxGroups.ToList();
            PayCodes = lookups.PayCodes.ToList();
            Departments = lookups.Departments.ToList();
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

        if (_disposed) return;

        if (IsNewMode)
        {
            ResetNewDocument();
            IsLoading = false;
            return;
        }

        var result = await Cdns.GetAsync(DocNo ?? string.Empty, _cts.Token);
        if (_disposed) return;

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Document was not found.";
            if (result.ErrorKind == SaCdnErrorKind.NotFound)
            {
                Navigation.NavigateTo(ListRoute);
            }

            IsLoading = false;
            return;
        }

        ApplyDocument(result.Document);
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        await RefreshReservationIndicatorAsync();
        IsLoading = false;
    }

    private void ResetNewDocument()
    {
        DocNo = null;
        DocNoDisplay = "AUTO";
        StatusDisplay = "NEW";
        DocDate = Dates.Today.Date;
        CustCode = null;
        CustName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        PayCode = null;
        TaxGrCode = null;
        SalesmanCode = null;
        InvNo = null;
        DoNo = null;
        ReservationIndicator = null;
        ReturnStock = false;
        RefNo = null;
        ExternalDocNo = null;
        Dept = null;
        ProjId = null;
        Remarks = null;
        ClearAddresses();
        Lines = [];
        GrossAmnt = 0;
        Taxes = 0;
        TotAmnt = 0;
        _rowVersion = [];
        _discountMethod = null;
        _decPoint = false;
        _taxable = null;
    }

    private void ClearAddresses()
    {
        InvName = InvAddress1 = InvAddress2 = InvAddress3 = null;
        InvCity = InvState = InvPostalCode = InvCountry = InvTel = InvFax = null;
    }

    private void WipeAllCustomerDependentFields()
    {
        CustName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = false;
        PayCode = null;
        TaxGrCode = null;
        SalesmanCode = null;
        Remarks = null;
        ClearAddresses();
        _taxable = null;
        _discountMethod = null;
        _decPoint = false;
    }

    private static string NormCustCode(string? custCode) => (custCode ?? string.Empty).Trim();

    private void ApplyDocument(SaCdnDocument doc)
    {
        DocNo = doc.DocNo;
        DocNoDisplay = doc.DocNo;
        DocDate = doc.DocDate;
        StatusDisplay = doc.Status;
        _type = doc.Type;
        CustCode = doc.CustCode;
        CustName = doc.CustName;
        Prefix = doc.Prefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = doc.CurrRate > 0m;
        PayCode = doc.PayCode;
        TaxGrCode = doc.TaxGrCode;
        SalesmanCode = doc.SalesmanCode;
        InvNo = doc.InvNo;
        DoNo = doc.DoNo;
        ReservationIndicator = null;
        ReturnStock = doc.ReturnStock;
        RefNo = doc.RefNo;
        ExternalDocNo = doc.ExternalDocNo;
        Dept = doc.Dept;
        ProjId = doc.ProjId;
        Remarks = doc.Remarks;
        InvName = doc.CustName;
        InvAddress1 = doc.InvAddress1;
        InvAddress2 = doc.InvAddress2;
        InvAddress3 = doc.InvAddress3;
        InvCity = doc.InvCity;
        InvState = doc.InvState;
        InvPostalCode = doc.InvPostalCode;
        InvCountry = doc.InvCountry;
        InvTel = doc.InvTel;
        InvFax = doc.InvFax;
        GrossAmnt = doc.GrossAmnt;
        Taxes = doc.Taxes;
        TotAmnt = doc.TotAmnt;
        _rowVersion = doc.RowVersion;
        Lines = doc.Lines.Select(SaCdnLineVm.FromDto).ToList();
    }

    /// <summary>R9: refresh the draft-CN reservation indicator for the linked invoice.</summary>
    protected async Task OnInvNoChangedAsync()
    {
        MarkDirty();
        await RefreshReservationIndicatorAsync();
    }

    private async Task RefreshReservationIndicatorAsync()
    {
        ReservationIndicator = null;
        var no = (InvNo ?? string.Empty).Trim();
        if (!IsCreditNote || no.Length == 0)
        {
            return;
        }

        try
        {
            var result = await Cdns.GetInvoiceReservationsAsync(no, IsNewMode ? null : DocNo, _cts.Token);
            if (!result.Succeeded || result.Reservations is not { } summary || !summary.HasDraftReservation)
            {
                return;
            }

            ReservationIndicator = $"{summary.DraftIndicator} Remaining {summary.Remaining:n2}.";
        }
        catch (OperationCanceledException)
        {
            // navigation / re-entry — the indicator is advisory only.
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

            return;
        }

        var result = await Cdns.GetCustomerDefaultsAsync(code, DocDate, _cts.Token);
        if (seq != _customerApplySeq || _disposed) return;

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

        if (!addressApply) return;
        if (seq != _customerApplySeq) return;

        ApplyDefaults(d);
        Remarks = null;
    }

    protected async Task OnCustCodeChanged(string? value)
    {
        if (_isApplyingDefaults || _disposed || !CanEditDocument) return;

        var next = NormCustCode(value);
        next = string.IsNullOrEmpty(next) ? null : next;
        if (string.Equals(CustCode, next, StringComparison.OrdinalIgnoreCase)) return;

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

    private void ApplyDefaults(SaCdnCustomerDefaults d)
    {
        CustCode = d.CustCode;
        CustName = d.CustName;
        Prefix = null;
        Currency = d.Currency ?? "MYR";
        CurrRate = d.CurrRate;
        CurrRateValid = d.CurrRateValid;
        PayCode = d.PayCode;
        TaxGrCode = d.TaxGrCode;
        SalesmanCode = d.SalesmanCode;
        _taxable = d.Taxable;
        _discountMethod = d.DiscountMethod;
        _decPoint = d.DecPoint == true;

        InvAddress1 = d.InvAddress1;
        InvAddress2 = d.InvAddress2;
        InvAddress3 = d.InvAddress3;
        InvCity = d.InvCity;
        InvState = d.InvState;
        InvPostalCode = d.InvPostalCode;
        InvCountry = d.InvCountry;
        InvTel = d.InvTel;
        InvFax = d.InvFax;

        // InvName comes from InvName field on customer defaults
        InvName = d.InvName;
    }

    protected async Task OnDocDateChanged(DateTime newDate)
    {
        if (!CanEditDocument) return;

        DocDate = newDate.Date;
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

        var result = await Cdns.ResolveCurrencyRateAsync(Currency, DocDate, _cts.Token);
        if (_disposed) return;

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
        if (!CanMutateLines) return;

        _editingLine = null;
        Popup = new SaCdnLineVm
        {
            FrWarehouse = Warehouses.FirstOrDefault()?.WarehouseCode,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? false
        };
        PopupDiscountIsAmount = false;
        PopupError = null;
        _priceBlockMessage = null;
        _priceHint = null;
        _autoDiscountSlots = null;
        PopupVisible = true;
    }

    protected void EditLine(SaCdnLineVm line)
    {
        if (!CanMutateLines) return;

        _editingLine = line;
        Popup = line.Clone();

        // Phase 4: as SaSo — adopt the loaded price as the override baseline when no engine price was ever
        // recorded, so a manual change on a reopened line is still governed. `??=` preserves a real
        // prior engine price when one exists.
        Popup.OriginalUnitPrice ??= line.UnitPrice;
        PopupDiscountIsAmount = Popup.ItemDiscAmount != 0m;
        PopupError = null;
        _priceBlockMessage = null;
        _priceHint = null;

        // Re-opening a draft line deliberately does NOT re-price it: the price is re-resolved only when
        // a pricing input actually changes, so editing an unrelated field can never silently move the
        // line to today's price. The stored slots become the baseline for that later comparison.
        _autoDiscountSlots = (Popup.ItemDiscount, Popup.ItemDiscount2, Popup.ItemDiscAmount, 0m);
        PopupVisible = true;
    }

    protected void RemoveLine(SaCdnLineVm line)
    {
        if (!CanMutateLines) return;

        Lines.Remove(line);
        Renumber();
        RecalcDocument();
        MarkDirty();
    }

    protected async Task OnPopupItemChangedAsync(string? iCode)
    {
        Popup.ICode = iCode ?? string.Empty;
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, Popup.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        Popup.IDesc = item.IDesc;
        Popup.StdUom = item.StdUom;
        Popup.StockControl = item.StockControl;
        if (!string.IsNullOrWhiteSpace(item.TaxGroup)
            && TaxGroups.Any(x => string.Equals(x.TaxGrCode, item.TaxGroup, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.TaxGrCode = item.TaxGroup;
        }

        if (string.IsNullOrWhiteSpace(Popup.FrWarehouse))
        {
            Popup.FrWarehouse = item.DefWarehouse ?? Warehouses.FirstOrDefault()?.WarehouseCode;
        }

        // A new item means new discount rules.
        _autoDiscountSlots = null;
        Popup.ItemDiscount = Popup.ItemDiscount2 = Popup.ItemDiscount3 = 0m;
        Popup.ItemDiscount4 = Popup.ItemDiscount5 = Popup.ItemDiscount6 = 0m;
        Popup.ItemDiscAmount = 0m;
        PopupDiscountIsAmount = false;

        // UnitPrice is deliberately NOT seeded from item.SellingPrice: the server engine decides it,
        // and a missing price must BLOCK rather than become RM 0.00.
        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    protected async Task OnPopupQuantityChangedAsync(decimal qty)
    {
        Popup.Qty = qty;
        await ResolvePopupPriceAsync(assignDiscountSlots: true);
    }

    /// <summary>
    /// Toggling the basis changes the STORED price, so the price is re-resolved. The discount slots are
    /// left untouched: the document engine recomputes the effective discount from the slots anyway.
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
    /// RecalcDocument. Every trigger funnels through here, with a sequence guard so a slow first
    /// response cannot overwrite a newer one.
    /// </summary>
    private async Task ResolvePopupPriceAsync(bool assignDiscountSlots)
    {
        if (string.IsNullOrWhiteSpace(Popup.ICode) || string.IsNullOrWhiteSpace(CustCode))
        {
            return;
        }

        var seq = Interlocked.Increment(ref _priceApplySeq);

        var result = await SalesRefService.ResolveLinePricingAsync(
            new SaLinePricingRequest
            {
                CustCode = CustCode!,
                ICode = Popup.ICode,
                UOM = Popup.StdUom ?? string.Empty,
                Qty = Popup.Qty,
                DocDate = DocDate,
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
            // it must not keep a stale override baseline either - there is nothing left to compare to.
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
        // Phase 4: a fresh resolution REBASES the override - the engine has just produced an
        // authoritative price, so an override against the previous one is meaningless.
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
            PopupDiscountIsAmount = priced.ItemDiscAmount != 0m;
            _autoDiscountSlots = (priced.ItemDiscount, priced.ItemDiscount2, priced.ItemDiscAmount, priced.ItemDiscAmount1);
        }

        if (!string.IsNullOrWhiteSpace(_priceBlockMessage) && PopupError == _priceBlockMessage)
        {
            PopupError = null;
        }

        _priceBlockMessage = null;
    }

    /// <summary>
    /// Phase 4: has the operator moved the price away from what the engine resolved? A line with no
    /// recorded engine price is NOT an override - there is nothing to compare against.
    /// </summary>
    private bool IsPriceOverridden() =>
        Popup.OriginalUnitPrice is { } resolved && resolved != Popup.UnitPrice;

    /// <summary>True when the discount fields still hold exactly what the engine last assigned.</summary>
    private bool DiscountSlotsAreUntouched() =>
        _autoDiscountSlots is not { } last
        || (Popup.ItemDiscount == last.P1
            && Popup.ItemDiscount2 == last.P2
            && Popup.ItemDiscAmount == last.A1
            && last.A2 == 0m);

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

        // The engine refused to price this line. Saving it would persist a price the pricing rules
        // never produced (the legacy defect was RM 0.00).
        if (!string.IsNullOrWhiteSpace(_priceBlockMessage))
        {
            PopupError = _priceBlockMessage;
            return;
        }

        // Phase 4: a price moved away from the resolved one is an override and needs a reason. Checked
        // here for a clear message; the SERVER enforces the same rule AND the permission.
        if (IsPriceOverridden() && string.IsNullOrWhiteSpace(Popup.OverrideReason))
        {
            PopupError = SaPriceOverridePolicy.ReasonRequiredMessage;
            return;
        }

        if (Lines.Count > 0)
        {
            var expected = _editingLine?.IsInclusive ?? Lines[0].IsInclusive;
            if (Popup.IsInclusive != expected)
            {
                PopupError = "All lines must use the same tax type (inclusive or exclusive).";
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
        if (!CanSave) return;

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        try
        {
            var request = ToRequest();
            var result = IsNewMode
                ? await Cdns.SaveNewAsync(request, _cts.Token)
                : await Cdns.UpdateAsync(DocNo!, request, _cts.Token);
            if (_disposed) return;

            if (!HandleOperationResult(result, stayOnPage: true)) return;

            _isDirty = false;
            Navigation.NavigateTo(ListRoute);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (_isDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo(ListRoute);
        return Task.CompletedTask;
    }

    protected void OnClose() => Navigation.NavigateTo(ListRoute);

    protected void OnEditFromView() => Navigation.NavigateTo(EditRouteFor(DocNo!));

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        Navigation.NavigateTo(ListRoute);
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || string.IsNullOrWhiteSpace(DocNo)) return;

        var result = await Cdns.GetAsync(DocNo, _cts.Token);
        if (_disposed) return;

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload document.";
            return;
        }

        ApplyDocument(result.Document);
        await ApplyCustomerDefaultsAsync(result.Document.CustCode, addressApply: false, seq: _customerApplySeq);
        RecalcDocument();
        await RefreshReservationIndicatorAsync();
        _isDirty = false;
        ValidationErrors.Clear();
        StatusMessage = "Loaded latest version.";
    }

    // ── Invoice picker ────────────────────────────────────────────────────────

    protected async Task OpenInvoicePickerAsync()
    {
        if (!HasCustomer) return;

        InvoicePickerVisible = true;
        InvoicePickerLoading = true;
        InvoicePickerError = null;
        InvoicePickerRows = [];
        try
        {
            var result = await Cdns.SearchPostedInvoicesAsync(CustCode, null, _cts.Token);
            if (_disposed) return;

            if (!result.Succeeded)
            {
                InvoicePickerError = result.ErrorMessage ?? "Unable to load invoices.";
                return;
            }

            InvoicePickerRows = result.InvoicePickerRows.ToList();
            if (InvoicePickerRows.Count == 0)
            {
                InvoicePickerError = "No posted invoices found for this customer.";
            }
        }
        finally
        {
            InvoicePickerLoading = false;
        }
    }

    protected async Task OnInvoiceRowDoubleClick(GridRowClickEventArgs args)
    {
        if (args.Grid.GetDataItem(args.VisibleIndex) is SaCdnInvoicePickerRow row)
        {
            await OnInvoicePickerSelected(row);
        }
    }

    protected async Task OnInvoicePickerSelected(SaCdnInvoicePickerRow row)
    {

        InvoicePickerVisible = false;
        InvoicePickerLoading = true;
        try
        {
            var result = await Cdns.CopyFromInvoiceAsync(row.InvNo, _cts.Token);
            if (_disposed) return;

            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to copy from invoice.";
                return;
            }

            // Apply invoice data to the form (do not save)
            var doc = result.Document;
            InvNo = doc.InvNo;
            DoNo = doc.DoNo;
            Lines = doc.Lines.Select(SaCdnLineVm.FromDto).ToList();
            Renumber();
            RecalcDocument();
            MarkDirty();
            StatusMessage = $"Copied {Lines.Count} line(s) from invoice {row.InvNo}. Review and save to confirm.";
        }
        finally
        {
            InvoicePickerLoading = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private bool HandleOperationResult(SaCdnOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded) return true;

        // A previous attempt's field errors must never linger next to a different failure kind.
        if (result.ErrorKind != SaCdnErrorKind.Validation)
        {
            ValidationErrors.Clear();
        }

        switch (result.ErrorKind)
        {
            case SaCdnErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                ErrorMessage = BuildValidationMessage(ValidationErrors, result.ErrorMessage);
                break;
            case SaCdnErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This document was changed by another user.";
                break;
            case SaCdnErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Document was not found.";
                if (!stayOnPage) Navigation.NavigateTo(ListRoute);
                break;
            case SaCdnErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private SaCdnSaveRequest ToRequest() =>
        new()
        {
            Type = _type,
            DocDate = DocDate,
            CustCode = CustCode ?? string.Empty,
            InvNo = IsCreditNote ? InvNo : null,
            DoNo = DoNo,
            ReturnStock = IsCreditNote && ReturnStock,
            Currency = Currency,
            CurrRate = CurrRate,
            PayCode = PayCode,
            TaxGrCode = TaxGrCode,
            SalesmanCode = SalesmanCode,
            Dept = Dept,
            ProjId = ProjId,
            Remarks = Remarks,
            RefNo = RefNo,
            ExternalDocNo = ExternalDocNo,
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
        if (string.IsNullOrWhiteSpace(code)) return 0m;
        var match = TaxGroups.FirstOrDefault(x =>
            string.Equals(x.TaxGrCode, code, StringComparison.OrdinalIgnoreCase));
        return match?.Percentage ?? 0m;
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
        if (CanEditDocument) _isDirty = true;
    }

    protected void MarkDirtyOnly() => MarkDirty();
}

// ── Line ViewModel ─────────────────────────────────────────────────────────

public sealed class SaCdnLineVm
{
    public int Line { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public decimal Qty { get; set; } = 1m;
    public string? StdUom { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>The engine's pricing provenance for this line (plan Phase 2); stored, never re-derived.</summary>
    public string? PricingSource { get; set; }

    public string? PricingRef { get; set; }

    /// <summary>
    /// Phase 4: the price the ENGINE resolved; a different saved <see cref="UnitPrice"/> is an OVERRIDE
    /// and the server refuses it without <c>PRICE_OVERRIDE</c> and a reason.
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
    public bool IsInclusive { get; set; }
    public string? TaxGrCode { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal NetAmount { get; set; }
    public bool StockControl { get; set; } = true;
    public string? ItemGlCode { get; set; }
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    // Return-stock fields
    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public SaCdnLineVm Clone() => new()
    {
        Line = Line,
        ICode = ICode,
        IDesc = IDesc,
        Qty = Qty,
        StdUom = StdUom,
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
        IsInclusive = IsInclusive,
        TaxGrCode = TaxGrCode,
        Amount = Amount,
        TaxAmt = TaxAmt,
        NetAmount = NetAmount,
        StockControl = StockControl,
        ItemGlCode = ItemGlCode,
        Classification = Classification,
        Remarks = Remarks,
        FrWarehouse = FrWarehouse,
        LocCode = LocCode,
        IStatus = IStatus,
        LotNo = LotNo,
        ExpiryDate = ExpiryDate
    };

    public SaCdnLineRequest ToRequest() => new()
    {
        ICode = ICode,
        IDesc = IDesc,
        Qty = Qty,
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
        IsInclusive = IsInclusive,
        TaxGrCode = TaxGrCode,
        Classification = Classification,
        Remarks = Remarks,
        ItemGlCode = ItemGlCode,
        FrWarehouse = FrWarehouse,
        LocCode = LocCode,
        IStatus = IStatus,
        LotNo = LotNo,
        ExpiryDate = ExpiryDate
    };

    public SaInvoiceLineCalcState ToCalcState() => new()
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
        IsInclusive = IsInclusive
    };

    public static SaCdnLineVm FromDto(SaCdnLineDto dto) => new()
    {
        Line = dto.Line,
        ICode = dto.ICode,
        IDesc = dto.IDesc,
        Qty = dto.Qty,
        StdUom = dto.StdUom,
        UnitPrice = dto.UnitPrice,
        PricingSource = dto.PricingSource,
        PricingRef = dto.PricingRef,
        // Loading an EXISTING credit/debit note must preserve its override record, or an unrelated edit
        // would silently erase it.
        OriginalUnitPrice = dto.OriginalUnitPrice,
        OverrideReason = dto.OverrideReason,
        ItemDiscount = dto.ItemDiscount,
        ItemDiscount2 = dto.ItemDiscount2,
        ItemDiscount3 = dto.ItemDiscount3,
        ItemDiscount4 = dto.ItemDiscount4,
        ItemDiscount5 = dto.ItemDiscount5,
        ItemDiscount6 = dto.ItemDiscount6,
        ItemDiscAmount = dto.ItemDiscAmount,
        IsInclusive = dto.IsInclusive,
        TaxGrCode = dto.TaxGrCode,
        Amount = dto.NetAmount + dto.TaxAmt,
        TaxAmt = dto.TaxAmt,
        NetAmount = dto.NetAmount,
        StockControl = dto.StockControl,
        ItemGlCode = dto.ItemGlCode,
        Classification = dto.Classification,
        Remarks = dto.Remarks,
        FrWarehouse = dto.FrWarehouse,
        LocCode = dto.LocCode,
        IStatus = dto.IStatus,
        LotNo = dto.LotNo,
        ExpiryDate = dto.ExpiryDate
    };
}
