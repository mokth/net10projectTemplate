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

public partial class PoPr : PageBase, IAsyncDisposable
{
    [Parameter] public string Mode { get; set; } = string.Empty;
    [Parameter] public string? PrNo { get; set; }

    [Inject] private IPoPrService Prs { get; set; } = default!;
    [Inject] private IPoPrAttachmentService Attachments { get; set; } = default!;
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
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected string? PopupError;
    protected bool CanEditPermission;
    protected bool CanViewCost;
    protected string PrNoDisplay = "AUTO";
    protected string StatusDisplay = PoPrStatuses.New;
    protected DateTime CreateDt;
    protected string? Requester;
    protected string? DeptCode;
    protected string? PrType = PoPrTypes.Purchasing;
    protected string? ProjId;
    protected string? LocationCode;
    protected string? CurrencyDisplay;
    protected string? CheckedBy;
    protected string? AuthorisedBy;
    protected string? AuthorisedBy2nd;
    protected string? Remarks;
    protected string? TempDocId;
    protected decimal Gross;
    protected decimal Taxes;
    protected decimal Total;
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    protected ElementReference AttachmentInputRef;
    protected string? SelectedAttachmentName;

    private PoPrLineVm? _editingLine;
    private string? _loadedKey;
    private bool _isDirty;
    private bool _disposed;
    private byte[] _rowVersion = [];
    private string? _antiforgeryToken;
    private IJSObjectReference? _attachmentModule;
    private CancellationTokenSource _cts = new();
    private bool _defaultInclusive;
    private int _taxDecimals = 2;

    protected List<PoPrLineVm> Lines { get; set; } = [];
    protected List<PoPrAttachmentRow> AttachmentRows { get; set; } = [];
    protected List<PoPrItemLookupRow> Items { get; set; } = [];
    protected List<PoPrVendorLookupRow> Vendors { get; set; } = [];
    protected List<PoPrTaxGroupLookupRow> TaxGroups { get; set; } = [];
    protected List<PoPrCodeLookupRow> Currencies { get; set; } = [];
    protected List<PoPrCodeLookupRow> BuyingTerms { get; set; } = [];
    protected List<PoPrCodeLookupRow> PaymentTerms { get; set; } = [];
    protected List<PoPrCodeLookupRow> AuthorisedPersons { get; set; } = [];
    protected List<IvWarehouseLookupRow> Warehouses { get; set; } = [];
    protected List<PoPrCodeLookupRow> Categories { get; set; } = [];
    protected List<PoPrCodeLookupRow> Departments { get; set; } = [];
    protected List<PoPrCodeLookupRow> Projects { get; set; } = [];
    protected PoPrLineVm Popup { get; set; } = new();

    protected IReadOnlyList<PoPrCodeLookupRow> PrTypeOptions { get; } =
    [
        new() { Code = PoPrTypes.Purchasing, Name = "Purchasing" },
        new() { Code = PoPrTypes.Repair, Name = "Repair" }
    ];

    protected IReadOnlyList<PoPrCodeLookupRow> RepairTypeOptions { get; } =
    [
        new() { Code = PoPrRepairTypes.Internal, Name = "Internal" },
        new() { Code = PoPrRepairTypes.External, Name = "External" }
    ];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsCopyMode => string.Equals(Mode, "copy", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsCancelledStatus => string.Equals(StatusDisplay, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
    protected bool IsApprovedStatus => string.Equals(StatusDisplay, PoPrStatuses.Approved, StringComparison.OrdinalIgnoreCase);
    protected bool IsReadOnlyPresentation => IsViewMode || IsCancelledStatus || IsApprovedStatus;
    protected bool CanEditDocument => (IsNewMode || IsEditMode || IsCopyMode) && !IsViewMode && !IsCancelledStatus && !IsApprovedStatus;
    protected bool CanEditFromView =>
        IsViewMode
        && CanEditPermission
        && !IsCancelledStatus
        && !IsApprovedStatus
        && !string.IsNullOrWhiteSpace(PrNo);
    protected bool IsRepairType =>
        string.Equals(PrType, PoPrTypes.Repair, StringComparison.OrdinalIgnoreCase);
    protected string PageHeading =>
        IsNewMode ? "New purchase requisition"
        : IsEditMode ? "Edit purchase requisition"
        : IsCopyMode ? "Copy purchase requisition"
        : "View purchase requisition";
    protected string ModeChip =>
        IsNewMode ? "New" : IsEditMode ? "Edit" : IsCopyMode ? "Copy" : "View";
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count} lines";
    protected bool CanMutateLines => CanEditDocument && !IsSubmitting;
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0;
    protected bool IsEditingLine => _editingLine is not null;
    protected string PopupTitle => IsEditingLine ? "Edit line" : "Add line";
    protected string PopupPrimaryText => IsEditingLine ? "Update line" : "Add line";
    protected bool PopupInclusiveLocked =>
        Lines.Count > 1 || (_editingLine is null && Lines.Count > 0);
    protected bool PopupIsConsumed => _editingLine?.IsConsumed == true;
    protected bool PopupReadOnly => !CanEditDocument || PopupIsConsumed;
    protected string? AttachmentDocId =>
        !string.IsNullOrWhiteSpace(TempDocId) ? TempDocId
        : !string.IsNullOrWhiteSpace(PrNoDisplay) && !string.Equals(PrNoDisplay, "AUTO", StringComparison.OrdinalIgnoreCase)
            ? PrNoDisplay
            : null;
    protected bool CanManageAttachments =>
        CanEditDocument && !string.IsNullOrWhiteSpace(AttachmentDocId) && !IsSubmitting;
    protected bool AttachmentUploadDisabled => !CanManageAttachments || IsUploadingAttachment;
    protected (decimal Amount, decimal TaxAmount, decimal NetAmount) PopupCalc => BuildPopupCalc();

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var key = $"{Mode}:{PrNo}";
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
        ConcurrencyVisible = false;
        _isDirty = false;
        PopupVisible = false;
        SelectedAttachmentName = null;
        AttachmentRows = [];

        CanEditPermission = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Edit);
        var lookups = await Prs.GetLookupsAsync(_cts.Token);
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

        var result = await Prs.GetAsync(PrNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Purchase requisition was not found.";
            if (result.ErrorKind == PoPrErrorKind.NotFound)
            {
                Navigation.NavigateTo("/purchase/requisitions");
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
        var temp = await Prs.CreateTempDocIdAsync(_cts.Token);
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
        PrNoDisplay = "AUTO";
        StatusDisplay = PoPrStatuses.New;
        CreateDt = Dates.Today.Date;
        Requester = CurrentUser.UserId;
        DeptCode = null;
        PrType = PoPrTypes.Purchasing;
        ProjId = null;
        LocationCode = CurrentUser.LocationCode;
        CurrencyDisplay = null;
        CheckedBy = null;
        AuthorisedBy = null;
        AuthorisedBy2nd = null;
        Remarks = null;
        Gross = Taxes = Total = 0m;
        _rowVersion = [];
        Lines = [];
        AttachmentRows = [];
    }

    private async Task InitializeCopyAsync()
    {
        var copy = await Prs.CopyAsync(PrNo ?? string.Empty, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!copy.Succeeded || copy.Document is null)
        {
            ErrorMessage = copy.ErrorMessage ?? "Unable to copy purchase requisition.";
            return;
        }

        var temp = await Prs.CreateTempDocIdAsync(_cts.Token);
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
        StatusMessage = $"Copied from {PrNo}";
    }

    private void ApplyLookups(PoPrLookups lookups)
    {
        Items = lookups.DirectItems.Concat(lookups.IndirectItems)
            .OrderBy(x => x.ICode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Vendors = lookups.Vendors.ToList();
        TaxGroups = lookups.TaxGroups.ToList();
        Currencies = lookups.Currencies.ToList();
        BuyingTerms = lookups.BuyingTerms.ToList();
        PaymentTerms = lookups.PaymentTerms.ToList();
        AuthorisedPersons = lookups.AuthorisedPersons.ToList();
        Warehouses = lookups.Warehouses.ToList();
        Categories = lookups.Categories.ToList();
        Departments = lookups.Departments.ToList();
        Projects = lookups.Projects.ToList();
        CanViewCost = lookups.CanViewCost;
        _defaultInclusive = lookups.DefaultInclusive;
    }

    private void ApplyDocument(PoPrDocument doc)
    {
        PrNoDisplay = string.IsNullOrWhiteSpace(doc.PrNo) ? "AUTO" : doc.PrNo;
        StatusDisplay = doc.Status;
        CreateDt = doc.CreateDt.Date;
        Requester = doc.Requester;
        DeptCode = doc.DeptCode;
        PrType = doc.PrType ?? PoPrTypes.Purchasing;
        ProjId = doc.ProjId;
        LocationCode = doc.LocationCode ?? CurrentUser.LocationCode;
        CurrencyDisplay = doc.Currency;
        CheckedBy = doc.CheckedBy;
        AuthorisedBy = doc.AuthorisedBy;
        AuthorisedBy2nd = doc.AuthorisedBy2nd;
        Remarks = doc.Remarks;
        Gross = doc.Gross;
        Taxes = doc.Taxes;
        Total = doc.Total;
        _rowVersion = doc.RowVersion ?? [];
        Lines = doc.Lines.Select(PoPrLineVm.FromDto).ToList();
        RefreshCurrencyDisplay();
        RecalcDocument();
    }

    protected void MarkDirtyOnly() => MarkDirty();

    protected void OnHeaderChanged()
    {
        MarkDirty();
    }

    protected void OnPrTypeChanged(string? value)
    {
        PrType = value;
        if (!IsRepairType)
        {
            foreach (var line in Lines.Where(x => !x.IsConsumed))
            {
                line.RepairType = null;
            }
        }

        MarkDirty();
    }

    protected void OnNewLineClick()
    {
        if (!CanMutateLines)
        {
            return;
        }

        _editingLine = null;
        Popup = new PoPrLineVm
        {
            PurchaseQty = 1m,
            PackSz = 1m,
            IsInclusive = Lines.FirstOrDefault()?.IsInclusive ?? _defaultInclusive,
            Currency = CurrencyDisplay ?? Currencies.FirstOrDefault()?.Code,
            ToWarehouse = Warehouses.FirstOrDefault()?.WarehouseCode
        };
        PopupError = null;
        PopupVisible = true;
    }

    protected void EditLine(PoPrLineVm line)
    {
        if (!CanMutateLines && !IsViewMode)
        {
            return;
        }

        if (!CanEditDocument && !IsViewMode)
        {
            return;
        }

        _editingLine = line;
        Popup = line.Clone();
        PopupError = null;
        PopupVisible = true;
    }

    protected void RemoveLine(PoPrLineVm line)
    {
        if (!CanDeleteLine(line))
        {
            return;
        }

        Lines.Remove(line);
        RefreshCurrencyDisplay();
        RecalcDocument();
        MarkDirty();
    }

    protected bool CanDeleteLine(PoPrLineVm line) =>
        CanMutateLines && !line.IsConsumed;

    protected bool CanEditLine(PoPrLineVm line) =>
        CanMutateLines && !line.IsConsumed;

    protected void OnPopupItemChanged(string? iCode)
    {
        if (PopupReadOnly)
        {
            return;
        }

        var previous = Popup.ICode;
        Popup.ICode = iCode ?? string.Empty;
        var item = Items.FirstOrDefault(x => string.Equals(x.ICode, Popup.ICode, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        Popup.IDesc = item.IDesc;
        Popup.PurchaseUom = item.PurchaseUom;
        Popup.StdUom = item.StdUom;
        Popup.PackSz = item.PackSz == 0m ? 1m : item.PackSz;
        if (!string.IsNullOrWhiteSpace(item.Category))
        {
            Popup.Category = item.Category;
        }

        if (!string.IsNullOrWhiteSpace(item.TaxGroup)
            && TaxGroups.Any(x => string.Equals(x.TaxGrCode, item.TaxGroup, StringComparison.OrdinalIgnoreCase)))
        {
            Popup.TaxGroup = item.TaxGroup;
        }

        if (!string.IsNullOrWhiteSpace(item.VendorCd))
        {
            Popup.VendorCd = item.VendorCd;
            Popup.VendNm = item.VendNm;
        }

        if (!string.IsNullOrWhiteSpace(item.DefWarehouse))
        {
            Popup.ToWarehouse = item.DefWarehouse;
        }

        var icodeChanged = !string.Equals(previous, Popup.ICode, StringComparison.OrdinalIgnoreCase);
        if (_editingLine is null || icodeChanged)
        {
            if (CanViewCost)
            {
                Popup.UnitPrice = item.UnitPrice ?? 0m;
            }
        }

        if (string.IsNullOrWhiteSpace(Popup.Currency))
        {
            Popup.Currency = CurrencyDisplay ?? Currencies.FirstOrDefault()?.Code;
        }

        RecalcPopupDerived();
    }

    protected void OnPopupVendorChanged(string? vendorCd)
    {
        if (PopupReadOnly)
        {
            return;
        }

        Popup.VendorCd = vendorCd;
        var vendor = Vendors.FirstOrDefault(x => string.Equals(x.SuppCode, vendorCd, StringComparison.OrdinalIgnoreCase));
        Popup.VendNm = vendor?.SuppName;
        if (vendor is not null
            && !string.IsNullOrWhiteSpace(vendor.Currency)
            && string.IsNullOrWhiteSpace(Popup.Currency))
        {
            Popup.Currency = vendor.Currency;
        }

        // Vendor change does not reprice — retained UnitPrice.
        RecalcPopupDerived();
    }

    protected void OnPopupQtyOrPriceChanged()
    {
        if (PopupReadOnly)
        {
            return;
        }

        RecalcPopupDerived();
    }

    protected void OnPopupTaxChanged()
    {
        if (PopupReadOnly)
        {
            return;
        }

        RecalcPopupDerived();
    }

    protected void OnPopupSave()
    {
        if (PopupIsConsumed)
        {
            PopupVisible = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(Popup.ICode))
        {
            PopupError = "Item is required.";
            return;
        }

        if (Popup.PurchaseQty == 0m)
        {
            PopupError = "Purchase quantity cannot be zero.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Popup.PurchaseUom))
        {
            PopupError = "Purchase UOM is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Popup.Currency))
        {
            PopupError = "Currency is required.";
            return;
        }

        if (IsRepairType && string.IsNullOrWhiteSpace(Popup.RepairType))
        {
            PopupError = "Repair type is required.";
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

            var expectedCurrency = CurrencyDisplay
                ?? Lines.OrderBy(x => x.Line == 0 ? short.MaxValue : x.Line)
                    .Select(x => x.Currency)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(expectedCurrency)
                && !string.Equals(expectedCurrency, Popup.Currency, StringComparison.OrdinalIgnoreCase)
                && !AllLinesWillUseCurrency(Popup.Currency))
            {
                PopupError = "All lines must use the same currency.";
                return;
            }
        }

        RecalcPopupDerived();
        Popup.EtaDt = Popup.EtaDt?.Date;

        if (_editingLine is null)
        {
            var line = Popup.Clone();
            line.Line = 0;
            Lines.Add(line);
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

        RefreshCurrencyDisplay();
        RecalcDocument();
        MarkDirty();
        PopupVisible = false;
    }

    private bool AllLinesWillUseCurrency(string? currency)
    {
        foreach (var line in Lines)
        {
            if (_editingLine is not null && ReferenceEquals(line, _editingLine))
            {
                continue;
            }

            if (!string.Equals(line.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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
                ? await Prs.SaveNewAsync(request, _cts.Token)
                : await Prs.UpdateAsync(PrNo!, request, _cts.Token);
            if (_disposed)
            {
                return;
            }

            if (!HandleOperationResult(result))
            {
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo("/purchase/requisitions");
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
        Navigation.NavigateTo("/purchase/requisitions");
    }

    protected void OnClose() => Navigation.NavigateTo("/purchase/requisitions");

    protected void OnEditFromView() => Navigation.NavigateTo($"/purchase/requisitions/edit/{PrNo}");

    protected async Task ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _isDirty = false;
        await DiscardDraftIfNeededAsync();
        Navigation.NavigateTo("/purchase/requisitions");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (IsNewMode || IsCopyMode || string.IsNullOrWhiteSpace(PrNo))
        {
            return;
        }

        var result = await Prs.GetAsync(PrNo, _cts.Token);
        if (_disposed)
        {
            return;
        }

        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to reload purchase requisition.";
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
                "uploadPrAttachmentFromInput",
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

    protected async Task OnDeleteAttachmentAsync(PoPrAttachmentRow row)
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
        $"/purchase/requisitions/attachments/{Uri.EscapeDataString(AttachmentDocId ?? string.Empty)}/file?docName={Uri.EscapeDataString(docName ?? string.Empty)}";

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

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

    private bool HandleOperationResult(PoPrOperationResult result)
    {
        if (result.Succeeded)
        {
            return true;
        }

        switch (result.ErrorKind)
        {
            case PoPrErrorKind.Validation:
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
                var firstDetail = ValidationErrors.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                ErrorMessage = !string.IsNullOrWhiteSpace(firstDetail)
                    ? firstDetail
                    : (result.ErrorMessage ?? "Validation failed.");
                break;
            case PoPrErrorKind.Concurrency:
                ConcurrencyVisible = true;
                ErrorMessage = result.ErrorMessage ?? "This purchase requisition was changed by another user.";
                break;
            case PoPrErrorKind.NotFound:
                ErrorMessage = result.ErrorMessage ?? "Purchase requisition was not found.";
                Navigation.NavigateTo("/purchase/requisitions");
                break;
            case PoPrErrorKind.Authorization:
                ErrorMessage = result.ErrorMessage ?? "Access denied.";
                break;
            default:
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the request.";
                break;
        }

        return false;
    }

    private PoPrSaveRequest ToRequest()
    {
        var isCreate = IsNewMode || IsCopyMode;
        return new PoPrSaveRequest
        {
            CreateDt = CreateDt,
            Requester = Requester,
            DeptCode = DeptCode,
            CheckedBy = CheckedBy,
            AuthorisedBy = AuthorisedBy,
            AuthorisedBy2nd = AuthorisedBy2nd,
            PrType = PrType,
            ProjId = ProjId,
            Remarks = Remarks,
            TempDocId = TempDocId,
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

        foreach (var line in Lines.Where(x => !x.IsConsumed))
        {
            RecalcLine(line);
        }

        var totals = PoPrCalc.SumTotals(Lines.Select(x => (x.NetAmount, x.TaxAmount)));
        Gross = totals.Gross;
        Taxes = totals.Taxes;
        Total = totals.Total;
    }

    private void RecalcPopupDerived() => RecalcLine(Popup);

    private void RecalcLine(PoPrLineVm line)
    {
        line.StdQty = PoPrCalc.ComputeStdQty(line.PurchaseQty, line.PackSz);
        line.Qty = line.StdQty;
        var amount = PoPrCalc.ComputeAmount(line.PurchaseQty, line.UnitPrice);
        line.Amount = amount;
        var taxPercent = ResolveTaxPercent(line.TaxGroup);
        var (net, tax) = PoPrCalc.ComputeTax(amount, taxPercent, line.IsInclusive, _taxDecimals);
        line.NetAmount = net;
        line.TaxAmount = tax;
    }

    private (decimal Amount, decimal TaxAmount, decimal NetAmount) BuildPopupCalc()
    {
        var amount = PoPrCalc.ComputeAmount(Popup.PurchaseQty, Popup.UnitPrice);
        var taxPercent = ResolveTaxPercent(Popup.TaxGroup);
        var (net, tax) = PoPrCalc.ComputeTax(amount, taxPercent, Popup.IsInclusive, _taxDecimals);
        return (amount, tax, net);
    }

    private decimal ResolveTaxPercent(string? taxGroup)
    {
        if (string.IsNullOrWhiteSpace(taxGroup))
        {
            return 0m;
        }

        var match = TaxGroups.FirstOrDefault(x =>
            string.Equals(x.TaxGrCode, taxGroup, StringComparison.OrdinalIgnoreCase));
        return match?.Percentage ?? 0m;
    }

    private void RefreshCurrencyDisplay()
    {
        CurrencyDisplay = Lines
            .Where(x => !string.IsNullOrWhiteSpace(x.Currency))
            .OrderBy(x => x.Line == 0 ? short.MaxValue : x.Line)
            .Select(x => x.Currency)
            .FirstOrDefault();
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
            "/js/pr-attach.js");
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

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    private sealed class AttachmentUploadResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string? Message { get; set; }
    }
}

public sealed class PoPrLineVm
{
    public Guid UiKey { get; set; } = Guid.NewGuid();
    public short Line { get; set; }
    public DateTime? EtaDt { get; set; }
    public bool? OneTimeItemYn { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? Category { get; set; }
    public decimal Qty { get; set; }
    public decimal PackSz { get; set; } = 1m;
    public string? StdUom { get; set; }
    public decimal PurchaseQty { get; set; }
    public string? PurchaseUom { get; set; }
    public string? Currency { get; set; }
    public decimal UnitPrice { get; set; }
    public bool? OneTimeVendor { get; set; }
    public string? VendorCd { get; set; }
    public string? VendNm { get; set; }
    public string? Purpose { get; set; }
    public string? Status { get; set; }
    public decimal StdQty { get; set; }
    public decimal WtQty { get; set; }
    public string? WtUom { get; set; }
    public string? PaymentTerm { get; set; }
    public string? BuyingTerm { get; set; }
    public string? RepairType { get; set; }
    public decimal Amount { get; set; }
    public string? PoNo { get; set; }
    public string? TaxGroup { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsInclusive { get; set; }
    public string? ToWarehouse { get; set; }
    public string? SoNo { get; set; }
    public int? SoLine { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsConsumed => !string.IsNullOrWhiteSpace(PoNo);

    public static PoPrLineVm FromDto(PoPrLineDto dto) =>
        new()
        {
            UiKey = Guid.NewGuid(),
            Line = dto.Line,
            EtaDt = dto.EtaDt,
            OneTimeItemYn = dto.OneTimeItemYn,
            ICode = dto.ICode,
            IDesc = dto.IDesc,
            Category = dto.Category,
            Qty = dto.Qty,
            PackSz = dto.PackSz,
            StdUom = dto.StdUom,
            PurchaseQty = dto.PurchaseQty,
            PurchaseUom = dto.PurchaseUom,
            Currency = dto.Currency,
            UnitPrice = dto.UnitPrice,
            OneTimeVendor = dto.OneTimeVendor,
            VendorCd = dto.VendorCd,
            VendNm = dto.VendNm,
            Purpose = dto.Purpose,
            Status = dto.Status,
            StdQty = dto.StdQty,
            WtQty = dto.WtQty,
            WtUom = dto.WtUom,
            PaymentTerm = dto.PaymentTerm,
            BuyingTerm = dto.BuyingTerm,
            RepairType = dto.RepairType,
            Amount = dto.Amount,
            PoNo = dto.PoNo,
            TaxGroup = dto.TaxGroup,
            TaxAmount = dto.TaxAmount,
            IsInclusive = dto.IsInclusive,
            ToWarehouse = dto.ToWarehouse,
            SoNo = dto.SoNo,
            SoLine = dto.SoLine,
            NetAmount = dto.NetAmount
        };

    public PoPrLineDto ToDto() =>
        new()
        {
            Line = Line,
            EtaDt = EtaDt,
            OneTimeItemYn = OneTimeItemYn,
            ICode = ICode,
            IDesc = IDesc,
            Category = Category,
            Qty = Qty,
            PackSz = PackSz,
            StdUom = StdUom,
            PurchaseQty = PurchaseQty,
            PurchaseUom = PurchaseUom,
            Currency = Currency,
            UnitPrice = UnitPrice,
            OneTimeVendor = OneTimeVendor,
            VendorCd = VendorCd,
            VendNm = VendNm,
            Purpose = Purpose,
            Status = Status,
            StdQty = StdQty,
            WtQty = WtQty,
            WtUom = WtUom,
            PaymentTerm = PaymentTerm,
            BuyingTerm = BuyingTerm,
            RepairType = RepairType,
            Amount = Amount,
            PoNo = PoNo,
            TaxGroup = TaxGroup,
            TaxAmount = TaxAmount,
            IsInclusive = IsInclusive,
            ToWarehouse = ToWarehouse,
            SoNo = SoNo,
            SoLine = SoLine,
            NetAmount = NetAmount
        };

    public PoPrLineVm Clone()
    {
        var clone = FromDto(ToDto());
        clone.UiKey = UiKey;
        return clone;
    }
}
