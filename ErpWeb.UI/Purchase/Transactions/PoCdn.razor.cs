using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using ErpWeb.UI.Services;
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
    protected bool CandidateLinePickerVisible;
    protected string? PopupError;
    protected string? InvoicePickerError;
    protected string? InvoicePickerSearchText;
    protected string? PendingSourceInvNo;
    protected PoCdnInvoicePickerRow? CandidateInvoice;
    protected bool SourceTransitionInProgress;
    protected bool SourceConfirmVisible;
    protected string? SourceConfirmMessage;
    protected string? CandidateImportError;
    protected int CandidateSkippedCount;
    protected IReadOnlyList<object> SelectedCandidateLines { get; set; } = [];
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
    protected string? Dept;
    protected string? ProjId;
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
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<PoCdnTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected List<IvCodeLookupRow> Departments { get; set; } = [];
    protected List<IvCodeLookupRow> Projects { get; set; } = [];
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
    private int _sourceGeneration;
    private string? _appliedQueryInvNo;
    private PoCdnDraftSnapshot? _sourceConfirmRestore;
    private Func<Task>? _sourceConfirmCommit;
    private Action? _sourceConfirmCancel;

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
    protected bool HasPendingSourceDifference =>
        !string.Equals(NormalizeSource(PendingSourceInvNo), NormalizeSource(InvNo), StringComparison.OrdinalIgnoreCase);
    protected bool SourceUnresolved =>
        SourceTransitionInProgress
        || SourceConfirmVisible
        || InvoicePickerVisible
        || LinePickerVisible
        || CandidateLinePickerVisible
        || HasPendingSourceDifference;
    protected bool CanFindSource => CanEditDocument && HasVendor && !IsSubmitting;
    protected bool CanAddFromInvoice => CanMutateLines;
    protected bool CanPostCommand => CanPostPermission && !IsSubmitting && !SourceUnresolved;

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

    protected bool CanMutateLines => CanEditDocument && HasVendor && !IsSubmitting && !SourceTransitionInProgress;

    protected bool CanSave =>
        CanEditDocument
        && !IsSubmitting
        && Lines.Count > 0
        && HasVendor
        && !string.IsNullOrWhiteSpace(ReasonCode)
        && !SourceUnresolved;

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
        // The family is part of this page's identity: ONE component answers BOTH the credit-note and the
        // debit-note route, so `new` -> `new` across families must NOT look like the same page.
        var key = $"{ResolveFamily()}:{Mode}:{DocNo}";
        var incomingInv = NormalizeSource(InvoiceFromQuery);
        if (!string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            _loadedKey = key;
            await LoadAsync();
            return;
        }

        if (IsNewMode
            && !string.Equals(incomingInv, NormalizeSource(_appliedQueryInvNo), StringComparison.OrdinalIgnoreCase))
        {
            await HandleIncomingSourceQueryAsync(incomingInv);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceGeneration++;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void ResolveType() => _type = ResolveFamily();

    /// <summary>
    /// The family the URL names. PURE and side-effect free: it is called both BEFORE the reuse-key
    /// comparison (so a credit-note -> debit-note switch is detected) and while loading.
    /// </summary>
    private string ResolveFamily() =>
        Navigation.Uri.Contains("/debit-notes", StringComparison.OrdinalIgnoreCase)
            ? PoCdnTypes.DebitNote
            : PoCdnTypes.CreditNote;

    private async Task LoadAsync()
    {
        ResolveType();
        _sourceGeneration++;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var lookups = await Cdns.GetLookupsAsync(_type, _cts.Token);
            if (lookups.Succeeded)
            {
                Warehouses = lookups.Warehouses.ToList();
                TaxGroups = lookups.TaxGroups.ToList();
                PayCodes = lookups.PayCodes.ToList();
                Departments = lookups.Departments.ToList();
                Projects = lookups.Projects.ToList();
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
                _appliedQueryInvNo = NormalizeSource(InvoiceFromQuery);

                if (!string.IsNullOrWhiteSpace(InvoiceFromQuery))
                {
                    PendingSourceInvNo = InvoiceFromQuery.Trim();
                    await TryApplyExactSourceAsync(PendingSourceInvNo, requireConfirm: false);
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
        Dept = null;
        ProjId = null;
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
        PendingSourceInvNo = null;
        CandidateInvoice = null;
        SourceTransitionInProgress = false;
        SourceConfirmVisible = false;
        InvoicePickerVisible = false;
        LinePickerVisible = false;
        CandidateLinePickerVisible = false;
        SelectedCandidateLines = [];
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
        Dept = doc.Dept;
        ProjId = doc.ProjId;
        TaxGrCode = doc.TaxGrCode;
        ReasonCode = doc.ReasonCode;
        InvNo = doc.InvNo;
        PendingSourceInvNo = doc.InvNo;
        CandidateInvoice = null;
        SourceTransitionInProgress = false;
        SourceConfirmVisible = false;
        InvoicePickerVisible = false;
        LinePickerVisible = false;
        CandidateLinePickerVisible = false;
        SelectedCandidateLines = [];
        _appliedQueryInvNo = null;
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

    protected Task OnVendorSelectedAsync(PoSupplierLookupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ChangeVendorWithSourceGuardAsync(row.SuppCode);
    }

    protected Task OnVendorClearedAsync() => ChangeVendorWithSourceGuardAsync(null);

    protected async Task OnVendorChanged(string? vendorCode)
    {
        VendorCode = vendorCode;
        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            VendorName = null;
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

    protected void OnPendingSourceTextChanged()
    {
    }

    protected Task ApplyPendingSourceAsync() =>
        TryApplyExactSourceAsync(PendingSourceInvNo, requireConfirm: true);

    protected void CancelPendingSource()
    {
        PendingSourceInvNo = InvNo;
        ValidationErrors.Remove("InvNo");
        ErrorMessage = null;
    }

    // ── Source invoice line picker (C24 / C34) ───────────────────────────────

    /// <summary>
    /// Lists the invoice's lines with their remaining ceilings already computed by the service, so
    /// the operator picks from what is actually still creditable rather than from the raw invoice.
    /// </summary>
    protected async Task OpenLinePickerAsync()
    {
        if (string.IsNullOrWhiteSpace(InvNo) || string.IsNullOrWhiteSpace(VendorCode))
        {
            ErrorMessage = "Select or enter an invoice reference before picking a source line.";
            return;
        }

        LinePickerVisible = true;
        LinePickerLoading = true;
        LinePickerError = null;
        var token = ++_sourceGeneration;
        SourceTransitionInProgress = true;
        try
        {
            var result = await Cdns.GetInvoiceLinesAsync(
                _type,
                InvNo!,
                UsageExcludeDocNo(InvNo),
                VendorCode!,
                _cts.Token);
            if (token != _sourceGeneration)
            {
                return;
            }

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
            if (token == _sourceGeneration)
            {
                LinePickerLoading = false;
                SourceTransitionInProgress = false;
            }
        }
    }

    /// <summary>
    /// Seeds the popup from a chosen invoice line. Sets C24 traceability and the PO link that a
    /// stock-return line requires, so the operator does not have to find the PO by hand.
    /// </summary>
    protected void UseInvoiceLine(PoCdnInvoiceLinePickerRow row)
    {
        if (row is null) return;

        var mapped = PoCdnInvoiceLineMapper.TryMap(row);
        if (!mapped.IsSupported || mapped.Line is null)
        {
            LinePickerError = mapped.BlockReason ?? "This invoice line cannot be imported.";
            return;
        }

        var source = mapped.Line;
        Popup.InvLineNo = source.InvLineNo;
        Popup.ICode = source.ICode;
        Popup.IDesc = source.IDesc;
        Popup.Qty = source.Qty;
        Popup.UnitPrice = source.UnitPrice;
        Popup.ItemDiscount = source.ItemDiscount;
        Popup.ItemDiscount1 = source.ItemDiscount1;
        Popup.IDiscountType = source.IDiscountType;
        Popup.IDiscountType1 = source.IDiscountType1;
        Popup.TaxGroup = source.TaxGroup;
        Popup.IsInclusive = source.IsInclusive;
        Popup.ItemGlCode = source.ItemGlCode;
        Popup.Classification = source.Classification;
        Popup.StdCustPSize = source.StdCustPSize;
        Popup.PoNo = source.PoNo;
        Popup.PoRelNo = source.PoRelNo;
        Popup.PoLineNo = source.PoLineNo;
        Popup.StdUom = source.StdUom;
        Popup.CostPrice = 0m;
        Popup.IsStockReturn = false;
        Popup.FromBalLocId = null;
        Popup.FrWarehouse = null;
        Popup.LocCode = null;
        Popup.IStatus = null;
        Popup.LotNo = null;
        Popup.ExpiryDate = null;
        Popup.BalLocDisplay = null;
        Popup.AvailableQty = 0m;
        Popup.LotControl = false;
        Popup.LineRemainingStdQty = IsCreditNote ? source.LineRemainingStdQty : null;
        Popup.LineRemainingAmount = IsCreditNote ? source.LineRemainingAmount : null;

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
        ReservationIndicator = IsCreditNote
            ? $"Invoice {summary.InvNo}: total {summary.InvoiceTotal:n2} · "
              + $"posted {summary.PostedCnTotal:n2} · draft {summary.DraftCnTotal:n2} · "
              + $"remaining {summary.Remaining:n2} (hint only; Save remains authoritative)."
            : $"Linked to internal PI {summary.InvNo}. Credit-note remaining is not a debit-note limit.";
    }

    protected void OnReasonCodeChanged(string? code) => ReasonCode = code;

    // ── Invoice picker ───────────────────────────────────────────────────────

    protected Task OpenAddFromInvoiceAsync() =>
        HasInvoiceReference ? OpenCandidateLinePickerAsync(ConfirmedPickerRow()) : OpenInvoicePickerAsync();

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
        var token = ++_sourceGeneration;
        SourceTransitionInProgress = true;
        try
        {
            var result = await Cdns.SearchPostedInvoicesAsync(
                _type, VendorCode!, InvoicePickerSearchText, _cts.Token);
            if (token != _sourceGeneration)
            {
                return;
            }

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
            if (token == _sourceGeneration)
            {
                InvoicePickerLoading = false;
                SourceTransitionInProgress = false;
            }
        }
    }

    protected void OnInvoicePickerSearchTextChanged(string? value) =>
        InvoicePickerSearchText = value;

    protected Task SearchInvoicePickerAsync() => OpenInvoicePickerAsync();

    protected void CloseInvoicePicker()
    {
        InvoicePickerVisible = false;
        InvoicePickerError = null;
        CandidateInvoice = null;
    }

    protected async Task SelectInvoiceItemsAsync(PoCdnInvoicePickerRow row)
    {
        if (row is null) return;
        CandidateInvoice = row;
        InvoicePickerVisible = false;
        await OpenCandidateLinePickerAsync(row);
    }

    protected Task UseInvoiceReferenceOnlyAsync(PoCdnInvoicePickerRow row)
    {
        if (row is null) return Task.CompletedTask;
        CandidateInvoice = row;
        var sameSource = string.Equals(
            NormalizeSource(row.InvNo), NormalizeSource(InvNo), StringComparison.OrdinalIgnoreCase);
        if (!sameSource && IsDraftDirty)
        {
            return AskSourceConfirmAsync(
                $"Set the invoice reference to {row.InvNo} without changing lines?",
                () => CommitCandidateAsync(row, mappedLines: null, replaceLines: false, skipCount: 0),
                restore: null);
        }

        return CommitCandidateAsync(row, mappedLines: null, replaceLines: false, skipCount: 0);
    }

    protected async Task OpenCandidateLinePickerAsync(PoCdnInvoicePickerRow? source)
    {
        source ??= CandidateInvoice ?? ConfirmedPickerRow();
        if (source is null || string.IsNullOrWhiteSpace(source.InvNo) || string.IsNullOrWhiteSpace(source.VendorCode))
        {
            ErrorMessage = "Select a purchase invoice before importing items.";
            return;
        }

        CandidateInvoice = source;
        CandidateLinePickerVisible = true;
        LinePickerLoading = true;
        CandidateImportError = null;
        CandidateSkippedCount = 0;
        SelectedCandidateLines = [];
        var token = ++_sourceGeneration;
        SourceTransitionInProgress = true;
        try
        {
            var result = await Cdns.GetInvoiceLinesAsync(
                _type,
                source.InvNo,
                UsageExcludeDocNo(source.InvNo),
                source.VendorCode,
                _cts.Token);
            if (token != _sourceGeneration)
            {
                return;
            }

            if (!result.Succeeded)
            {
                CandidateImportError = result.ErrorMessage ?? "Unable to load the invoice lines.";
                InvoiceLineRows = [];
                return;
            }

            InvoiceLineRows = result.InvoiceLinePickerRows.ToList();
        }
        finally
        {
            if (token == _sourceGeneration)
            {
                LinePickerLoading = false;
                SourceTransitionInProgress = false;
            }
        }
    }

    protected void CloseCandidateLinePicker()
    {
        CandidateLinePickerVisible = false;
        CandidateImportError = null;
        CandidateInvoice = null;
        SelectedCandidateLines = [];
        InvoiceLineRows = [];
    }

    protected void OnCandidateLinePickerVisibleChanged(bool visible)
    {
        if (visible)
        {
            CandidateLinePickerVisible = true;
            return;
        }

        CloseCandidateLinePicker();
    }

    protected void OnSourceConfirmVisibleChanged(bool visible)
    {
        if (visible)
        {
            SourceConfirmVisible = true;
            return;
        }

        if (SourceConfirmVisible)
        {
            CancelSourceChange();
        }
    }

    protected void ClosePopupLinePicker()
    {
        LinePickerVisible = false;
        LinePickerError = null;
    }

    protected void OnSelectedCandidateLinesChanged(IReadOnlyList<object> items) =>
        SelectedCandidateLines = items;

    protected Task AddSelectedCandidateLinesAsync() =>
        ImportCandidateLinesAsync(SelectedCandidateRows(), copyAll: false);

    protected Task CopyAllCandidateLinesAsync() =>
        ImportCandidateLinesAsync(InvoiceLineRows, copyAll: true);

    private IReadOnlyList<PoCdnInvoiceLinePickerRow> SelectedCandidateRows() =>
        SelectedCandidateLines.OfType<PoCdnInvoiceLinePickerRow>().ToList();

    private async Task ImportCandidateLinesAsync(
        IReadOnlyList<PoCdnInvoiceLinePickerRow> rows,
        bool copyAll)
    {
        var source = CandidateInvoice ?? ConfirmedPickerRow();
        if (source is null)
        {
            CandidateImportError = "Select a purchase invoice before importing items.";
            return;
        }

        if (rows.Count == 0)
        {
            CandidateImportError = copyAll
                ? "This invoice has no importable lines."
                : "Select at least one invoice line.";
            return;
        }

        var mapped = PoCdnInvoiceLineMapper.MapSelection(rows);
        if (!mapped.IsSupported)
        {
            CandidateImportError = mapped.ErrorMessage
                ?? "One or more selected lines cannot be imported. Deselect them and retry.";
            return;
        }

        var sameSource = string.Equals(
            NormalizeSource(source.InvNo), NormalizeSource(InvNo), StringComparison.OrdinalIgnoreCase);
        var existingKeys = sameSource
            ? Lines.Where(x => x.InvLineNo is not null).Select(x => x.InvLineNo!.Value).ToHashSet()
            : [];
        var accepted = mapped.Lines.Where(x => x.InvLineNo is null || !existingKeys.Contains(x.InvLineNo.Value)).ToList();
        var skipped = mapped.Lines.Count - accepted.Count;
        if (accepted.Count == 0)
        {
            CandidateImportError = skipped > 0
                ? $"Skipped {skipped} line(s) already on this note."
                : "No lines to import.";
            CandidateSkippedCount = skipped;
            return;
        }

        var replace = !sameSource;
        if (replace && IsDraftDirty)
        {
            await AskSourceConfirmAsync(
                $"Apply purchase invoice {source.InvNo} and replace the current lines with {accepted.Count} selected item(s)? Stock piles on the old source will be cleared.",
                () => CommitCandidateAsync(source, accepted, replaceLines: true, skipped),
                restore: null);
            return;
        }

        if (copyAll && sameSource && Lines.Count > 0 && skipped == 0 && accepted.Count > 0)
        {
            await AskSourceConfirmAsync(
                $"Copy all remaining lines from {source.InvNo}? {accepted.Count} line(s) will be added.",
                () => CommitCandidateAsync(source, accepted, replaceLines: false, skipped),
                restore: null);
            return;
        }

        await CommitCandidateAsync(source, accepted, replaceLines: replace, skipped);
    }

    private async Task<bool> TryApplyExactSourceAsync(string? typed, bool requireConfirm)
    {
        var no = NormalizeSource(typed);
        if (no.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(InvNo))
            {
                PendingSourceInvNo = null;
                return true;
            }

            if (!requireConfirm)
            {
                return false;
            }

            await AskSourceConfirmAsync(
                "Clear the invoice reference? Imported line identities will be kept until you remove them.",
                () => CommitClearedSourceAsync(),
                restore: null);
            return false;
        }

        if (string.Equals(no, NormalizeSource(InvNo), StringComparison.OrdinalIgnoreCase)
            && !SourceTransitionInProgress)
        {
            PendingSourceInvNo = InvNo;
            return true;
        }

        var token = ++_sourceGeneration;
        SourceTransitionInProgress = true;
        try
        {
            var expectedVendor = string.IsNullOrWhiteSpace(VendorCode) ? null : VendorCode;
            var result = await Cdns.GetPostedInvoiceSourceAsync(_type, no, expectedVendor, _cts.Token);
            if (token != _sourceGeneration)
            {
                return false;
            }

            if (!result.Succeeded || result.PostedInvoiceSource is null)
            {
                ValidationErrors["InvNo"] = result.ErrorMessage ?? "A posted purchase invoice is required.";
                ErrorMessage = ValidationErrors["InvNo"];
                return false;
            }

            var source = result.PostedInvoiceSource;
            if (requireConfirm && (IsDraftDirty || (!IsNewMode && !string.IsNullOrWhiteSpace(InvNo))))
            {
                SourceTransitionInProgress = false;
                await AskSourceConfirmAsync(
                    $"Apply purchase invoice {source.InvNo} as the source of this note?",
                    () => CommitCandidateAsync(source, mappedLines: null, replaceLines: false, skipCount: 0),
                    restore: null);
                return false;
            }

            return await CommitCandidateAsync(source, mappedLines: null, replaceLines: false, skipCount: 0, token);
        }
        finally
        {
            if (token == _sourceGeneration)
            {
                SourceTransitionInProgress = false;
            }
        }
    }

    private async Task<bool> CommitCandidateAsync(
        PoCdnInvoicePickerRow source,
        IReadOnlyList<PoCdnLineVm>? mappedLines,
        bool replaceLines,
        int skipCount,
        int? existingToken = null)
    {
        var token = existingToken ?? ++_sourceGeneration;
        var snapshot = CaptureDraft();
        SourceTransitionInProgress = true;
        try
        {
            var vendorMatches = string.Equals(
                VendorCode, source.VendorCode, StringComparison.OrdinalIgnoreCase);
            var isExistingEdit = !IsNewMode && !string.IsNullOrWhiteSpace(DocNo);

            InvNo = source.InvNo;
            PendingSourceInvNo = source.InvNo;

            if (!vendorMatches)
            {
                if (isExistingEdit && HasUnrelatedHeaderFields)
                {
                    // Keep user-specified header fields; OnVendorChanged only fills blanks / names.
                }

                await OnVendorChanged(source.VendorCode);
                if (token != _sourceGeneration)
                {
                    RestoreDraft(snapshot);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(ErrorMessage))
                {
                    RestoreDraft(snapshot);
                    PendingSourceInvNo = source.InvNo;
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(source.Currency))
            {
                Currency = source.Currency;
            }

            await RefreshFxAsync();
            if (token != _sourceGeneration)
            {
                RestoreDraft(snapshot);
                return false;
            }

            if (!CurrRateValid)
            {
                var fxError = ValidationErrors.GetValueOrDefault("Currency") ?? "Valid FX rate is required.";
                RestoreDraft(snapshot);
                PendingSourceInvNo = source.InvNo;
                ValidationErrors["InvNo"] = fxError;
                ErrorMessage = fxError;
                return false;
            }

            if (!IsCreditNote)
            {
                ReturnStock = false;
            }

            if (mappedLines is not null)
            {
                if (replaceLines)
                {
                    Lines = mappedLines.Select(x => x.Clone()).ToList();
                }
                else
                {
                    Lines.AddRange(mappedLines.Select(x => x.Clone()));
                }

                Renumber();
                RecalcDocument();
            }

            InvoicePickerVisible = false;
            CandidateLinePickerVisible = false;
            LinePickerVisible = false;
            SourceConfirmVisible = false;
            CandidateInvoice = null;
            SelectedCandidateLines = [];
            CandidateSkippedCount = skipCount;
            _appliedQueryInvNo = IsNewMode ? source.InvNo : _appliedQueryInvNo;
            ValidationErrors.Remove("InvNo");
            await RefreshReservationIndicatorAsync();
            StatusMessage = mappedLines is null
                ? $"Invoice reference set to {source.InvNo}. Lines were not changed."
                : skipCount > 0
                    ? $"Imported {mappedLines.Count} line(s) from {source.InvNo}. Skipped {skipCount} duplicate(s)."
                    : $"Imported {mappedLines.Count} line(s) from {source.InvNo}. Review and save to confirm.";
            return true;
        }
        catch (OperationCanceledException)
        {
            RestoreDraft(snapshot);
            return false;
        }
        catch
        {
            RestoreDraft(snapshot);
            throw;
        }
        finally
        {
            if (token == _sourceGeneration)
            {
                SourceTransitionInProgress = false;
            }
        }
    }

    private Task CommitClearedSourceAsync()
    {
        InvNo = null;
        PendingSourceInvNo = null;
        ReservationIndicator = null;
        CandidateInvoice = null;
        StatusMessage = "Invoice reference cleared.";
        return Task.CompletedTask;
    }

    private async Task HandleIncomingSourceQueryAsync(string incoming)
    {
        var restoreUrl = CurrentNewRoute(_appliedQueryInvNo);
        async Task Commit()
        {
            PendingSourceInvNo = incoming;
            _appliedQueryInvNo = incoming;
            await TryApplyExactSourceAsync(incoming, requireConfirm: false);
        }

        if (IsDraftDirty || HasInvoiceReference)
        {
            await AskSourceConfirmAsync(
                string.IsNullOrWhiteSpace(incoming)
                    ? "Remove the incoming invoice link and keep the current draft?"
                    : $"Open purchase invoice {incoming} as the source? The current draft stays until you confirm.",
                Commit,
                () => Navigation.NavigateTo(restoreUrl, replace: true));
            return;
        }

        await Commit();
    }

    private async Task ChangeVendorWithSourceGuardAsync(string? vendorCode)
    {
        if (string.Equals(VendorCode, vendorCode, StringComparison.OrdinalIgnoreCase)
            && !(string.IsNullOrWhiteSpace(VendorCode) && string.IsNullOrWhiteSpace(vendorCode)))
        {
            return;
        }

        if (!HasInvoiceReference && Lines.Count == 0 && !PopupVisible)
        {
            await OnVendorChanged(vendorCode);
            return;
        }

        await AskSourceConfirmAsync(
            "Changing the vendor will clear the invoice reference. Imported lines stay until you remove them. Continue?",
            async () =>
            {
                InvNo = null;
                PendingSourceInvNo = null;
                ReservationIndicator = null;
                CandidateInvoice = null;
                await OnVendorChanged(vendorCode);
            },
            restore: null);
    }

    private Task AskSourceConfirmAsync(string message, Func<Task> commit, Action? restore)
    {
        _sourceConfirmRestore = CaptureDraft();
        _sourceConfirmCommit = commit;
        _sourceConfirmCancel = restore;
        SourceConfirmMessage = message;
        SourceConfirmVisible = true;
        return Task.CompletedTask;
    }

    protected async Task ConfirmSourceChangeAsync()
    {
        var commit = _sourceConfirmCommit;
        SourceConfirmVisible = false;
        _sourceConfirmCommit = null;
        _sourceConfirmCancel = null;
        if (commit is not null)
        {
            await commit();
        }
    }

    protected void CancelSourceChange()
    {
        var restore = _sourceConfirmCancel;
        var snapshot = _sourceConfirmRestore;
        SourceConfirmVisible = false;
        _sourceConfirmCommit = null;
        _sourceConfirmCancel = null;
        _sourceConfirmRestore = null;
        snapshot?.Restore(this);
        restore?.Invoke();
    }

    private PoCdnInvoicePickerRow? ConfirmedPickerRow() =>
        string.IsNullOrWhiteSpace(InvNo) || string.IsNullOrWhiteSpace(VendorCode)
            ? null
            : new PoCdnInvoicePickerRow
            {
                InvNo = InvNo!,
                InvDate = DocDate,
                VendorCode = VendorCode!,
                VendorName = VendorName,
                Currency = Currency,
                TotAmnt = TotAmnt
            };

    private string? UsageExcludeDocNo(string? candidateInvNo)
    {
        if (!IsCreditNote || IsNewMode || string.IsNullOrWhiteSpace(DocNo))
        {
            return null;
        }

        if (!string.Equals(NormalizeSource(candidateInvNo), NormalizeSource(InvNo), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DocNo;
    }

    private string CurrentNewRoute(string? invNo)
    {
        var path = IsCreditNote ? "/purchase/credit-notes/new" : "/purchase/debit-notes/new";
        return string.IsNullOrWhiteSpace(invNo)
            ? path
            : $"{path}?invNo={Uri.EscapeDataString(invNo)}";
    }

    private static string NormalizeSource(string? value) => (value ?? string.Empty).Trim();

    private bool IsDraftDirty =>
        Lines.Count > 0
        || PopupVisible
        || !string.IsNullOrWhiteSpace(ReasonCode)
        || !string.IsNullOrWhiteSpace(SupplierDocNo)
        || SupplierDocDate is not null
        || !string.IsNullOrWhiteSpace(Remarks)
        || !string.IsNullOrWhiteSpace(ExternalDocNo)
        || ReturnStock
        || Lines.Any(x => x.FromBalLocId is not null);

    private bool HasUnrelatedHeaderFields =>
        !string.IsNullOrWhiteSpace(PayCode)
        || !string.IsNullOrWhiteSpace(Dept)
        || !string.IsNullOrWhiteSpace(ProjId)
        || !string.IsNullOrWhiteSpace(ReasonCode)
        || !string.IsNullOrWhiteSpace(SupplierDocNo)
        || !string.IsNullOrWhiteSpace(Remarks);

    private PoCdnDraftSnapshot CaptureDraft() => PoCdnDraftSnapshot.Capture(this);

    private void RestoreDraft(PoCdnDraftSnapshot snapshot) => snapshot.Restore(this);

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

    protected void OnPopupItemCleared()
    {
        Popup.ICode = null;
        Popup.IDesc = null;
    }

    protected void OnPopupItemSelected(IvStockMasterLookupRow item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Popup.ICode = item.ICode;
        Popup.IDesc = item.IDesc;
        Popup.UnitPrice = item.PurchasePrice ?? Popup.UnitPrice;
        Popup.TaxGroup ??= item.PurchaseTaxGroup ?? item.TaxGroup;
        Popup.ItemGlCode = item.PurchaseGlCode;
        Popup.Classification = item.Classification;
        Popup.StockControl = item.StockControl;
        var pack = PoOrderCalc.EffectivePackSize(item.PurStdPackSize ?? item.StdPackSize ?? 1m);
        Popup.StdCustPSize = PoCdnCalc.IsUsablePackSize(pack) ? pack : 1m;
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
        Dept = Dept,
        ProjId = ProjId,
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

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();

        // MainLayout's NavigationLock cancels internal navigation while PageNavigationGuard is
        // blocking, so the scope opened below must end before we navigate.
        string? navigateTo = null;
        try
        {
            using var blocking = BeginBlockingWork("Please wait. Saving is still running.");

            RecalcDocument();

            if (IsNewMode)
            {
                var created = await Cdns.SaveNewAsync(ToRequest(), _cts.Token);
                if (!HandleOperationResult(created, stayOnPage: false)) return;

                navigateTo = ListRoute;
            }
            else
            {
                var updated = await Cdns.UpdateAsync(DocNo!, ToRequest(), _cts.Token);
                if (!HandleOperationResult(updated, stayOnPage: true)) return;

                navigateTo = ListRoute;
            }
        }
        finally
        {
            IsSubmitting = false;
        }

        if (!string.IsNullOrWhiteSpace(navigateTo))
        {
            Navigation.NavigateTo(navigateTo);
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

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        // MainLayout's NavigationLock cancels internal navigation while PageNavigationGuard is
        // blocking, so the scope opened below must end before we navigate.
        string? navigateTo = null;
        try
        {
            using var blocking = BeginBlockingWork("Please wait. Delete is still running.");

            var result = await Cdns.DeleteAsync(
                [new PoCdnKeyedRequest { DocNo = DocNo!, RowVersion = _rowVersion }], _cts.Token);

            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to delete the document.";
                await ReloadCurrentAsync();
                return;
            }

            navigateTo = ListRoute;
        }
        finally
        {
            IsSubmitting = false;
        }

        if (!string.IsNullOrWhiteSpace(navigateTo))
        {
            Navigation.NavigateTo(navigateTo);
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

    protected void OnClose() => DocumentReturnNavigation.NavigateBack(Navigation, ListRoute);

    protected void OnEditFromView() => Navigation.NavigateTo(
        DocumentReturnNavigation.PreserveReturnUrl(Navigation.Uri, EditRouteFor(DocNo!)));

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private bool HandleOperationResult(PoCdnOperationResult result, bool stayOnPage)
    {
        if (result.Succeeded)
        {
            return true;
        }

        // A previous attempt's field errors must never linger next to a different failure kind.
        if (result.ErrorKind != PoCdnErrorKind.Validation)
        {
            ValidationErrors.Clear();
        }

        switch (result.ErrorKind)
        {
            case PoCdnErrorKind.Validation:
                ValidationErrors = new Dictionary<string, string>(
                    result.ValidationErrors, StringComparer.OrdinalIgnoreCase);
                ErrorMessage = BuildValidationMessage(ValidationErrors, result.ErrorMessage);
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

    private sealed class PoCdnDraftSnapshot
    {
        public required string? InvNo { get; init; }
        public required string? PendingSourceInvNo { get; init; }
        public required string? VendorCode { get; init; }
        public required string? VendorName { get; init; }
        public required string? VendorBlockedMessage { get; init; }
        public required string Currency { get; init; }
        public required decimal CurrRate { get; init; }
        public required bool CurrRateValid { get; init; }
        public required string? PayCode { get; init; }
        public required string? Dept { get; init; }
        public required string? ProjId { get; init; }
        public required string? TaxGrCode { get; init; }
        public required string? ReasonCode { get; init; }
        public required string? SupplierDocNo { get; init; }
        public required DateTime? SupplierDocDate { get; init; }
        public required bool ReturnStock { get; init; }
        public required string? ExternalDocNo { get; init; }
        public required string? Remarks { get; init; }
        public required string? ReservationIndicator { get; init; }
        public required List<PoCdnLineVm> Lines { get; init; }
        public required PoCdnLineVm Popup { get; init; }
        public required bool PopupVisible { get; init; }
        public required bool InvoicePickerVisible { get; init; }
        public required bool LinePickerVisible { get; init; }
        public required bool CandidateLinePickerVisible { get; init; }
        public required string? ErrorMessage { get; init; }
        public required Dictionary<string, string> ValidationErrors { get; init; }

        public static PoCdnDraftSnapshot Capture(PoCdn page) => new()
        {
            InvNo = page.InvNo,
            PendingSourceInvNo = page.PendingSourceInvNo,
            VendorCode = page.VendorCode,
            VendorName = page.VendorName,
            VendorBlockedMessage = page.VendorBlockedMessage,
            Currency = page.Currency,
            CurrRate = page.CurrRate,
            CurrRateValid = page.CurrRateValid,
            PayCode = page.PayCode,
            Dept = page.Dept,
            ProjId = page.ProjId,
            TaxGrCode = page.TaxGrCode,
            ReasonCode = page.ReasonCode,
            SupplierDocNo = page.SupplierDocNo,
            SupplierDocDate = page.SupplierDocDate,
            ReturnStock = page.ReturnStock,
            ExternalDocNo = page.ExternalDocNo,
            Remarks = page.Remarks,
            ReservationIndicator = page.ReservationIndicator,
            Lines = page.Lines.Select(x => x.Clone()).ToList(),
            Popup = page.Popup.Clone(),
            PopupVisible = page.PopupVisible,
            InvoicePickerVisible = page.InvoicePickerVisible,
            LinePickerVisible = page.LinePickerVisible,
            CandidateLinePickerVisible = page.CandidateLinePickerVisible,
            ErrorMessage = page.ErrorMessage,
            ValidationErrors = new Dictionary<string, string>(page.ValidationErrors, StringComparer.OrdinalIgnoreCase)
        };

        public void Restore(PoCdn page)
        {
            page.InvNo = InvNo;
            page.PendingSourceInvNo = PendingSourceInvNo;
            page.VendorCode = VendorCode;
            page.VendorName = VendorName;
            page.VendorBlockedMessage = VendorBlockedMessage;
            page.Currency = Currency;
            page.CurrRate = CurrRate;
            page.CurrRateValid = CurrRateValid;
            page.PayCode = PayCode;
            page.Dept = Dept;
            page.ProjId = ProjId;
            page.TaxGrCode = TaxGrCode;
            page.ReasonCode = ReasonCode;
            page.SupplierDocNo = SupplierDocNo;
            page.SupplierDocDate = SupplierDocDate;
            page.ReturnStock = ReturnStock;
            page.ExternalDocNo = ExternalDocNo;
            page.Remarks = Remarks;
            page.ReservationIndicator = ReservationIndicator;
            page.Lines = Lines.Select(x => x.Clone()).ToList();
            page.Popup = Popup.Clone();
            page.PopupVisible = PopupVisible;
            page.InvoicePickerVisible = InvoicePickerVisible;
            page.LinePickerVisible = LinePickerVisible;
            page.CandidateLinePickerVisible = CandidateLinePickerVisible;
            page.ErrorMessage = ErrorMessage;
            page.ValidationErrors = new Dictionary<string, string>(ValidationErrors, StringComparer.OrdinalIgnoreCase);
            page.CandidateInvoice = null;
            page.SourceConfirmVisible = false;
            page.RecalcDocument();
        }
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
