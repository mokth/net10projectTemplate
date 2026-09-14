using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Purchase Credit / Debit Note entry screen (New / Edit / View).
///
/// The page keeps editable state in flat fields rather than binding <see cref="PoCdnDocument"/>
/// directly, because the document DTO is intentionally immutable (<c>init</c> properties) so that
/// nothing can mutate a loaded document by accident.
///
/// A stock-return line is authored by first picking the source invoice line (which carries the C24
/// traceability and the PO link) and then picking an on-hand balance location. Both are required:
/// C43 rejects a stock-return line without a pile, and C24 rejects one without an invoice line.
/// </summary>
public partial class PoCdn : PageBase, IDisposable
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    /// <summary>
    /// Optional invoice number supplied by the "Create Purchase Credit Note" action on the purchase
    /// invoice screen, so the operator does not have to re-key the reference.
    /// </summary>
    [SupplyParameterFromQuery(Name = "invNo")]
    public string? InvoiceFromQuery { get; set; }

    [Inject] private IPoCdnService Cdns { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;

    protected string? StatusMessage;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool PopupVisible;
    protected bool InvoicePickerVisible;
    protected bool InvoicePickerLoading;
    protected bool LinePickerVisible;
    protected bool LinePickerLoading;
    protected string? LinePickerError;
    protected string? PopupError;
    protected string? InvoicePickerError;
    protected bool CanEditPermission;
    protected bool CanPostPermission;

    // ── Header state ─────────────────────────────────────────────────────────
    protected string DocNoDisplay = "AUTO";
    protected string Status = PoCdnStatuses.New;
    protected DateTime DocDate = DateTime.Today;
    protected string? VendorCode;
    protected string? VendorName;
    protected string? Prefix;
    protected string Currency = "MYR";
    protected decimal CurrRate = 1m;
    protected bool CurrRateValid;
    protected string? PayCode;
    protected string? TaxGrCode;
    protected string? ReasonCode;
    protected string? InvNo;
    protected string? SupplierDocNo;
    protected DateTime? SupplierDocDate;
    protected bool ReturnStock;
    protected string? ExternalDocNo;
    protected string? Remarks;
    protected string? ReservationIndicator;
    protected string? VendorBlockedMessage;
    protected decimal GrossAmnt;
    protected decimal Taxes;
    protected decimal TotAmnt;
    protected int? VrBatchNo;

    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Lookups ──────────────────────────────────────────────────────────────
    protected List<PoCdnVendorLookupRow> Vendors { get; set; } = [];
    protected List<PoCdnItemLookupRow> Items { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<PoCdnTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<PoCdnReasonCodeOption> ReasonCodes { get; set; } = [];
    protected List<PoCdnInvoicePickerRow> InvoicePickerRows { get; set; } = [];
    protected List<PoCdnInvoiceLinePickerRow> InvoiceLineRows { get; set; } = [];

    protected List<PoCdnLineVm> Lines { get; set; } = [];
    protected PoCdnLineVm Popup { get; set; } = new();

    private byte[] _rowVersion = [];
    private string? _loadedKey;
    private string _type = "CN";
    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();

    // ── Derived ──────────────────────────────────────────────────────────────

    protected bool IsCreditNote => string.Equals(_type, PoCdnTypes.CreditNote, StringComparison.OrdinalIgnoreCase);

    protected string MenuCode => IsCreditNote ? MenuCodes.PurchaseCreditNote : MenuCodes.PurchaseDebitNote;

    protected string ListRoute => IsCreditNote ? "/purchase/credit-notes" : "/purchase/debit-notes";

    protected string EditRouteFor(string docNo) =>
        IsCreditNote ? $"/purchase/credit-notes/edit/{docNo}" : $"/purchase/debit-notes/edit/{docNo}";

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;
    protected bool IsNewDocument => string.Equals(Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase);
    protected bool IsPostedDocument => string.Equals(Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    protected bool CanEditFromView => IsViewMode && CanEditPermission && IsNewDocument && !string.IsNullOrWhiteSpace(DocNo);

    protected string PageHeading =>
        IsNewMode ? $"New {(IsCreditNote ? "credit note" : "debit note")}"
        : IsEditMode ? $"Edit {(IsCreditNote ? "credit note" : "debit note")}"
        : $"View {(IsCreditNote ? "credit note" : "debit note")}";

    protected string StatusDisplay =>
        IsPostedDocument ? "Posted (operational)" : Status;

    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";

    protected bool HasVendor => !string.IsNullOrWhiteSpace(VendorCode);
    protected bool HasInvoiceReference => !string.IsNullOrWhiteSpace(InvNo);

    /// <summary>C43: DN lines can never move stock, so the header flag is CN-only.</summary>
    protected bool CanToggleReturnStock => CanEditDocument && IsCreditNote;

    /// <summary>
    /// C44: the supplier document number may only be blank for INTERNAL_ADJUSTMENT, which is itself
    /// permission-gated and surfaced via <see cref="PoCdnReasonCodeOption.AllowsBlankSupplierDocNo"/>.
    /// </summary>
    protected bool SupplierDocNoRequired => !SelectedReasonAllowsBlankSupplierDocNo;

    protected bool SelectedReasonAllowsBlankSupplierDocNo =>
        ReasonCodes.FirstOrDefault(x => string.Equals(x.Code, ReasonCode, StringComparison.OrdinalIgnoreCase))
            ?.AllowsBlankSupplierDocNo ?? false;

    protected bool SupplierDocDateRequired => !string.IsNullOrWhiteSpace(SupplierDocNo);

    protected bool CanMutateLines => CanEditDocument && HasVendor && !IsSubmitting;

    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasVendor
        && !string.IsNullOrWhiteSpace(ReasonCode);

    protected string StockReturnHint =>
        "A stock-return line needs a source invoice line and an on-hand balance location. "
        + "Use Source line to pick one, then choose the pile to return from.";

    protected IReadOnlyList<PoCdnReasonCodeOption> SelectableReasons =>
        ReasonCodes.Where(x => x.AllowedForUser).ToList();

    protected PoCdnReasonCodeOption? SelectedReasonInfo =>
        ReasonCodes.FirstOrDefault(x => string.Equals(x.Code, ReasonCode, StringComparison.OrdinalIgnoreCase));

    // ── Lifecycle ────────────────────────────────────────────────────────────

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
        _type = Navigation.Uri.Contains("/debit-notes", StringComparison.OrdinalIgnoreCase)
            ? PoCdnTypes.DebitNote
            : PoCdnTypes.CreditNote;
    }

    private async Task LoadAsync()
    {
        ResolveType();
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var lookups = await Cdns.GetLookupsAsync(_type, _cts.Token);
            if (lookups.Succeeded)
            {
                Vendors = lookups.Vendors.ToList();
                Items = lookups.Items.ToList();
                Warehouses = lookups.Warehouses.ToList();
                TaxGroups = lookups.TaxGroups.ToList();
                PayCodes = lookups.PayCodes.ToList();
                ReasonCodes = lookups.ReasonCodes.ToList();
            }
            else
            {
                ErrorMessage = lookups.ErrorMessage ?? "Unable to load lookups.";
            }

            CanEditPermission = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit, _cts.Token);
            CanPostPermission = await AccessRights.CanAsync(MenuCode, PermissionCodes.Post, _cts.Token);

            if (IsNewMode)
            {
                ResetNewDocument();

                if (!string.IsNullOrWhiteSpace(InvoiceFromQuery))
                {
                    InvNo = InvoiceFromQuery!.Trim();
                    await RefreshReservationIndicatorAsync();
                }
            }
            else if (!string.IsNullOrWhiteSpace(DocNo))
            {
                var result = await Cdns.GetAsync(DocNo, _cts.Token);
                if (!result.Succeeded || result.Document is null)
                {
                    ErrorMessage = result.ErrorMessage ?? "Document was not found.";
                    IsLoading = false;
                    return;
                }

                ApplyDocument(result.Document);
            }
        }
        catch (OperationCanceledException)
        {
            // navigation away
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetNewDocument()
    {
        DocNoDisplay = "AUTO";
        Status = PoCdnStatuses.New;
        DocDate = Dates.Today.Date;
        VendorCode = null;
        VendorName = null;
        Prefix = null;
        Currency = "MYR";
        CurrRate = 1m;
        CurrRateValid = true;
        PayCode = null;
        TaxGrCode = null;
        InvNo = null;
        SupplierDocNo = null;
        SupplierDocDate = null;
        ReturnStock = false;
        ExternalDocNo = null;
        Remarks = null;
        ReservationIndicator = null;
        VendorBlockedMessage = null;
        VrBatchNo = null;
        Lines = [];
        ValidationErrors.Clear();
        RecalcDocument();
    }

    private void ApplyDocument(PoCdnDocument doc)
    {
        DocNoDisplay = doc.DocNo;
        Status = doc.Status;
        DocDate = doc.DocDate;
        VendorCode = doc.VendorCode;
        VendorName = doc.VendorName;
        Prefix = doc.Prefix;
        Currency = doc.Currency ?? "MYR";
        CurrRate = doc.CurrRate;
        CurrRateValid = true;
        PayCode = doc.PayCode;
        TaxGrCode = doc.TaxGrCode;
        ReasonCode = doc.ReasonCode;
        InvNo = doc.InvNo;
        SupplierDocNo = doc.SupplierDocNo;
        SupplierDocDate = doc.SupplierDocDate;
        ReturnStock = doc.ReturnStock;
        ExternalDocNo = doc.ExternalDocNo;
        Remarks = doc.Remarks;
        VrBatchNo = doc.VrBatchNo;
        _rowVersion = doc.RowVersion;
        Lines = doc.Lines.Select(PoCdnLineVm.FromDto).ToList();
        ValidationErrors.Clear();
        GrossAmnt = doc.GrossAmnt;
        Taxes = doc.Taxes;
        TotAmnt = doc.TotAmnt;
    }

    // ── Header handlers ──────────────────────────────────────────────────────

    protected async Task OnDocDateChanged(DateTime newDate)
    {
        DocDate = newDate;
        await RefreshFxAsync();
    }

    protected async Task OnVendorChanged(string? vendorCode)
    {
        VendorCode = vendorCode;
        VendorName = Vendors.FirstOrDefault(x => x.VendorCode == vendorCode)?.VendorName;

        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            VendorBlockedMessage = null;
            return;
        }

        var result = await Cdns.GetVendorDefaultsAsync(vendorCode!, DocDate, _cts.Token);
        if (!result.Succeeded || result.VendorDefaults is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load vendor defaults.";
            return;
        }

        var defaults = result.VendorDefaults;
        VendorName = defaults.VendorName;

        // C13/C15: a suspended or inactive vendor blocks a new document.
        VendorBlockedMessage = defaults.BlockedForSave ? defaults.BlockedMessage : null;

        if (string.IsNullOrWhiteSpace(InvNo))
        {
            Currency = defaults.Currency ?? Currency;
            CurrRate = defaults.CurrRate;
            CurrRateValid = defaults.CurrRateValid;
        }

        PayCode ??= defaults.PayCode;
        TaxGrCode ??= defaults.TaxGrCode;
    }

    protected async Task RefreshFxAsync()
    {
        if (string.IsNullOrWhiteSpace(Currency))
        {
            CurrRateValid = false;
            return;
        }

        var result = await Cdns.ResolveCurrencyRateAsync(Currency, DocDate, _cts.Token);
        CurrRateValid = result.CurrRateValid;
        if (result.Succeeded)
        {
            CurrRate = result.CurrRate;
        }
        else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            ValidationErrors["Currency"] = result.ErrorMessage!;
        }
    }

    protected async Task OnInvNoChanged(string? invNo)
    {
        InvNo = string.IsNullOrWhiteSpace(invNo) ? null : invNo.Trim();
        await RefreshReservationIndicatorAsync();
    }

    // ── Source invoice line picker (C24 / C34) ───────────────────────────────

    /// <summary>
    /// Lists the invoice's lines with their remaining ceilings already computed by the service, so
    /// the operator picks from what is actually still creditable rather than from the raw invoice.
    /// </summary>
    protected async Task OpenLinePickerAsync()
    {
        if (string.IsNullOrWhiteSpace(InvNo))
        {
            ErrorMessage = "Select or enter an invoice reference before picking a source line.";
            return;
        }

        LinePickerVisible = true;
        LinePickerLoading = true;
        LinePickerError = null;
        try
        {
            var result = await Cdns.GetInvoiceLinesAsync(InvNo!, IsNewMode ? null : DocNo, _cts.Token);
            if (!result.Succeeded)
            {
                LinePickerError = result.ErrorMessage ?? "Unable to load the invoice lines.";
                InvoiceLineRows = [];
                return;
            }

            InvoiceLineRows = result.InvoiceLinePickerRows.ToList();
        }
        finally
        {
            LinePickerLoading = false;
        }
    }

    /// <summary>
    /// Seeds the popup from a chosen invoice line. Sets C24 traceability and the PO link that a
    /// stock-return line requires, so the operator does not have to find the PO by hand.
    /// </summary>
    protected void UseInvoiceLine(PoCdnInvoiceLinePickerRow row)
    {
        if (row is null) return;

        Popup.InvLineNo = row.Line;
        Popup.ICode = row.ICode;
        Popup.IDesc = row.IDesc;
        Popup.UnitPrice = row.UnitPrice;
        Popup.TaxGroup = row.TaxGroup;
        Popup.IsInclusive = row.IsInclusive;
        Popup.ItemGlCode = row.ItemGlCode;
        Popup.Classification = row.Classification;
        Popup.StdCustPSize = row.StdCustPSize == 0m ? 1m : row.StdCustPSize;
        Popup.PoNo = row.PoNo;
        Popup.PoRelNo = row.PoRelNo;
        Popup.PoLineNo = row.PoLineNo;
        Popup.LineRemainingStdQty = row.RemainingStdQty;
        Popup.LineRemainingAmount = row.RemainingAmount;

        LinePickerVisible = false;
    }

    /// <summary>Applies the pile chosen in <c>IvBalLocPicker</c> to the line being edited.</summary>
    protected Task OnBalLocSelectedAsync(IvBalLocLookupRow row)
    {
        Popup.FromBalLocId = row.Id;
        Popup.BalLocDisplay = string.IsNullOrWhiteSpace(row.LotNo) ? $"#{row.Id}" : row.LotNo;
        Popup.FrWarehouse = row.WhCode;
        Popup.LocCode = row.LocCode;
        Popup.LotNo = row.LotNo;
        Popup.IStatus = row.IStatus;
        Popup.ExpiryDate = row.ExpiryDate;
        Popup.AvailableQty = row.StdQty;
        Popup.StdUom = row.StdUom;
        Popup.LotControl = row.LotControl;
        return Task.CompletedTask;
    }

    private async Task RefreshReservationIndicatorAsync()
    {
        ReservationIndicator = null;
        if (string.IsNullOrWhiteSpace(InvNo))
        {
            return;
        }

        var result = await Cdns.GetInvoiceReservationsAsync(InvNo!, IsNewMode ? null : DocNo, _cts.Token);
        if (!result.Succeeded || result.Reservations is null)
        {
            return;
        }

        var summary = result.Reservations;
        ReservationIndicator =
            $"Invoice {summary.InvNo}: total {summary.InvoiceTotal:n2} · "
            + $"posted {summary.PostedCnTotal:n2} · draft {summary.DraftCnTotal:n2} · "
            + $"remaining {summary.Remaining:n2}";

        // C18: the invoice currency governs the note.
        if (!string.IsNullOrWhiteSpace(summary.InvNo))
        {
            await RefreshFxAsync();
        }
    }

    protected void OnReasonCodeChanged(string? code) => ReasonCode = code;

    // ── Invoice picker ───────────────────────────────────────────────────────

    protected async Task OpenInvoicePickerAsync()
    {
        if (string.IsNullOrWhiteSpace(VendorCode))
        {
            ErrorMessage = "Select a vendor before choosing an invoice.";
            return;
        }

        InvoicePickerVisible = true;
        InvoicePickerLoading = true;
        InvoicePickerError = null;
        try
        {
            var result = await Cdns.SearchPostedInvoicesAsync(VendorCode!, null, _cts.Token);
            if (!result.Succeeded)
            {
                InvoicePickerError = result.ErrorMessage ?? "Unable to load invoices.";
                InvoicePickerRows = [];
                return;
            }

            InvoicePickerRows = result.InvoicePickerRows.ToList();
        }
        finally
        {
            InvoicePickerLoading = false;
        }
    }

    /// <summary>
    /// Maps a posted invoice onto a draft note (C5). The service never maps onto a quantity
    /// correction; it builds a fresh draft, so we replace the current lines rather than append.
    /// </summary>
    protected async Task CopyFromInvoiceAsync(PoCdnInvoicePickerRow row)
    {
        if (row is null) return;

        var result = await Cdns.CopyFromInvoiceAsync(row.InvNo, _cts.Token);
        if (!result.Succeeded || result.Document is null)
        {
            InvoicePickerError = result.ErrorMessage ?? "Unable to copy from the invoice.";
            return;
        }

        var doc = result.Document;
        Lines = doc.Lines.Select(PoCdnLineVm.FromDto).ToList();
        InvNo = doc.InvNo ?? row.InvNo;
        Currency = doc.Currency ?? Currency;
        CurrRate = doc.CurrRate;
        CurrRateValid = true;
        TaxGrCode ??= doc.TaxGrCode;
        PayCode ??= doc.PayCode;
        RecalcDocument();
        InvoicePickerVisible = false;
        StatusMessage = $"Copied {Lines.Count} line(s) from invoice {row.InvNo}. Review and save to confirm.";
    }

    /// <summary>
    /// Sets the invoice reference only. Deliberately does not copy lines — "use the invoice as the
    /// reference but keep my own lines" is a real workflow (e.g. a rebate against invoice X).
    /// </summary>
    protected Task UseInvoiceReferenceOnlyAsync(PoCdnInvoicePickerRow row)
    {
        if (row is null) return Task.CompletedTask;

        InvNo = row.InvNo;
        InvoicePickerVisible = false;
        StatusMessage = $"Invoice reference set to {row.InvNo}. Lines were not changed.";
        return RefreshReservationIndicatorAsync();
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    protected void OnNewLineClick()
    {
        Popup = new PoCdnLineVm
        {
            ClientKey = Guid.NewGuid().ToString("N"),
            TaxGroup = TaxGrCode,
            StdCustPSize = 1m,
            InvLineNo = null,
            // A stock-return line only makes sense when the header says stock is moving.
            IsStockReturn = ReturnStock
        };
        PopupError = null;
        PopupVisible = true;
    }

    protected void EditLine(PoCdnLineVm line)
    {
        Popup = line.Clone();
        PopupError = null;
        PopupVisible = true;
    }

    protected void RemoveLine(PoCdnLineVm line)
    {
        Lines.Remove(line);
        Renumber();
        RecalcDocument();
    }

    protected void OnPopupItemChanged(string? iCode)
    {
        Popup.ICode = iCode;
        var item = Items.FirstOrDefault(x => x.ICode == iCode);
        if (item is null) return;

        Popup.IDesc = item.IDesc;
        Popup.UnitPrice = item.PurchasePrice ?? Popup.UnitPrice;
        Popup.TaxGroup ??= item.PurchaseTaxGroup ?? item.TaxGroup;
        Popup.ItemGlCode = item.PurchaseGlCode;
        Popup.Classification = item.Classification;
        Popup.StockControl = item.StockControl;
        Popup.StdCustPSize = PoCdnCalc.IsUsablePackSize(item.ItemPackSize) ? item.ItemPackSize : 1m;
    }

    protected void OnPopupSave()
    {
        PopupError = null;

        if (string.IsNullOrWhiteSpace(Popup.ICode))
        {
            PopupError = "Select an item.";
            return;
        }

        if (Popup.Qty <= 0m)
        {
            PopupError = "Quantity must be greater than zero.";
            return;
        }

        if (Popup.UnitPrice <= 0m && !Popup.IsTaxOnly)
        {
            PopupError = "Unit price must be greater than zero.";
            return;
        }

        if (ReturnStock && Popup.IsStockReturn)
        {
            // C43: a stock-return line must name a source invoice line, a PO line and a pile.
            if (Popup.InvLineNo is null)
            {
                PopupError = "A stock-return line needs a source invoice line. Use 'Source line'.";
                return;
            }

            if (Popup.FromBalLocId is null || Popup.FromBalLocId <= 0)
            {
                PopupError = "Choose the on-hand balance location to return from.";
                return;
            }

            if (string.IsNullOrWhiteSpace(Popup.PoNo) || Popup.PoRelNo is null || Popup.PoLineNo is null)
            {
                PopupError = "A stock-return line needs a PO line. Copy the line from the invoice to pick one up.";
                return;
            }

            // Do not let the operator commit more than the pile actually holds.
            if (Popup.AvailableQty > 0m && Popup.Qty > Popup.AvailableQty)
            {
                PopupError = $"Quantity {Popup.Qty:n4} exceeds the {Popup.AvailableQty:n4} on hand for that pile.";
                return;
            }
        }
        else if (Popup.IsStockReturn && !ReturnStock)
        {
            PopupError = "Enable 'Return stock' on the document header before adding a stock-return line.";
            return;
        }

        RecalcLine(Popup);

        var existing = Lines.FirstOrDefault(x => x.ClientKey == Popup.ClientKey);
        if (existing is not null)
        {
            Lines[Lines.IndexOf(existing)] = Popup.Clone();
        }
        else
        {
            Lines.Add(Popup.Clone());
        }

        Renumber();
        RecalcDocument();
        PopupVisible = false;
    }

    private void Renumber()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].Line = (short)(i + 1);
        }
    }

    private decimal TaxPercentFor(string? taxGroup)
    {
        if (string.IsNullOrWhiteSpace(taxGroup)) return 0m;
        return TaxGroups.FirstOrDefault(x =>
            string.Equals(x.TaxGrCode, taxGroup.Trim(), StringComparison.OrdinalIgnoreCase))?.Percentage ?? 0m;
    }

    private void RecalcLine(PoCdnLineVm line)
    {
        var (net, tax, amount) = PoCdnCalc.ComputeLineAmounts(
            line.Qty,
            line.UnitPrice,
            line.ItemDiscount,
            line.IDiscountType,
            line.ItemDiscount1,
            line.IDiscountType1,
            TaxPercentFor(line.TaxGroup),
            line.IsInclusive,
            2);

        line.NetAmount = net;
        line.TaxAmt = tax;
        line.Amount = amount;
        line.IsTaxOnly = net == 0m && tax != 0m;
    }

    /// <summary>
    /// Client-side preview only. The service recomputes totals authoritatively on save.
    /// </summary>
    private void RecalcDocument()
    {
        foreach (var line in Lines)
        {
            RecalcLine(line);
        }

        GrossAmnt = PoCdnCalc.Money(Lines.Sum(x => x.NetAmount));
        Taxes = PoCdnCalc.Money(Lines.Sum(x => x.TaxAmt));
        TotAmnt = PoCdnCalc.Money(GrossAmnt + Taxes);
    }

    // ── Persist ──────────────────────────────────────────────────────────────

    private PoCdnSaveRequest ToRequest() => new()
    {
        Type = _type,
        DocDate = DocDate,
        VendorCode = VendorCode ?? string.Empty,
        InvNo = InvNo,
        ReturnStock = ReturnStock,
        Currency = Currency,
        CurrRate = CurrRate,
        PayCode = PayCode,
        TaxGrCode = TaxGrCode,
        ReasonCode = ReasonCode,
        SupplierDocNo = SupplierDocNo,
        SupplierDocDate = SupplierDocDate,
        Remarks = Remarks,
        ExternalDocNo = ExternalDocNo,
        RowVersion = _rowVersion.Length == 0 ? null : _rowVersion,
        Lines = Lines.Select(x => x.ToRequest()).ToList()
    };

    protected async Task OnSaveAsync()
    {
        if (IsSubmitting) return;

        using var blocking = BeginBlockingWork("Please wait. Saving is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();

        try
        {
            RecalcDocument();

            if (IsNewMode)
            {
                var created = await Cdns.SaveNewAsync(ToRequest(), _cts.Token);
                if (!HandleOperationResult(created, stayOnPage: false)) return;

                StatusMessage = $"Saved {created.DocNo}.";
                Navigation.NavigateTo(EditRouteFor(created.DocNo!));
                return;
            }

            var updated = await Cdns.UpdateAsync(DocNo!, ToRequest(), _cts.Token);
            if (!HandleOperationResult(updated, stayOnPage: true)) return;

            StatusMessage = "Saved.";
            var reloaded = await Cdns.GetAsync(DocNo!, _cts.Token);
            if (reloaded.Succeeded && reloaded.Document is not null)
            {
                ApplyDocument(reloaded.Document);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnPostAsync()
    {
        if (IsSubmitting || string.IsNullOrWhiteSpace(DocNo)) return;

        using var blocking = BeginBlockingWork("Please wait. Posting is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await Cdns.PostAsync(
                [new PoCdnKeyedRequest { DocNo = DocNo!, RowVersion = _rowVersion }], _cts.Token);

            var item = result.Posting.FirstOrDefault();
            if (item is not null && !item.Succeeded)
            {
                ErrorMessage = item.ErrorMessage ?? item.Outcome;
            }
            else if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to post the document.";
            }
            else
            {
                StatusMessage = "Posted.";
                if (item?.VendorWarning == true && !string.IsNullOrWhiteSpace(item.VendorWarningMessage))
                {
                    StatusMessage = $"Posted. {item.VendorWarningMessage}";
                }
            }

            await ReloadCurrentAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnRollbackAsync()
    {
        if (IsSubmitting || string.IsNullOrWhiteSpace(DocNo)) return;

        using var blocking = BeginBlockingWork("Please wait. Rollback is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await Cdns.RollbackAsync(
                [new PoCdnKeyedRequest { DocNo = DocNo!, RowVersion = _rowVersion }], _cts.Token);

            var item = result.Posting.FirstOrDefault();
            if (item is not null && !item.Succeeded)
            {
                ErrorMessage = item.ErrorMessage ?? item.Outcome;
            }
            else if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to roll back the document.";
            }
            else
            {
                StatusMessage = "Rolled back.";
            }

            await ReloadCurrentAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnDeleteAsync()
    {
        if (IsSubmitting || string.IsNullOrWhiteSpace(DocNo)) return;

        using var blocking = BeginBlockingWork("Please wait. Delete is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await Cdns.DeleteAsync(
                [new PoCdnKeyedRequest { DocNo = DocNo!, RowVersion = _rowVersion }], _cts.Token);

            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to delete the document.";
                await ReloadCurrentAsync();
                return;
            }

            Navigation.NavigateTo(ListRoute);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task ReloadCurrentAsync()
    {
        if (string.IsNullOrWhiteSpace(DocNo)) return;

        var result = await Cdns.GetAsync(DocNo!, _cts.Token);
        if (result.Succeeded && result.Document is not null)
        {
            ApplyDocument(result.Document);
        }
        else
        {
            // A failed reload must not silently leave stale state on screen.
            ErrorMessage = result.ErrorMessage ?? "Unable to reload the document. Refresh before continuing.";
        }
    }

    protected void OnClose() => Navigation.NavigateTo(ListRoute);

    protected void OnEditFromView() => Navigation.NavigateTo(EditRouteFor(DocNo!));

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private bool HandleOperationResult(PoCdnOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        switch (result.ErrorKind)
        {
            case PoCdnErrorKind.Validation:
                ValidationErrors = new Dictionary<string, string>(
                    result.ValidationErrors, StringComparer.OrdinalIgnoreCase);
                ErrorMessage = result.ErrorMessage ?? "Validation failed.";
                break;
            case PoCdnErrorKind.Concurrency:
                ErrorMessage = result.ErrorMessage
                    ?? "This document was changed by another user. Reload before saving.";
                break;
            case PoCdnErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Document was not found.";
                break;
            case PoCdnErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }
}

/// <summary>
/// Editable line state. Mirrors <see cref="PoCdnLineDto"/> but is mutable so the popup editor can
/// bind to it. <see cref="ClientKey"/> keeps grid identity stable while lines are renumbered.
/// </summary>
public sealed class PoCdnLineVm
{
    public string ClientKey { get; set; } = Guid.NewGuid().ToString("N");
    public short Line { get; set; }
    public short? InvLineNo { get; set; }
    public bool IsStockReturn { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal StdCustPSize { get; set; } = 1m;
    public decimal ItemDiscount { get; set; }
    public decimal ItemDiscount1 { get; set; }
    public string? IDiscountType { get; set; }
    public string? IDiscountType1 { get; set; }
    public bool IsInclusive { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmt { get; set; }
    public decimal NetAmount { get; set; }
    public decimal Amount { get; set; }
    public bool IsTaxOnly { get; set; }
    public bool StockControl { get; set; }
    public string? ItemGlCode { get; set; }
    public string? Classification { get; set; }
    public string? Remarks { get; set; }
    public decimal CostPrice { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }

    // ── Stock-return fields (C43). Populated only when IsStockReturn. ──
    /// <summary>The authoritative inventory source for the line.</summary>
    public int? FromBalLocId { get; set; }
    public string? FrWarehouse { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }

    /// <summary>VM-only: what the picker shows for the chosen pile.</summary>
    public string? BalLocDisplay { get; set; }

    /// <summary>VM-only: on-hand qty of the chosen pile, so the operator can see it before committing.</summary>
    public decimal AvailableQty { get; set; }

    public string? StdUom { get; set; }
    public bool LotControl { get; set; }

    /// <summary>VM-only: source invoice line ceilings from the picker (C34), for display.</summary>
    public decimal? LineRemainingStdQty { get; set; }
    public decimal? LineRemainingAmount { get; set; }

    public PoCdnLineVm Clone() => new()
    {
        ClientKey = ClientKey,
        Line = Line,
        InvLineNo = InvLineNo,
        IsStockReturn = IsStockReturn,
        ICode = ICode,
        IDesc = IDesc,
        Qty = Qty,
        UnitPrice = UnitPrice,
        StdCustPSize = StdCustPSize,
        ItemDiscount = ItemDiscount,
        ItemDiscount1 = ItemDiscount1,
        IDiscountType = IDiscountType,
        IDiscountType1 = IDiscountType1,
        IsInclusive = IsInclusive,
        TaxGroup = TaxGroup,
        TaxAmt = TaxAmt,
        NetAmount = NetAmount,
        Amount = Amount,
        IsTaxOnly = IsTaxOnly,
        StockControl = StockControl,
        ItemGlCode = ItemGlCode,
        Classification = Classification,
        Remarks = Remarks,
        CostPrice = CostPrice,
        PoNo = PoNo,
        PoRelNo = PoRelNo,
        PoLineNo = PoLineNo,
        FromBalLocId = FromBalLocId,
        FrWarehouse = FrWarehouse,
        LocCode = LocCode,
        IStatus = IStatus,
        LotNo = LotNo,
        ExpiryDate = ExpiryDate,
        BalLocDisplay = BalLocDisplay,
        AvailableQty = AvailableQty,
        StdUom = StdUom,
        LotControl = LotControl,
        LineRemainingStdQty = LineRemainingStdQty,
        LineRemainingAmount = LineRemainingAmount
    };

    public PoCdnLineRequest ToRequest() => new()
    {
        InvLineNo = InvLineNo,
        IsStockReturn = IsStockReturn,
        ICode = ICode,
        IDesc = IDesc,
        Qty = Qty,
        UnitPrice = UnitPrice,
        StdCustPSize = StdCustPSize,
        ItemDiscount = ItemDiscount,
        ItemDiscount1 = ItemDiscount1,
        IDiscountType = IDiscountType,
        IDiscountType1 = IDiscountType1,
        IsInclusive = IsInclusive,
        TaxGroup = TaxGroup,
        ItemGlCode = ItemGlCode,
        Classification = Classification,
        Remarks = Remarks,
        CostPrice = CostPrice,
        PoNo = PoNo,
        PoRelNo = PoRelNo,
        PoLineNo = PoLineNo,
        FromBalLocId = FromBalLocId,
        FrWarehouse = FrWarehouse,
        LocCode = LocCode,
        IStatus = IStatus,
        LotNo = LotNo,
        ExpiryDate = ExpiryDate
    };

    public static PoCdnLineVm FromDto(PoCdnLineDto dto) => new()
    {
        // A fresh key per materialisation: ClientKey is the grid's identity, and two VMs built from
        // the same DTO (e.g. load, then reload after post) must never collide.
        ClientKey = Guid.NewGuid().ToString("N"),
        Line = dto.Line,
        InvLineNo = dto.InvLineNo,
        IsStockReturn = dto.IsStockReturn,
        ICode = dto.ICode,
        IDesc = dto.IDesc,
        Qty = dto.Qty,
        UnitPrice = dto.UnitPrice,
        StdCustPSize = dto.StdCustPSize == 0m ? 1m : dto.StdCustPSize,
        ItemDiscount = dto.ItemDiscount,
        ItemDiscount1 = dto.ItemDiscount1,
        IDiscountType = dto.IDiscountType,
        IDiscountType1 = dto.IDiscountType1,
        IsInclusive = dto.IsInclusive,
        TaxGroup = dto.TaxGroup,
        TaxAmt = dto.TaxAmt,
        NetAmount = dto.NetAmount,
        Amount = dto.Amount,
        IsTaxOnly = dto.IsTaxOnly,
        StockControl = dto.StockControl,
        ItemGlCode = dto.ItemGlCode,
        Classification = dto.Classification,
        Remarks = dto.Remarks,
        CostPrice = dto.CostPrice,
        PoNo = dto.PoNo,
        PoRelNo = dto.PoRelNo,
        PoLineNo = dto.PoLineNo,
        FromBalLocId = dto.FromBalLocId,
        FrWarehouse = dto.FrWarehouse,
        LocCode = dto.LocCode,
        IStatus = dto.IStatus,
        LotNo = dto.LotNo,
        ExpiryDate = dto.ExpiryDate,
        BalLocDisplay = dto.LotNo,
        StdUom = dto.StdUom
    };
}
