using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Self-billed purchase invoice entry (LHDN type 11).
///
/// <para>
/// A self-billed document is issued by the BUYER, so the parties are reversed: the VENDOR is the payload's
/// <c>Supplier</c> and our COMPANY is its <c>Customer</c>. The header therefore asks for the VENDOR only —
/// its e-Invoice identity (TIN, registration, address, phone, MSIC, business description) is read live
/// from the vendor master when the payload is built, and a missing field is reported at submission rather
/// than copied onto the document.
/// </para>
/// </summary>
public partial class PoSbInvoice : PageBase
{
    [Inject] private IPoSbInvoiceService Invoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    /// <summary>
    /// The (mode, document) pair this instance last loaded. See <see cref="OnParametersSetAsync"/>: the
    /// router REUSES this instance for a change to another route the same component answers, so
    /// view -> edit must be detected here or the page stays on the mode it was created in.
    /// </summary>
    private string? _loadedKey;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;

    protected PoSbInvoiceVm Model { get; set; } = new();
    protected List<LineEdit> EditLines { get; } = [];
    protected PoSbLookups Lookups { get; set; } = new();
    protected IReadOnlyDictionary<string, string> ValidationErrors { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    protected bool CanAdd;
    protected bool CanEdit;

    // ── Line editor ──────────────────────────────────────────────────────────

    protected LineEdit Popup { get; set; } = new();
    protected bool PopupVisible;
    protected int PopupIndex = -1;
    protected string PopupTitle => PopupIndex < 0 ? "Add line" : "Edit line";

    // ── Mode helpers ─────────────────────────────────────────────────────────

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsEdit => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Editing is refused while the e-Invoice status locks the document (SUBMITTING / SUBMITTED / VALID) —
    /// the service re-checks it, this only disables the controls.
    /// </summary>
    protected bool IsEditable => !IsView && !Model.IsEInvoiceLocked;

    protected bool CanMutateLines => IsEditable && !IsSubmitting;

    /// <summary>A document needs a vendor and at least one line before it can be saved.</summary>
    protected bool CanSave => !string.IsNullOrWhiteSpace(Model.VendorCode) && EditLines.Count > 0;

    /// <summary>
    /// The e-Invoice panel is only meaningful for a SAVED document: an unsaved one has no document number
    /// to address, so the panel would have nothing to load.
    /// </summary>
    protected bool CanUseEInvoice => !IsNew && !string.IsNullOrWhiteSpace(Model.DocNo) && !IsSubmitting;

    protected string PageHeading => IsNew ? "New self-billed invoice" : "Self-billed invoice";

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

    /// <summary>
    /// Nothing happens in the per-instance <c>OnInitializedAsync</c> hook: the load lives in
    /// <see cref="OnParametersSetAsync"/> so that a REUSED instance can run it again.
    /// </summary>
    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    /// <summary>
    /// The router REUSES this component instance when the URL changes to another route this component
    /// answers — <c>.../view/X</c> -> <c>.../edit/X</c> is the SAME component — and <c>OnInitialized</c>
    /// does not run again, only the parameters are set. Without this guard the view-mode Edit button
    /// changed the URL and left the page read-only; only a full page load (which builds a new instance)
    /// showed the editable form.
    /// </summary>
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

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;

        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Edit);

        await LoadLookupsAsync();
        await ReloadAsync();
        IsLoading = false;
    }

    private async Task LoadLookupsAsync()
    {
        var result = await Invoices.GetLookupsAsync();
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
            Model = new PoSbInvoiceVm { DocDate = DateTime.Today };
            EditLines.Clear();
            return;
        }

        var result = await Invoices.GetAsync(DocNo ?? string.Empty);
        if (result.Succeeded && result.Document is not null)
        {
            ApplyDocument(result.Document);
        }
        else
        {
            ErrorMessage = result.Message;
        }
    }

    private void ApplyDocument(PoSbInvoiceDocument document)
    {
        Model = new PoSbInvoiceVm
        {
            DocNo = document.DocNo,
            DocDate = document.DocDate,
            Status = document.Status,
            Prefix = document.Prefix,
            VendorCode = document.VendorCode,
            VendorName = document.VendorName,
            Currency = document.Currency,
            CurrRate = document.CurrRate,
            TaxGrCode = document.TaxGrCode,
            Remarks = document.Remarks,
            RowVersion = document.RowVersion,
            IrbmStatus = document.IrbmStatus,
            IrbmUuid = document.IrbmUuid,
            IrbmError = document.IrbmError,
            IsEInvoiceLocked = document.IsEInvoiceLocked
        };

        EditLines.Clear();
        foreach (var line in document.Lines)
        {
            EditLines.Add(LineEdit.FromDto(line));
        }
    }

    // ── Header events ────────────────────────────────────────────────────────

    protected async Task OnVendorChangedAsync(string? vendorCode)
    {
        Model.VendorCode = vendorCode ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Model.VendorCode))
        {
            Model.VendorName = null;
            return;
        }

        var defaults = await Invoices.GetVendorDefaultsAsync(Model.VendorCode);
        if (!defaults.Succeeded || defaults.VendorDefaults is null)
        {
            Model.VendorName = null;
            ErrorMessage = defaults.Message;
            return;
        }

        var vendor = defaults.VendorDefaults;
        Model.VendorName = vendor.VendorName;

        // Blank-only defaults: an operator's explicit choice is never overwritten.
        Model.Currency ??= vendor.Currency;
        Model.TaxGrCode ??= vendor.TaxGrCode;
    }

    // ── Line events ──────────────────────────────────────────────────────────

    protected void OnNewLineClick()
    {
        Popup = new LineEdit
        {
            StdUom = Uoms.FirstOrDefault()?.Code,
            TaxGroup = Model.TaxGrCode ?? TaxGroups.FirstOrDefault()?.TaxGrCode,
            Classification = "022"
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
        // Amounts are computed server-side; the popup only previews them.
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
    /// Tax percentage for the popup PREVIEW only. The service resolves the same percentage from the same
    /// master on save, so the preview can be wrong for a stale lookup list but the stored value cannot.
    /// </summary>
    private decimal TaxPercentFor(string? taxGroupCode) =>
        string.IsNullOrWhiteSpace(taxGroupCode)
            ? 0m
            : Lookups.TaxGroups.FirstOrDefault(
                x => string.Equals(x.TaxGrCode, taxGroupCode.Trim(), StringComparison.OrdinalIgnoreCase))?.Percentage ?? 0m;

    // ── Save / post / rollback ───────────────────────────────────────────────

    private PoSbInvoiceSaveRequest BuildRequest() => new()
    {
        DocDate = Model.DocDate,
        VendorCode = Model.VendorCode,
        VendorName = Model.VendorName,
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
                ? await Invoices.SaveNewAsync(request)
                : await Invoices.UpdateAsync(Model.DocNo, request);

            if (result.Succeeded)
            {
                StatusMessage = "Saved.";
                navigateTo = "/purchase/self-billed-invoices";
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

    protected void OnEditFromView() =>
        Navigation.NavigateTo(DocumentReturnNavigation.PreserveReturnUrl(
            Navigation.Uri, $"/purchase/self-billed-invoices/edit/{Model.DocNo}"));

    protected void GoBack() => DocumentReturnNavigation.NavigateBack(Navigation, "/purchase/self-billed-invoices");

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError()
    {
        ErrorMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The e-Invoice panel reports the new status so the edit lock follows it immediately.</summary>
    protected async Task OnEInvoiceStateChangedAsync(string? irbmStatus)
    {
        Model.IrbmStatus = irbmStatus;
        Model.IsEInvoiceLocked = EInvoiceStatuses.IsLocked(irbmStatus);
        await Task.CompletedTask;
    }

    // ── View models ──────────────────────────────────────────────────────────

    protected sealed class PoSbInvoiceVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime DocDate { get; set; } = DateTime.Today;
        public string Status { get; set; } = string.Empty;
        public string? Prefix { get; set; }
        public string VendorCode { get; set; } = string.Empty;
        public string? VendorName { get; set; }
        public string? Currency { get; set; }
        public decimal CurrRate { get; set; } = 1m;
        public string? TaxGrCode { get; set; }
        public string? Remarks { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string? IrbmStatus { get; set; }
        public string? IrbmUuid { get; set; }
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

        // Computed for display only.
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
