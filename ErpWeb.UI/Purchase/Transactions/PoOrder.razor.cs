using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Purchase.Transactions;

public partial class PoOrder : PageBase, IAsyncDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? PoNo { get; set; }
    [Parameter] public int? PoRelNo { get; set; }

    [Inject] private IPoOrderService Orders { get; set; } = default!;
    [Inject] private IPoOrderAttachmentService Attachments { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;
    [Inject] private IAntiforgery Antiforgery { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsUploadingAttachment;
    protected bool PopupVisible;
    protected bool PrPickerVisible;
    protected bool ConfirmDiscardVisible;
    protected bool ConfirmSupplierChangeVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected string? PrPickerError;
    protected bool CanEditPermission;
    protected bool CanViewCost;
    protected string PoNoDisplay = "AUTO";
    protected short PoRelNoDisplay = 1;
    protected bool IsLatest = true;
    protected string StatusDisplay = PoOrderStatuses.New;
    protected DateTime? PoDate;
    protected string? Buyer;
    protected bool? OneTime;
    protected string? VendCode;
    protected string? VendName;
    protected string? VendAddress1;
    protected string? VendAddress2;
    protected string? VendAddress3;
    protected string? VendAddress4;
    protected string? VendCity;
    protected string? VendState;
    protected string? VendPostal;
    protected string? VendCountryCode;
    protected string? VendTel;
    protected string? VendFax;
    protected string? CurCode;
    protected string? TermCode;
    protected string? ContactPerson;
    protected string? Email;
    protected string? Website;
    protected string? ShipName;
    protected string? ShipAddress1;
    protected string? ShipAddress2;
    protected string? ShipAddress3;
    protected string? ShipAddress4;
    protected string? ShipCity;
    protected string? ShipState;
    protected string? ShipPostal;
    protected string? ShipCountryCode;
    protected string? ShipTel;
    protected string? ShipFax;
    protected string? TaxGrpCode;
    protected decimal Discount;
    protected string? SiRemark;
    protected string? RegNo;
    protected string? DeptCode;
    protected string? BuyingTerm;
    protected string? LocationCode;
    protected string? ProjId;
    protected string? Prefix;
    protected string? CheckBy;
    protected string? ApprovedBy;
    protected string? AuthorisedBy;
    protected string? QuatationNo;
    protected string? Ref1;
    protected string? Ref2;
    protected string? Ref3;
    protected string? Ref4;
    protected string? RevisionReason;
    protected string? TempDocId;
    protected decimal Gross;
    protected decimal Taxes;
    protected decimal Total;
    protected int ActiveTabIndex;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    protected ElementReference AttachmentInputRef;
    protected string? SelectedAttachmentName;
    protected string PrSearchText = string.Empty;

    private PoOrderLineVm? _editingLine;
    private string? _loadedKey;
    private bool _isDirty;
    private bool _disposed;
    private bool _clearLinesOnSupplierChange;
    private string? _pendingVendCode;
    private byte[] _rowVersion = [];
    private string? _antiforgeryToken;
    private IJSObjectReference? _attachmentModule;
    private CancellationTokenSource _cts = new();
    private bool _defaultInclusive;
    private int _taxDecimals = 2;
    private int? _shipToLine;
    private IReadOnlyList<PoOrderShipToLookupRow> _shipToOptions = [];

    protected List<PoOrderLineVm> Lines { get; set; } = [];
    protected List<PoOrderRevisionRow> Revisions { get; set; } = [];
    protected List<PoOrderAttachmentRow> AttachmentRows { get; set; } = [];
    protected List<PoOrderItemLookupRow> Items { get; set; } = [];
    protected List<PoOrderVendorLookupRow> Vendors { get; set; } = [];
    protected List<PoOrderTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<PoOrderCodeLookupRow> Currencies { get; set; } = [];
    protected List<PoOrderCodeLookupRow> BuyingTerms { get; set; } = [];
    protected List<PoOrderCodeLookupRow> PaymentTerms { get; set; } = [];
    protected List<PoOrderCodeLookupRow> Buyers { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<PoOrderCodeLookupRow> Departments { get; set; } = [];
    protected List<PoOrderCodeLookupRow> Projects { get; set; } = [];
    protected List<PoPrForPoRow> PrRows { get; set; } = [];
    protected List<PoPrRemainingLineVm> PrRemainingLines { get; set; } = [];
    protected IReadOnlyList<PoOrderShipToLookupRow> ShipToOptions => _shipToOptions;
    protected int? ShipToLine => _shipToLine;
    protected PoOrderLineVm Popup { get; set; } = new();

    protected IReadOnlyList<string> DiscountTypes { get; } = ["%", "AMOUNT"];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsCopyMode => string.Equals(Mode, "copy", StringComparison.OrdinalIgnoreCase);
    protected bool IsReviseMode => string.Equals(Mode, "revise", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsClosedStatus => string.Equals(StatusDisplay, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase);
    protected bool IsCancelledStatus => string.Equals(StatusDisplay, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
    protected bool IsHistoricalRevision => !IsLatest || (PoRelNo is not null && PoRelNoDisplay != 0);
    protected bool IsReadOnlyPresentation => IsViewMode || IsClosedStatus || IsCancelledStatus || IsHistoricalRevision;
    protected bool CanEditDocument =>
        (IsNewMode || IsEditMode || IsCopyMode || IsReviseMode)
        && !IsViewMode
        && !IsClosedStatus
        && !IsCancelledStatus
        && !IsHistoricalRevision;
    protected bool CanEditSupplier => CanEditDocument && !Lines.Any(x => x.IsReceived);
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && !IsClosedStatus
        && !IsCancelledStatus
        && !IsHistoricalRevision
        && !string.IsNullOrWhiteSpace(PoNo);
    protected string PageHeading =>
        IsNewMode ? "New purchase order"
        : IsEditMode ? "Edit purchase order"
        : IsCopyMode ? "Copy purchase order"
        : IsReviseMode ? "Revise purchase order"
        : "View purchase order";
    protected string ModeChip =>
        IsNewMode ? "New" : IsEditMode ? "Edit" : IsCopyMode ? "Copy" : IsReviseMode ? "Revise" : "View";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected bool CanMutateLines => CanEditDocument && !IsSubmitting && !string.IsNullOrWhiteSpace(VendCode);
    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && !string.IsNullOrWhiteSpace(VendCode)
        && !string.IsNullOrWhiteSpace(CurCode);
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit line" : "Add line";
    protected string PopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected bool PopupLockedByReceipt => _editingLine?.IsReceived == true;
    protected bool CanEditPopupIdentity => CanEditDocument && !PopupLockedByReceipt && string.IsNullOrWhiteSpace(Popup.PrNo);
    protected string? AttachmentDocId =>
        !string.IsNullOrWhiteSpace(TempDocId) ? TempDocId
        : !string.IsNullOrWhiteSpace(PoNoDisplay) && !string.Equals(PoNoDisplay, "AUTO", StringComparison.OrdinalIgnoreCase)
            ? PoNoDisplay
            : null;
    protected bool CanManageAttachments =>
        CanEditDocument && !string.IsNullOrWhiteSpace(AttachmentDocId) && !IsSubmitting;
    protected bool AttachmentUploadDisabled => !CanManageAttachments || IsUploadingAttachment;
    protected (decimal Amount, decimal TaxAmount, decimal NetAmount) PopupCalc => BuildPopupCalc();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{PoNo}:{PoRelNo}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        LoadAntiforgeryToken();
        await LoadAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        if (_attachmentModule is not null)
        {
            try
            {
                await _attachmentModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConfirmDiscardVisible = false;
        ConfirmSupplierChangeVisible = false;
        ConcurrencyVisible = false;
        PrPickerVisible = false;
        PopupVisible = false;
        _isDirty = false;
        _clearLinesOnSupplierChange = false;
        SelectedAttachmentName = null;
        AttachmentRows = [];

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Edit);
        var lookups = await Orders.GetLookupsAsync(_cts.Token);
        if (_disposed)
        {
            return;
        }

        if (lookups.Succeeded && lookups.Lookups is not null)
        {
            ApplyLookups(lookups.Lookups);
        }
        else if (!lookups.Succeeded)
        {
            ErrorMessage = lookups.ErrorMessage ?? "Unable to load lookups.";
            IsLoading = false;
            return;
        }

        if (IsNewMode)
        {
            await InitializeNewAsync();
            IsLoading = false;
            return;
        }

        if (IsCopyMode)
        {
            await InitializeCopyAsync();
            IsLoading = false;
            return;
        }

        var result = IsReviseMode
            ? await Orders.GetReviseDraftAsync(PoNo ?? string.Empty, _cts.Token)
            : IsViewMode && PoRelNo is { } revision
                ? await Orders.GetAsync(PoNo ?? string.Empty, checked((short)revision), _cts.Token)
                : await Orders.GetAsync(PoNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Purchase order was not found.";
            if (result.ErrorKind == PoOrderErrorKind.NotFound)
            {
                Navigation.NavigateTo("/purchase/orders");
            }

            IsLoading = false;
            return;
        }

        ApplyDocument(result.Document);
        TempDocId = null;
        await LoadAttachmentsAsync();
        IsLoading = false;
    }

    private async Task InitializeNewAsync()
    {
        var temp = await Orders.CreateTempDocIdAsync(_cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!temp.Succeeded || string.IsNullOrWhiteSpace(temp.TempDocId))
        {
            ErrorMessage = temp.ErrorMessage ?? "Unable to create draft attachment id.";
            return;
        }

        TempDocId = temp.TempDocId;
        PoNo = null;
        PoNoDisplay = "AUTO";
        PoRelNoDisplay = 1;
        IsLatest = true;
        StatusDisplay = PoOrderStatuses.New;
        PoDate = Dates.Today.Date;
        Buyer = CurrentUser.UserId;
        OneTime = false;
        VendCode = null;
        VendName = null;
        CurCode = Currencies.FirstOrDefault()?.Code ?? "MYR";
        TermCode = null;
        BuyingTerm = null;
        LocationCode = CurrentUser.LocationCode;
        Prefix = null;
        RevisionReason = null;
        ClearAddresses();
        Lines = [];
        Revisions = [];
        Gross = Taxes = Total = 0m;
        _rowVersion = [];
    }

    private async Task InitializeCopyAsync()
    {
        var copy = await Orders.CopyAsync(PoNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!copy.Succeeded || copy.Document is null)
        {
            ErrorMessage = copy.ErrorMessage ?? "Unable to copy purchase order.";
            return;
        }

        var temp = await Orders.CreateTempDocIdAsync(_cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!temp.Succeeded || string.IsNullOrWhiteSpace(temp.TempDocId))
        {
            ErrorMessage = temp.ErrorMessage ?? "Unable to create draft attachment id.";
            return;
        }

        ApplyDocument(copy.Document);
        TempDocId = temp.TempDocId;
        AttachmentRows = [];
        StatusMessage = $"Copied from {PoNo}";
    }

    private void ApplyLookups(PoOrderLookups lookups)
    {
        Items = lookups.DirectItems.Concat(lookups.IndirectItems)
            .OrderBy(x => x.ICode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Vendors = lookups.Vendors.ToList();
        TaxGroups = lookups.TaxGroups.ToList();
        Currencies = lookups.Currencies.ToList();
        BuyingTerms = lookups.BuyingTerms.ToList();
        PaymentTerms = lookups.PaymentTerms.ToList();
        Buyers = lookups.Buyers.ToList();
        Warehouses = lookups.Warehouses.ToList();
        Departments = lookups.Departments.ToList();
        Projects = lookups.Projects.ToList();
        CanViewCost = lookups.CanViewCost;
        _defaultInclusive = lookups.DefaultInclusive;
    }

    private void ApplyDocument(PoOrderDocument doc)
    {
        PoNo = string.IsNullOrWhiteSpace(doc.PoNo) || string.Equals(doc.PoNo, "AUTO", StringComparison.OrdinalIgnoreCase) ? PoNo : doc.PoNo;
        PoNoDisplay = string.IsNullOrWhiteSpace(doc.PoNo) ? "AUTO" : doc.PoNo;
        PoRelNoDisplay = doc.PoRelNo;
        IsLatest = doc.IsLatest;
        StatusDisplay = doc.Status;
        PoDate = doc.PoDate?.Date ?? Dates.Today.Date;
        Buyer = doc.Buyer;
        OneTime = doc.OneTime;
        VendCode = doc.VendCode;
        VendName = doc.VendName;
        VendAddress1 = doc.VendAddress1;
        VendAddress2 = doc.VendAddress2;
        VendAddress3 = doc.VendAddress3;
        VendAddress4 = doc.VendAddress4;
        VendCity = doc.VendCity;
        VendState = doc.VendState;
        VendPostal = doc.VendPostal;
        VendCountryCode = doc.VendCountryCode;
        VendTel = doc.VendTel;
        VendFax = doc.VendFax;
        CurCode = doc.CurCode;
        TermCode = doc.TermCode;
        ContactPerson = doc.ContactPerson;
        Email = doc.Email;
        Website = doc.Website;
        ShipName = doc.ShipName;
        ShipAddress1 = doc.ShipAddress1;
        ShipAddress2 = doc.ShipAddress2;
        ShipAddress3 = doc.ShipAddress3;
        ShipAddress4 = doc.ShipAddress4;
        ShipCity = doc.ShipCity;
        ShipState = doc.ShipState;
        ShipPostal = doc.ShipPostal;
        ShipCountryCode = doc.ShipCountryCode;
        ShipTel = doc.ShipTel;
        ShipFax = doc.ShipFax;
        TaxGrpCode = doc.TaxGrpCode;
        Discount = doc.Discount;
        SiRemark = doc.SiRemark;
        RegNo = doc.RegNo;
        DeptCode = doc.DeptCode;
        BuyingTerm = doc.BuyingTerm;
        LocationCode = doc.LocationCode ?? CurrentUser.LocationCode;
        ProjId = doc.ProjId;
        Prefix = doc.Prefix;
        CheckBy = doc.CheckBy;
        ApprovedBy = doc.ApprovedBy;
        AuthorisedBy = doc.AuthorisedBy;
        QuatationNo = doc.QuatationNo;
        Ref1 = doc.Ref1;
        Ref2 = doc.Ref2;
        Ref3 = doc.Ref3;
        Ref4 = doc.Ref4;
        RevisionReason = doc.RevisionReason;
        Gross = doc.Gross;
        Taxes = doc.Taxes;
        Total = doc.Total;
        _rowVersion = doc.RowVersion ?? [];
        Lines = doc.Lines.Select(PoOrderLineVm.FromDto).ToList();
        Revisions = doc.Revisions.ToList();
        _shipToLine = null;
        RecalcDocument();
    }

    protected async Task OnVendCodeChanged(string? value)
    {
        if (!CanEditSupplier)
        {
            return;
        }

        var next = Normalize(value);
        if (string.Equals(VendCode, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Lines.Count > 0 && next is not null)
        {
            _pendingVendCode = next;
            ConfirmSupplierChangeVisible = true;
            return;
        }

        if (Lines.Count > 0)
        {
            Lines.Clear();
            _clearLinesOnSupplierChange = true;
        }

        await ApplySupplierAsync(next);
    }

    protected async Task ConfirmSupplierChangeAsync()
    {
        ConfirmSupplierChangeVisible = false;
        var next = _pendingVendCode;
        _pendingVendCode = null;
        Lines.Clear();
        _clearLinesOnSupplierChange = true;
        await ApplySupplierAsync(next);
        RecalcDocument();
        MarkDirty();
    }

    protected void CancelSupplierChange()
    {
        ConfirmSupplierChangeVisible = false;
        _pendingVendCode = null;
    }

    private async Task ApplySupplierAsync(string? vendCode)
    {
        VendCode = vendCode;
        if (string.IsNullOrWhiteSpace(vendCode))
        {
            ClearSupplierFields();
            MarkDirty();
            return;
        }

        var result = await Orders.GetSupplierDefaultsAsync(vendCode, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.SupplierDefaults is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load supplier defaults.";
            MarkDirty();
            return;
        }

        ApplySupplierDefaults(result.SupplierDefaults);
        MarkDirty();
    }

    private void ApplySupplierDefaults(PoOrderSupplierDefaults d)
    {
        VendCode = d.VendCode;
        VendName = d.VendName;
        CurCode = d.CurCode ?? CurCode;
        TermCode = d.TermCode;
        BuyingTerm = d.BuyingTerm;
        TaxGrpCode = d.TaxGrpCode;
        Prefix = d.PoPrefix;
        ContactPerson = d.ContactPerson;
        Email = d.Email;
        Website = d.Website;
        RegNo = d.RegNo;
        VendAddress1 = d.VendAddress1;
        VendAddress2 = d.VendAddress2;
        VendAddress3 = d.VendAddress3;
        VendAddress4 = d.VendAddress4;
        VendCity = d.VendCity;
        VendState = d.VendState;
        VendPostal = d.VendPostal;
        VendCountryCode = d.VendCountryCode;
        VendTel = d.VendTel;
        VendFax = d.VendFax;
        _shipToOptions = d.ShipToAddresses;
    }

    private void ClearSupplierFields()
    {
        VendName = Prefix = ContactPerson = Email = Website = RegNo = null;
        VendAddress1 = VendAddress2 = VendAddress3 = VendAddress4 = null;
        VendCity = VendState = VendPostal = VendCountryCode = VendTel = VendFax = null;
        ClearShipToState();
    }

    private void ClearAddresses()
    {
        ClearSupplierFields();
        ShipName = ShipAddress1 = ShipAddress2 = ShipAddress3 = ShipAddress4 = null;
        ShipCity = ShipState = ShipPostal = ShipCountryCode = ShipTel = ShipFax = null;
    }

    private void ClearShipToState()
    {
        _shipToLine = null;
        _shipToOptions = [];
    }

    protected void OnShipToLineChanged(int? line)
    {
        if (!CanEditDocument)
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

    private void StampShipFrom(PoOrderShipToLookupRow row)
    {
        ShipName = row.Name;
        ShipAddress1 = row.Address1;
        ShipAddress2 = row.Address2;
        ShipAddress3 = row.Address3;
        ShipAddress4 = row.Address4;
        ShipCity = row.City;
        ShipState = row.State;
        ShipPostal = row.Postal;
        ShipCountryCode = row.CountryCode;
        ShipTel = row.Tel;
        ShipFax = row.Fax;
    }

    protected void OnShipFieldEdited()
    {
        _shipToLine = null;
        MarkDirty();
    }

    protected void OnNewLineClick()
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = null;
        Popup = new PoOrderLineVm
        {
            PoPurQty = 1m,
            PackSz = 1m,
            CurCode = CurCode,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? _defaultInclusive,
            ToWarehouse = Warehouses.FirstOrDefault()?.WarehouseCode,
            DiscountType = "%",
            DiscountType1 = "%"
        };
        PopupError = null;
        PopupVisible = true;
    }

    protected void EditLine(PoOrderLineVm line)
    {
        if (!CanEditLine(line))
        {
            return;
        }

        _editingLine = line;
        Popup = line.Clone();
        PopupError = null;
        PopupVisible = true;
    }

    protected void RemoveLine(PoOrderLineVm line)
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

    protected bool CanEditLine(PoOrderLineVm line) => CanMutateLines;
    protected bool CanDeleteLine(PoOrderLineVm line) => CanMutateLines && !line.IsReceived;

    protected void OnPopupItemChanged(string? iCode)
    {
        if (!CanEditPopupIdentity)
        {
            return;
        }

        Popup.ICode = iCode;
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, iCode, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        Popup.IDesc = item.IDesc;
        Popup.IType = item.IType;
        Popup.PurchaseUom = item.PurchaseUom;
        Popup.StdUom = item.StdUom;
        Popup.PackSz = item.PackSz == 0m ? 1m : item.PackSz;
        Popup.PoUnitPrice = item.UnitPrice ?? 0m;
        Popup.TaxGroup = item.TaxGroup;
        Popup.ToWarehouse = item.DefWarehouse ?? Popup.ToWarehouse;
        Popup.CurCode = CurCode;
        RecalcPopupDerived();
    }

    protected void OnPopupQtyOrPriceChanged()
    {
        RecalcPopupDerived();
    }

    protected void OnPopupTaxChanged()
    {
        RecalcPopupDerived();
    }

    protected void OnPopupSave()
    {
        if (string.IsNullOrWhiteSpace(Popup.ICode))
        {
            PopupError = "Item is required.";
            return;
        }

        if (Popup.PoPurQty <= 0m)
        {
            PopupError = "Purchase quantity must be greater than zero.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Popup.PurchaseUom))
        {
            PopupError = "Purchase UOM is required.";
            return;
        }

        if (PopupLockedByReceipt && _editingLine is not null && Popup.PoPurQty < _editingLine.PoPurQty)
        {
            PopupError = "Received line purchase quantity can only be increased.";
            return;
        }

        if (Lines.Count > 0)
        {
            var expectedInclusive = _editingLine?.IsInclusive ?? Lines[0].IsInclusive;
            if (Popup.IsInclusive != expectedInclusive)
            {
                PopupError = "All lines must use the same tax type (inclusive or exclusive).";
                return;
            }
        }

        Popup.EtaDate = Popup.EtaDate?.Date;
        Popup.CurCode = CurCode;
        RecalcPopupDerived();

        if (_editingLine is null)
        {
            Lines.Add(Popup.Clone());
        }
        else
        {
            var idx = Lines.IndexOf(_editingLine);
            if (idx >= 0)
            {
                var updated = Popup.Clone();
                updated.Line = _editingLine.Line;
                Lines[idx] = updated;
            }
        }

        Renumber();
        RecalcDocument();
        MarkDirty();
        PopupVisible = false;
    }

    protected async Task OpenPrPickerAsync()
    {
        if (!CanMutateLines)
        {
            return;
        }

        PrPickerVisible = true;
        PrPickerError = null;
        PrRemainingLines = [];
        await SearchPrAsync();
    }

    protected async Task SearchPrAsync()
    {
        PrPickerError = null;
        var result = await Orders.SearchPrForPoAsync(VendCode, PrSearchText, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.PrRows is null)
        {
            PrRows = [];
            PrPickerError = result.ErrorMessage ?? "Unable to search purchase requisitions.";
            return;
        }

        PrRows = result.PrRows.ToList();
    }

    protected async Task LoadPrLinesAsync(string prNo)
    {
        PrPickerError = null;
        var result = await Orders.GetPrRemainingLinesAsync(prNo, VendCode, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.PrRemainingLines is null)
        {
            PrRemainingLines = [];
            PrPickerError = result.ErrorMessage ?? "Unable to load PR remaining lines.";
            return;
        }

        PrRemainingLines = result.PrRemainingLines.Select(PoPrRemainingLineVm.FromDto).ToList();
        var firstVendor = PrRemainingLines.Select(x => x.VendorCd).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(VendCode) && !string.IsNullOrWhiteSpace(firstVendor))
        {
            await ApplySupplierAsync(firstVendor);
        }
    }

    protected bool CanUsePrLine(PoPrRemainingLineVm line) =>
        line.RemainingQty > 0m
        && (string.IsNullOrWhiteSpace(VendCode) || string.Equals(line.VendorCd, VendCode, StringComparison.OrdinalIgnoreCase));

    protected void AddSelectedPrLines()
    {
        var selected = PrRemainingLines.Where(x => x.Selected && CanUsePrLine(x)).ToList();
        if (selected.Count == 0)
        {
            PrPickerError = "Select at least one remaining PR line.";
            return;
        }

        foreach (var line in selected)
        {
            var vm = PoOrderLineVm.FromPrLine(line);
            vm.CurCode = CurCode ?? line.Currency;
            vm.IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? line.IsInclusive;
            RecalcLine(vm);
            Lines.Add(vm);
        }

        Renumber();
        RecalcDocument();
        MarkDirty();
        PrPickerVisible = false;
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
            var result = IsNewMode || IsCopyMode
                ? await Orders.SaveNewAsync(request, _cts.Token)
                : IsReviseMode
                    ? await Orders.ReviseAsync(PoNo!, request, _cts.Token)
                    : await Orders.UpdateAsync(PoNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo("/purchase/orders");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnCancelAsync()
    {
        if (_isDirty && CanEditDocument)
        {
            ConfirmDiscardVisible = true;
            return;
        }

        await DiscardDraftIfNeededAsync();
        Navigation.NavigateTo("/purchase/orders");
    }

    protected void OnClose() => Navigation.NavigateTo("/purchase/orders");
    protected void OnEditFromView() => Navigation.NavigateTo($"/purchase/orders/edit/{PoNo}");
    protected void NavigateRevisionView(short poRelNo) =>
        Navigation.NavigateTo($"/purchase/orders/view/{PoNoDisplay}/{poRelNo}");

    protected async Task ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        await DiscardDraftIfNeededAsync();
        Navigation.NavigateTo("/purchase/orders");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || IsCopyMode || string.IsNullOrWhiteSpace(PoNo))
        {
            return;
        }

        var result = await Orders.GetAsync(PoNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload purchase order.";
            return;
        }

        ApplyDocument(result.Document);
        await LoadAttachmentsAsync();
        _isDirty = false;
        ValidationErrors.Clear();
        StatusMessage = "Loaded latest version.";
    }

    protected async Task OnBrowseAttachmentAsync()
    {
        if (AttachmentUploadDisabled)
        {
            return;
        }

        var module = await GetAttachmentModuleAsync();
        await module.InvokeVoidAsync("openFilePicker", AttachmentInputRef);
    }

    protected Task OnAttachmentInputChanged(ChangeEventArgs args)
    {
        SelectedAttachmentName = ExtractFileName(args.Value?.ToString());
        return Task.CompletedTask;
    }

    protected async Task OnUploadAttachmentAsync()
    {
        if (AttachmentUploadDisabled || string.IsNullOrWhiteSpace(AttachmentDocId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_antiforgeryToken))
        {
            ErrorMessage = "Unable to upload attachment because the antiforgery token is unavailable.";
            return;
        }

        IsUploadingAttachment = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var module = await GetAttachmentModuleAsync();
            var result = await module.InvokeAsync<AttachmentUploadResult>(
                "uploadPoAttachmentFromInput",
                AttachmentInputRef,
                AttachmentDocId,
                _antiforgeryToken);

            if (result.Ok)
            {
                await LoadAttachmentsAsync();
                SelectedAttachmentName = null;
                StatusMessage = result.Message ?? "Attachment uploaded.";
            }
            else
            {
                ErrorMessage = result.Message ?? $"Upload failed ({result.Status}).";
            }
        }
        finally
        {
            IsUploadingAttachment = false;
        }
    }

    protected async Task OnDeleteAttachmentAsync(PoOrderAttachmentRow row)
    {
        if (!CanManageAttachments || string.IsNullOrWhiteSpace(AttachmentDocId))
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        var result = await Attachments.DeleteAsync(AttachmentDocId, row.DocName, _cts.Token);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Message ?? "Unable to delete attachment.";
            return;
        }

        await LoadAttachmentsAsync();
        StatusMessage = $"Deleted attachment {row.DocName}.";
    }

    protected string BuildAttachmentDownloadUrl(string docName) =>
        $"/purchase/orders/attachments/{Uri.EscapeDataString(AttachmentDocId ?? string.Empty)}/file?docName={Uri.EscapeDataString(docName ?? string.Empty)}";

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;
    protected void MarkDirtyOnly() => MarkDirty();

    private async Task LoadAttachmentsAsync()
    {
        if (string.IsNullOrWhiteSpace(AttachmentDocId))
        {
            AttachmentRows = [];
            return;
        }

        var result = await Attachments.ListAsync(AttachmentDocId, _cts.Token);
        if (_disposed)
        {
            return;
        }

        AttachmentRows = result.Succeeded && result.Data is not null
            ? result.Data.ToList()
            : [];
    }

    private async Task DiscardDraftIfNeededAsync()
    {
        if (string.IsNullOrWhiteSpace(TempDocId))
        {
            return;
        }

        try
        {
            await Attachments.DiscardDraftAsync(TempDocId, CancellationToken.None);
        }
        catch
        {
            // Best-effort draft cleanup.
        }
    }

    private bool HandleOperationResult(PoOrderOperationResult result)
    {
        if (result.Succeeded)
        {
            return true;
        }

        switch (result.ErrorKind)
        {
            case PoOrderErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
                var firstDetail = ValidationErrors.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                ErrorMessage = !string.IsNullOrWhiteSpace(firstDetail)
                    ? firstDetail
                    : (result.ErrorMessage ?? "Validation failed.");
                break;
            case PoOrderErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This purchase order was changed by another user.";
                break;
            case PoOrderErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Purchase order was not found.";
                Navigation.NavigateTo("/purchase/orders");
                break;
            case PoOrderErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private PoOrderSaveRequest ToRequest()
    {
        var isCreate = IsNewMode || IsCopyMode;
        return new PoOrderSaveRequest
        {
            PoDate = PoDate,
            Buyer = Buyer,
            OneTime = OneTime,
            VendCode = VendCode,
            VendName = VendName,
            VendAddress1 = VendAddress1,
            VendAddress2 = VendAddress2,
            VendAddress3 = VendAddress3,
            VendAddress4 = VendAddress4,
            VendCity = VendCity,
            VendState = VendState,
            VendPostal = VendPostal,
            VendCountryCode = VendCountryCode,
            VendTel = VendTel,
            VendFax = VendFax,
            CurCode = CurCode,
            TermCode = TermCode,
            ContactPerson = ContactPerson,
            Email = Email,
            Website = Website,
            ShipName = ShipName,
            ShipAddress1 = ShipAddress1,
            ShipAddress2 = ShipAddress2,
            ShipAddress3 = ShipAddress3,
            ShipAddress4 = ShipAddress4,
            ShipCity = ShipCity,
            ShipState = ShipState,
            ShipPostal = ShipPostal,
            ShipCountryCode = ShipCountryCode,
            ShipTel = ShipTel,
            ShipFax = ShipFax,
            TaxGrpCode = TaxGrpCode,
            Discount = Discount,
            SiRemark = SiRemark,
            RegNo = RegNo,
            DeptCode = DeptCode,
            BuyingTerm = BuyingTerm,
            ProjId = ProjId,
            CheckBy = CheckBy,
            ApprovedBy = ApprovedBy,
            AuthorisedBy = AuthorisedBy,
            QuatationNo = QuatationNo,
            Ref1 = Ref1,
            Ref2 = Ref2,
            Ref3 = Ref3,
            Ref4 = Ref4,
            RevisionReason = IsReviseMode ? RevisionReason : null,
            ClearLinesOnSupplierChange = _clearLinesOnSupplierChange,
            TempDocId = TempDocId,
            PoRelNo = IsReviseMode ? PoRelNoDisplay : null,
            RowVersion = isCreate ? null : _rowVersion,
            Lines = Lines.Select(x =>
            {
                var dto = x.ToDto();
                if (isCreate)
                {
                    dto.Line = 0;
                }

                return dto;
            }).ToList()
        };
    }

    private void RecalcDocument()
    {
        if (Lines.Count == 0)
        {
            Gross = Taxes = Total = 0m;
            return;
        }

        foreach (var line in Lines)
        {
            RecalcLine(line);
        }

        var totals = PoOrderCalc.SumTotals(Lines.Select(x => (x.NetAmount, x.TaxAmount)));
        Gross = totals.Gross;
        Taxes = totals.Taxes;
        Total = totals.Total;
    }

    private void RecalcPopupDerived() => RecalcLine(Popup);

    private void RecalcLine(PoOrderLineVm line)
    {
        line.PackSz = PoOrderCalc.EffectivePackSize(line.PackSz);
        line.PoQty = PoOrderCalc.ComputeStdQty(line.PoPurQty, line.PackSz);
        var amount = PoOrderCalc.ComputeAmount(line.PoPurQty, line.PoUnitPrice);
        line.Amount = amount;
        var discounted = PoOrderCalc.ApplyTwoLevelDiscount(
            amount,
            line.ItemDiscount,
            line.DiscountType,
            line.ItemDiscount1,
            line.DiscountType1);
        var taxPercent = ResolveTaxPercent(line.TaxGroup);
        var (net, tax) = PoOrderCalc.ComputeTax(discounted, taxPercent, line.IsInclusive, _taxDecimals);
        line.NetAmount = net;
        line.TaxAmount = tax;
        line.BalanceQty = PoOrderCalc.ComputeBalance(line.PoPurQty, line.RecvQty, line.ReturnQty);
        line.OverRecvQty = PoOrderCalc.ComputeOverRecv(line.PoPurQty, line.RecvQty, line.ReturnQty);
        line.InvoiceableQty = PoOrderCalc.ComputeInvoiceable(line.RecvQty, line.ReturnQty, line.InvoicedQty);
        line.OverInvoicedQty = PoOrderCalc.ComputeOverInvoiced(line.RecvQty, line.ReturnQty, line.InvoicedQty);
    }

    private (decimal Amount, decimal TaxAmount, decimal NetAmount) BuildPopupCalc()
    {
        var amount = PoOrderCalc.ComputeAmount(Popup.PoPurQty, Popup.PoUnitPrice);
        var discounted = PoOrderCalc.ApplyTwoLevelDiscount(
            amount,
            Popup.ItemDiscount,
            Popup.DiscountType,
            Popup.ItemDiscount1,
            Popup.DiscountType1);
        var taxPercent = ResolveTaxPercent(Popup.TaxGroup);
        var (net, tax) = PoOrderCalc.ComputeTax(discounted, taxPercent, Popup.IsInclusive, _taxDecimals);
        return (discounted, tax, net);
    }

    private decimal ResolveTaxPercent(string? taxGroup)
    {
        var code = string.IsNullOrWhiteSpace(taxGroup) ? TaxGrpCode : taxGroup;
        if (string.IsNullOrWhiteSpace(code))
        {
            return 0m;
        }

        var match = TaxGroups.FirstOrDefault(x =>
            string.Equals(x.TaxGrCode, code, StringComparison.OrdinalIgnoreCase));
        return match?.Percentage ?? 0m;
    }

    private void Renumber()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].Line = (short)(i + 1);
        }
    }

    private void MarkDirty()
    {
        if (CanEditDocument)
        {
            _isDirty = true;
        }
    }

    private void LoadAntiforgeryToken()
    {
        var httpContext = HttpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        _antiforgeryToken = Antiforgery.GetAndStoreTokens(httpContext).RequestToken;
    }

    private async Task<IJSObjectReference> GetAttachmentModuleAsync()
    {
        _attachmentModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "/js/po-attach.js");
        return _attachmentModule;
    }

    private static string? ExtractFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var separators = new[] { '\\', '/' };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    private sealed class AttachmentUploadResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string? Message { get; set; }
    }
}

public sealed class PoOrderLineVm
{
    public Guid UiKey { get; set; } = Guid.NewGuid();
    public short Line { get; set; }
    public string? PrNo { get; set; }
    public short? PrLineNo { get; set; }
    public bool? OneTime { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? IType { get; set; }
    public decimal PoUnitPrice { get; set; }
    public decimal PoQty { get; set; }
    public decimal PoPurQty { get; set; }
    public decimal WtQty { get; set; }
    public decimal Amount { get; set; }
    public decimal RecvQty { get; set; }
    public decimal ReturnQty { get; set; }
    public decimal BalanceQty { get; set; }
    public decimal OverRecvQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal InvoiceableQty { get; set; }
    public decimal OverInvoicedQty { get; set; }
    public decimal PackSz { get; set; } = 1m;
    public string? StdUom { get; set; }
    public string? WtUom { get; set; }
    public string? PurchaseUom { get; set; }
    public DateTime? EtaDate { get; set; }
    public string? CurCode { get; set; }
    public string? Remarks { get; set; }
    public string? PoDesc { get; set; }
    public DateTime? RecvDate { get; set; }
    public string? RepairType { get; set; }
    public decimal Discount { get; set; }
    public decimal ItemDiscount { get; set; }
    public string? DiscountType { get; set; } = "%";
    public decimal NetAmount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? DiscountType1 { get; set; } = "%";
    public string? CjNo { get; set; }
    public int? CjRelNo { get; set; }
    public int? CjLine { get; set; }
    public string? ProjId { get; set; }
    public string? VendorPartNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Requester { get; set; }
    public bool IsReceived => RecvQty > 0m;
    public bool HasPartialInvoice => InvoicedQty > 0m && InvoiceableQty > 0m;
    public string PrReference => string.IsNullOrWhiteSpace(PrNo) ? string.Empty : $"{PrNo}/{PrLineNo}";

    public static PoOrderLineVm FromDto(PoOrderLineDto dto) =>
        new()
        {
            UiKey = Guid.NewGuid(),
            Line = dto.Line,
            PrNo = dto.PrNo,
            PrLineNo = dto.PrLineNo,
            OneTime = dto.OneTime,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            IType = dto.IType,
            PoUnitPrice = dto.PoUnitPrice,
            PoQty = dto.PoQty,
            PoPurQty = dto.PoPurQty,
            WtQty = dto.WtQty,
            Amount = dto.Amount,
            RecvQty = dto.RecvQty,
            ReturnQty = dto.ReturnQty,
            BalanceQty = dto.BalanceQty,
            OverRecvQty = dto.OverRecvQty,
            InvoicedQty = dto.InvoicedQty,
            InvoiceableQty = PoOrderCalc.ComputeInvoiceable(dto.RecvQty, dto.ReturnQty, dto.InvoicedQty),
            OverInvoicedQty = PoOrderCalc.ComputeOverInvoiced(dto.RecvQty, dto.ReturnQty, dto.InvoicedQty),
            PackSz = dto.PackSz == 0m ? 1m : dto.PackSz,
            StdUom = dto.StdUom,
            WtUom = dto.WtUom,
            PurchaseUom = dto.PurchaseUom,
            EtaDate = dto.EtaDate,
            CurCode = dto.CurCode,
            Remarks = dto.Remarks,
            PoDesc = dto.PoDesc,
            RecvDate = dto.RecvDate,
            RepairType = dto.RepairType,
            Discount = dto.Discount,
            ItemDiscount = dto.ItemDiscount,
            DiscountType = string.IsNullOrWhiteSpace(dto.DiscountType) ? "%" : dto.DiscountType,
            NetAmount = dto.NetAmount,
            ItemDiscount1 = dto.ItemDiscount1,
            DiscountType1 = string.IsNullOrWhiteSpace(dto.DiscountType1) ? "%" : dto.DiscountType1,
            CjNo = dto.CjNo,
            CjRelNo = dto.CjRelNo,
            CjLine = dto.CjLine,
            ProjId = dto.ProjId,
            VendorPartNo = dto.VendorPartNo,
            TaxGroup = dto.TaxGroup,
            TaxAmount = dto.TaxAmount,
            IsInclusive = dto.IsInclusive,
            ToWarehouse = dto.ToWarehouse,
            Requester = dto.Requester
        };

    public static PoOrderLineVm FromPrLine(PoPrRemainingLineVm dto) =>
        new()
        {
            UiKey = Guid.NewGuid(),
            PrNo = dto.PrNo,
            PrLineNo = dto.Line,
            OneTime = dto.OneTimeItemYn,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            PoUnitPrice = dto.UnitPrice,
            PoPurQty = dto.RemainingQty,
            PackSz = dto.PackSz == 0m ? 1m : dto.PackSz,
            StdUom = dto.StdUom,
            PurchaseUom = dto.PurchaseUom,
            EtaDate = dto.EtaDt,
            CurCode = dto.Currency,
            Remarks = dto.Purpose,
            PoDesc = dto.Purpose,
            TaxGroup = dto.TaxGroup,
            IsInclusive = dto.IsInclusive,
            ToWarehouse = dto.ToWarehouse,
            DiscountType = "%",
            DiscountType1 = "%"
        };

    public PoOrderLineDto ToDto() =>
        new()
        {
            Line = Line,
            PrNo = PrNo,
            PrLineNo = PrLineNo,
            OneTime = OneTime,
            ICode = ICode,
            IDesc = IDesc,
            IType = IType,
            PoUnitPrice = PoUnitPrice,
            PoQty = PoQty,
            PoPurQty = PoPurQty,
            WtQty = WtQty,
            Amount = Amount,
            RecvQty = RecvQty,
            ReturnQty = ReturnQty,
            BalanceQty = BalanceQty,
            OverRecvQty = OverRecvQty,
            InvoicedQty = InvoicedQty,
            PackSz = PackSz,
            StdUom = StdUom,
            WtUom = WtUom,
            PurchaseUom = PurchaseUom,
            EtaDate = EtaDate,
            CurCode = CurCode,
            Remarks = Remarks,
            PoDesc = PoDesc,
            RecvDate = RecvDate,
            RepairType = RepairType,
            Discount = Discount,
            ItemDiscount = ItemDiscount,
            DiscountType = DiscountType,
            NetAmount = NetAmount,
            ItemDiscount1 = ItemDiscount1,
            DiscountType1 = DiscountType1,
            CjNo = CjNo,
            CjRelNo = CjRelNo,
            CjLine = CjLine,
            ProjId = ProjId,
            VendorPartNo = VendorPartNo,
            TaxGroup = TaxGroup,
            TaxAmount = TaxAmount,
            IsInclusive = IsInclusive,
            ToWarehouse = ToWarehouse,
            Requester = Requester
        };

    public PoOrderLineVm Clone()
    {
        var clone = FromDto(ToDto());
        clone.UiKey = UiKey;
        return clone;
    }
}

public sealed class PoPrRemainingLineVm
{
    public Guid UiKey { get; set; } = Guid.NewGuid();
    public bool Selected { get; set; }
    public string PrNo { get; init; } = string.Empty;
    public short Line { get; init; }
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public decimal PurchaseQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? PurchaseUom { get; init; }
    public decimal PackSz { get; init; }
    public string? StdUom { get; init; }
    public decimal UnitPrice { get; init; }
    public string? Currency { get; init; }
    public string? VendorCd { get; init; }
    public string? VendNm { get; init; }
    public string? TaxGroup { get; init; }
    public bool IsInclusive { get; init; }
    public string? ToWarehouse { get; init; }
    public DateTime? EtaDt { get; init; }
    public string? Purpose { get; init; }
    public bool? OneTimeItemYn { get; init; }

    public static PoPrRemainingLineVm FromDto(PoPrRemainingLineDto dto) =>
        new()
        {
            PrNo = dto.PrNo,
            Line = dto.Line,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            PurchaseQty = dto.PurchaseQty,
            RemainingQty = dto.RemainingQty,
            PurchaseUom = dto.PurchaseUom,
            PackSz = dto.PackSz,
            StdUom = dto.StdUom,
            UnitPrice = dto.UnitPrice,
            Currency = dto.Currency,
            VendorCd = dto.VendorCd,
            VendNm = dto.VendNm,
            TaxGroup = dto.TaxGroup,
            IsInclusive = dto.IsInclusive,
            ToWarehouse = dto.ToWarehouse,
            EtaDt = dto.EtaDt,
            Purpose = dto.Purpose,
            OneTimeItemYn = dto.OneTimeItemYn
        };
}
