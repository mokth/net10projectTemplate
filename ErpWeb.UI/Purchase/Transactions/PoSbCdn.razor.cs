using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Self-billed purchase credit / debit note entry (LHDN 12 / 13). The route decides the type; the type
/// decides the e-Invoice family, the menu the rights are checked against and the numbering module.
///
/// <para>
/// The parties are reversed exactly as for the self-billed invoice: the VENDOR is the payload's
/// <c>Supplier</c> and our COMPANY (the issuer) is its <c>Customer</c>.
/// </para>
///
/// <para>
/// A note must name the self-billed invoice it adjusts. The origin is chosen from the VALID self-billed
/// invoices OF THE SAME VENDOR (a note against another vendor's invoice would describe a different trading
/// relationship); the server re-validates it and the submission additionally requires it to be VALID at
/// MyInvois with a UUID.
/// </para>
/// </summary>
public partial class PoSbCdn : PageBase
{
    [Inject] private IPoSbCdnService Notes { get; set; } = default!;
    [Inject] private IPoSbInvoiceService SbInvoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    private string _type = "CN"; // resolved from the route

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;

    protected PoSbCdnVm Model { get; set; } = new();
    protected List<LineEdit> EditLines { get; } = [];
    protected PoSbLookups Lookups { get; set; } = new();
    protected List<PoSbInvoiceListRow> OriginOptions { get; private set; } = [];
    protected IReadOnlyDictionary<string, string> ValidationErrors { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    protected bool CanAdd;
    protected bool CanEdit;

    // ── Line editor ──────────────────────────────────────────────────────────

    protected LineEdit Popup { get; set; } = new();
    protected bool PopupVisible;
    protected int PopupIndex = -1;
    protected string PopupTitle => PopupIndex < 0 ? "Add line" : "Edit line";

    // ── Mode / type helpers ──────────────────────────────────────────────────

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);

    private bool IsDebitNote => string.Equals(_type, "DN", StringComparison.OrdinalIgnoreCase);

    protected string DocumentTypeName => IsDebitNote ? "Self-billed debit note" : "Self-billed credit note";

    protected string MenuCode => IsDebitNote ? MenuCodes.PurchaseSbDebitNote : MenuCodes.PurchaseSbCreditNote;

    private string BaseRoute => IsDebitNote
        ? "/purchase/self-billed-debit-notes"
        : "/purchase/self-billed-credit-notes";

    /// <summary>The e-Invoice family this document is submitted as.</summary>
    protected string EInvoiceDocumentType => IsDebitNote
        ? EInvoiceDocumentTypes.SelfBilledDebitNote
        : EInvoiceDocumentTypes.SelfBilledCreditNote;

    /// <summary>The LHDN code the payload will carry, for the title bar.</summary>
    private string LhdnTypeLabel => IsDebitNote ? "SBD (LHDN 13)" : "SBC (LHDN 12)";

    protected bool IsEditable => !IsView && !Model.IsEInvoiceLocked;

    protected bool CanMutateLines => IsEditable && !IsSubmitting;

    /// <summary>A note needs a vendor, an origin and at least one line.</summary>
    protected bool CanSave =>
        !string.IsNullOrWhiteSpace(Model.VendorCode)
        && !string.IsNullOrWhiteSpace(Model.OriginSbInvNo)
        && EditLines.Count > 0;

    protected bool CanUseEInvoice => !IsNew && !string.IsNullOrWhiteSpace(Model.DocNo) && !IsSubmitting;

    protected string PageHeading => IsNew ? $"New {DocumentTypeName}" : DocumentTypeName;

    protected string DocNoDisplay => string.IsNullOrWhiteSpace(Model.DocNo) ? "(auto)" : Model.DocNo;

    protected string StatusDisplay => string.IsNullOrWhiteSpace(Model.Status) ? "NEW" : Model.Status;

    protected string EInvoiceDisplay =>
        string.IsNullOrWhiteSpace(Model.IrbmStatus) ? "NEW" : Model.IrbmStatus;

    protected string LineCountLabel =>
        EditLines.Count == 1 ? "1 line" : $"{EditLines.Count:N0} lines";

    protected decimal GrossTotal => EditLines.Sum(x => x.NetAmount);
    protected decimal TaxTotal => EditLines.Sum(x => x.TaxAmt);
    protected decimal GrandTotal => GrossTotal + TaxTotal;

    protected IReadOnlyList<PoSbVendorLookupRow> Vendors => Lookups.Vendors;
    protected IReadOnlyList<PoSbTaxGroupLookupRow> TaxGroups => Lookups.TaxGroups;
    protected IReadOnlyList<PoSbCodeLookupRow> Currencies => Lookups.Currencies;
    protected IReadOnlyList<PoSbCodeLookupRow> Uoms => Lookups.Uoms;

    protected override async Task OnPageInitializedAsync()
    {
        ResolveType();

        CanAdd = await AccessRights.CanAsync(MenuCode, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);

        await LoadLookupsAsync();
        await ReloadAsync();
        IsLoading = false;
    }

    private void ResolveType() =>
        _type = Navigation.Uri.Contains("/self-billed-debit-notes", StringComparison.OrdinalIgnoreCase)
            ? "DN"
            : "CN";

    private async Task LoadLookupsAsync()
    {
        var result = await Notes.GetLookupsAsync(_type);
        if (result.Succeeded && result.Lookups is not null)
        {
            Lookups = result.Lookups;
        }
        else
        {
            ErrorMessage = result.Message;
        }
    }

    protected async Task ReloadAsync()
    {
        if (IsNew)
        {
            Model = new PoSbCdnVm { DocDate = DateTime.Today, Type = _type };
            EditLines.Clear();
            return;
        }

        var result = await Notes.GetAsync(DocNo ?? string.Empty);
        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.Message;
            return;
        }

        var document = result.Document;
        Model = new PoSbCdnVm
        {
            DocNo = document.DocNo,
            DocDate = document.DocDate,
            Status = document.Status,
            Type = document.Type,
            Prefix = document.Prefix,
            VendorCode = document.VendorCode,
            VendorName = document.VendorName,
            OriginSbInvNo = document.OriginSbInvNo,
            Currency = document.Currency,
            CurrRate = document.CurrRate,
            TaxGrCode = document.TaxGrCode,
            Remarks = document.Remarks,
            RowVersion = document.RowVersion,
            IrbmStatus = document.IrbmStatus,
            IrbmUuid = document.IrbmUuid,
            IrbmOriUuid = document.IrbmOriUuid,
            IrbmError = document.IrbmError,
            IsEInvoiceLocked = document.IsEInvoiceLocked
        };

        EditLines.Clear();
        foreach (var line in document.Lines)
        {
            EditLines.Add(LineEdit.FromDto(line));
        }

        await RefreshOriginOptionsAsync();
    }

    /// <summary>
    /// Self-billed invoices of the selected vendor that a note may reference. The filter is the SAME rule
    /// the payload applies (<see cref="PoSbOriginResolver.ResolveValidAsync"/>): the origin must be VALID
    /// at MyInvois. The ERP status is irrelevant — the NEW/POSTED dimension is retired — and the list is
    /// filtered by vendor so the operator cannot accidentally adjust another supplier's document.
    /// </summary>
    private async Task RefreshOriginOptionsAsync()
    {
        if (string.IsNullOrWhiteSpace(Model.VendorCode))
        {
            OriginOptions = [];
            return;
        }

        var result = await SbInvoices.SearchAsync(new PoSbQuery
        {
            VendorCode = Model.VendorCode,
            IrbmStatus = EInvoiceStatuses.Valid,
            Take = 100,
            SortDescending = true
        });

        OriginOptions = result.Succeeded && result.List is not null
            ? result.List.Rows.ToList()
            : [];
    }

    // ── Header events ────────────────────────────────────────────────────────

    protected async Task OnVendorChangedAsync(string? vendorCode)
    {
        Model.VendorCode = vendorCode ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Model.VendorCode))
        {
            Model.VendorName = null;
            OriginOptions = [];
            return;
        }

        var defaults = await Notes.GetVendorDefaultsAsync(Model.VendorCode);
        if (defaults.Succeeded && defaults.VendorDefaults is not null)
        {
            Model.VendorName = defaults.VendorDefaults.VendorName;
            Model.Currency ??= defaults.VendorDefaults.Currency;
            Model.TaxGrCode ??= defaults.VendorDefaults.TaxGrCode;
        }
        else
        {
            Model.VendorName = null;
            ErrorMessage = defaults.Message;
        }

        await RefreshOriginOptionsAsync();
    }

    protected async Task OnOriginChangedAsync(string? originDocNo)
    {
        Model.OriginSbInvNo = originDocNo;

        var origin = OriginOptions.FirstOrDefault(x => x.DocNo == originDocNo);
        if (origin is null)
        {
            return;
        }

        // Inherit the origin's currency/rate when the operator has not chosen one: the note must describe
        // the same amounts. A conflict is refused server-side, so this only saves retyping.
        Model.Currency ??= origin.Currency;
    }

    // ── Line events ──────────────────────────────────────────────────────────

    protected void OnNewLineClick()
    {
        Popup = new LineEdit
        {
            StdUom = Uoms.FirstOrDefault()?.Code,
            TaxGroup = Model.TaxGrCode ?? TaxGroups.FirstOrDefault()?.TaxGrCode,
            Classification = "022",
            Qty = 1m
        };
        PopupIndex = -1;
        PopupVisible = true;
    }

    protected void OnEditLine(LineEdit line)
    {
        Popup = line.Clone();
        PopupIndex = EditLines.IndexOf(line);
        PopupVisible = true;
    }

    protected void OnDeleteLine(LineEdit line) => EditLines.Remove(line);

    protected void OnPopupSave()
    {
        var (net, tax, amount) = PoSbCalc.ComputeLineAmounts(
            Popup.Qty,
            Popup.UnitPrice,
            Popup.ItemDiscount,
            Popup.IDiscountType,
            Popup.ItemDiscount1,
            Popup.IDiscountType1,
            TaxPercentFor(Popup.TaxGroup),
            Popup.IsInclusive);

        Popup.Amount = amount;
        Popup.NetAmount = net;
        Popup.TaxAmt = tax;

        if (PopupIndex >= 0 && PopupIndex < EditLines.Count)
        {
            EditLines[PopupIndex] = Popup;
        }
        else
        {
            EditLines.Add(Popup);
        }

        PopupVisible = false;
    }

    /// <summary>
    /// Tax percentage for the popup PREVIEW only; the service resolves the same percentage from the same
    /// master on save, so the preview can be stale but the stored value cannot.
    /// </summary>
    private decimal TaxPercentFor(string? taxGroupCode) =>
        string.IsNullOrWhiteSpace(taxGroupCode)
            ? 0m
            : Lookups.TaxGroups.FirstOrDefault(
                x => string.Equals(x.TaxGrCode, taxGroupCode.Trim(), StringComparison.OrdinalIgnoreCase))?.Percentage ?? 0m;

    // ── Save ─────────────────────────────────────────────────────────────────

    private PoSbCdnSaveRequest BuildRequest() => new()
    {
        DocDate = Model.DocDate,
        Type = string.IsNullOrWhiteSpace(Model.Type) ? _type : Model.Type,
        VendorCode = Model.VendorCode,
        VendorName = Model.VendorName,
        OriginSbInvNo = Model.OriginSbInvNo,
        Currency = Model.Currency,
        CurrRate = Model.CurrRate,
        TaxGrCode = Model.TaxGrCode,
        Remarks = Model.Remarks,
        RowVersion = Model.RowVersion,
        Lines = EditLines.Select(x => x.ToRequest()).ToList()
    };

    protected async Task SaveAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? navigateTo = null;
        try
        {
            var request = BuildRequest();
            var result = IsNew
                ? await Notes.SaveNewAsync(request)
                : await Notes.UpdateAsync(Model.DocNo, request);

            if (result.Succeeded)
            {
                StatusMessage = "Saved.";
                navigateTo = BaseRoute;
            }
            else
            {
                ValidationErrors = result.ValidationErrors;
                ErrorMessage = result.Message;
            }
        }
        finally
        {
            IsSubmitting = false;
        }

        if (navigateTo is not null)
        {
            Navigation.NavigateTo(navigateTo);
        }
    }

    protected void OnEditFromView() => Navigation.NavigateTo($"{BaseRoute}/edit/{Model.DocNo}");

    protected void GoBack() => Navigation.NavigateTo(BaseRoute);

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError()
    {
        ErrorMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    protected async Task OnEInvoiceStateChangedAsync(string? irbmStatus)
    {
        Model.IrbmStatus = irbmStatus;
        Model.IsEInvoiceLocked = EInvoiceStatuses.IsLocked(irbmStatus);
        await Task.CompletedTask;
    }

    // ── View models ──────────────────────────────────────────────────────────

    protected sealed class PoSbCdnVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime DocDate { get; set; } = DateTime.Today;
        public string Status { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? Prefix { get; set; }
        public string VendorCode { get; set; } = string.Empty;
        public string? VendorName { get; set; }
        public string? OriginSbInvNo { get; set; }
        public string? Currency { get; set; }
        public decimal CurrRate { get; set; } = 1m;
        public string? TaxGrCode { get; set; }
        public string? Remarks { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string? IrbmStatus { get; set; }
        public string? IrbmUuid { get; set; }
        public string? IrbmOriUuid { get; set; }
        public string? IrbmError { get; set; }
        public bool IsEInvoiceLocked { get; set; }
    }

    protected sealed class LineEdit
    {
        /// <summary>
        /// Stable grid identity. The item code is NOT unique — one document may repeat an item — so it
        /// cannot be the grid key. Contract: <see cref="FromDto"/> assigns a NEW identity for a line loaded
        /// from the server, <c>Clone()</c> KEEPS the identity (it only ever backs the edit popup), and a
        /// manually added line gets a new one.
        /// </summary>
        public Guid UiKey { get; init; } = Guid.NewGuid();

        public string? ICode { get; set; }
        public string? IDesc { get; set; }
        public decimal Qty { get; set; } = 1m;
        public decimal UnitPrice { get; set; }
        public string? StdUom { get; set; }
        public string? TaxGroup { get; set; }
        public bool IsInclusive { get; set; }
        public decimal ItemDiscount { get; set; }
        public string? IDiscountType { get; set; }
        public decimal ItemDiscount1 { get; set; }
        public string? IDiscountType1 { get; set; }
        public string? Classification { get; set; }
        public string? Remarks { get; set; }

        public decimal Amount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal TaxAmt { get; set; }

        public static LineEdit FromDto(PoSbLineDto dto) => new()
        {
            UiKey = Guid.NewGuid(),
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Qty = dto.Qty,
            UnitPrice = dto.UnitPrice,
            StdUom = dto.StdUom,
            TaxGroup = dto.TaxGroup,
            IsInclusive = dto.IsInclusive,
            ItemDiscount = dto.ItemDiscount,
            IDiscountType = dto.IDiscountType,
            ItemDiscount1 = dto.ItemDiscount1,
            IDiscountType1 = dto.IDiscountType1,
            Classification = dto.Classification,
            Remarks = dto.Remarks,
            Amount = dto.Amount,
            NetAmount = dto.NetAmount,
            TaxAmt = dto.TaxAmt
        };

        public LineEdit Clone() => (LineEdit)MemberwiseClone();

        public PoSbLineRequest ToRequest() => new()
        {
            ICode = ICode,
            IDesc = IDesc,
            Qty = Qty,
            UnitPrice = UnitPrice,
            StdUom = StdUom,
            TaxGroup = TaxGroup,
            IsInclusive = IsInclusive,
            ItemDiscount = ItemDiscount,
            IDiscountType = IDiscountType,
            ItemDiscount1 = ItemDiscount1,
            IDiscountType1 = IDiscountType1,
            Classification = Classification,
            Remarks = Remarks
        };
    }
}
