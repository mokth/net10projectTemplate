using System.Text.Json;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoSuppEntry : PageBase, IAsyncDisposable
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? SuppCode { get; set; }
    [SupplyParameterFromQuery(Name = "copy")] public string? CopyFrom { get; set; }

    [Inject] private IPoSupplierService Suppliers { get; set; } = default!;
    [Inject] private IPoSupplierLookupService Lookups { get; set; } = default!;
    [Inject] private IPoSupplierAttachmentService AttachmentService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;
    [Inject] private IAntiforgery Antiforgery { get; set; } = default!;

    private string _cleanSnapshot = string.Empty;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private int? _editingAddressIndex;
    private IJSObjectReference? _attachmentModule;
    private string? _antiforgeryToken;

    protected int ActiveTabIndex { get; set; }
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsUploadingAttachment;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool AddressPopupVisible;
    protected string? AddressPopupError;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected string? SelectedAttachmentName;
    protected PoSupplierEditVm Model { get; set; } = CreateBlank();
    protected PoSupplierAddressVm AddressPopup { get; set; } = new();
    protected List<PoSupplierAttachmentRow> AttachmentRows { get; set; } = [];
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    protected ElementReference AttachmentInputRef;

    protected IReadOnlyList<IvCodeLookupRow> Areas { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Currencies { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> TaxGroups { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> BuyingTerms { get; set; } = [];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool IsEditingAddress => _editingAddressIndex.HasValue;
    protected bool CanManageAttachments => !IsViewMode && !IsNewMode && !string.IsNullOrWhiteSpace(Model.SuppCode);
    protected bool AttachmentUploadDisabled => !CanManageAttachments || IsSubmitting || IsUploadingAttachment;
    protected string AddressPopupTitle => IsEditingAddress ? "Edit shipping address" : "New shipping address";

    protected string PageHeading => IsNewMode
        ? (string.IsNullOrWhiteSpace(CopyFrom) ? "New supplier" : "Copy supplier")
        : IsEditMode
            ? "Edit supplier"
            : "View supplier";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected bool IsDirty =>
        !IsViewMode
        && !IsLoading
        && !string.Equals(_cleanSnapshot, Snapshot(Model), StringComparison.Ordinal);

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Edit);
            LoadAntiforgeryToken();
            await LoadLookupsAsync();
            _lookupsLoaded = true;
        }

        var key = $"{Mode}|{SuppCode}|{CopyFrom}";
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadPageAsync();
    }

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected async Task OnSaveAsync()
    {
        if (IsSubmitting || IsViewMode)
        {
            return;
        }

        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            NormalizeModel(Model);
            var result = await Suppliers.SaveAsync(Model, IsNewMode);
            if (result.Succeeded)
            {
                Navigation.NavigateTo("/purchase/suppliers");
                return;
            }

            if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ConcurrencyVisible = true;
                ErrorMessage = result.Message ?? "Concurrency conflict.";
                return;
            }

            if (IsUnknownOutcome(result.Message))
            {
                ErrorMessage = $"{result.Message} Reload the latest record and do not retry save.";
                return;
            }

            ErrorMessage = result.Message ?? "Unable to save supplier.";
            ValidationErrors = result.ValidationErrors.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (IsDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/purchase/suppliers");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/purchase/suppliers");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (string.IsNullOrWhiteSpace(Model.SuppCode))
        {
            return;
        }

        Navigation.NavigateTo($"/purchase/suppliers/edit/{BuildSuppCodePath(Model.SuppCode)}");
    }

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        Navigation.NavigateTo("/purchase/suppliers");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.SuppCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = DecodeSuppCode(SuppCode);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await Suppliers.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to reload supplier.";
            return;
        }

        Model = Clone(result.Data);
        await LoadAttachmentsAsync(Model.SuppCode);
        ValidationErrors.Clear();
        ErrorMessage = null;
        StatusMessage = "Loaded latest version.";
        CaptureCleanSnapshot();
    }

    protected async Task KeepMyChangesAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.SuppCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await Suppliers.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to refresh concurrency token.";
            return;
        }

        Model.RowVersion = result.Data.RowVersion;
        StatusMessage = "Kept your changes. Save again to overwrite.";
    }

    protected void OnNewAddressClick()
    {
        _editingAddressIndex = null;
        AddressPopupError = null;
        AddressPopup = CreateAddressDraft();
        AddressPopupVisible = true;
    }

    protected void OnEditAddress(PoSupplierAddressVm address)
    {
        var index = Model.Addresses.IndexOf(address);
        if (index < 0)
        {
            return;
        }

        _editingAddressIndex = index;
        AddressPopupError = null;
        AddressPopup = CloneAddress(address);
        AddressPopupVisible = true;
    }

    protected void OnDeleteAddress(PoSupplierAddressVm address)
    {
        Model.Addresses.Remove(address);
        RenumberAddresses();
    }

    protected void OnAddressPopupCancel()
    {
        AddressPopupVisible = false;
        _editingAddressIndex = null;
        AddressPopupError = null;
    }

    protected void OnAddressCommit()
    {
        AddressPopupError = null;

        if (_editingAddressIndex is int index && index >= 0 && index < Model.Addresses.Count)
        {
            Model.Addresses[index] = CloneAddress(AddressPopup);
        }
        else
        {
            Model.Addresses.Add(CloneAddress(AddressPopup));
        }

        RenumberAddresses();
        AddressPopupVisible = false;
        _editingAddressIndex = null;
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
        if (AttachmentUploadDisabled)
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
                "uploadSupplierAttachmentFromInput",
                AttachmentInputRef,
                Model.SuppCode,
                _antiforgeryToken);

            if (result.Ok)
            {
                await LoadAttachmentsAsync(Model.SuppCode);
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

    protected async Task OnDeleteAttachmentAsync(PoSupplierAttachmentRow row)
    {
        if (IsViewMode || IsNewMode || IsSubmitting || IsUploadingAttachment)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        var result = await AttachmentService.DeleteAsync(Model.SuppCode, row.DocName);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Message ?? "Unable to delete attachment.";
            return;
        }

        await LoadAttachmentsAsync(Model.SuppCode);
        StatusMessage = $"Deleted attachment {row.DocName}.";
    }

    protected void OnAddressRowDoubleClick(GridRowClickEventArgs e)
    {
        if (e.Grid.GetDataItem(e.VisibleIndex) is PoSupplierAddressVm address)
        {
            OnEditAddress(address);
        }
    }

    protected bool LmwValue
    {
        get => Model.Lmw ?? false;
        set => Model.Lmw = value;
    }

    protected bool TaxableValue
    {
        get => Model.Taxable ?? false;
        set => Model.Taxable = value;
    }

    protected bool IsStatementOpenItem =>
        MatchesOption(Model.StatementType, PoSupplierPaymentOptions.StatementOpenItem);

    protected bool IsStatementBalanceForward =>
        MatchesOption(Model.StatementType, PoSupplierPaymentOptions.StatementBalanceForward);

    protected bool IsStatementNone =>
        MatchesOption(Model.StatementType, PoSupplierPaymentOptions.StatementNone);

    protected bool IsAgingInvoice =>
        MatchesOption(Model.AgingType, PoSupplierPaymentOptions.AgingInvoice);

    protected bool IsAgingDue =>
        MatchesOption(Model.AgingType, PoSupplierPaymentOptions.AgingDue);

    protected void SetStatementType(string value) => Model.StatementType = value;
    protected void SetAgingType(string value) => Model.AgingType = value;

    protected string BuildAttachmentDownloadUrl(string docName) =>
        $"/purchase/suppliers/attachment/{BuildSuppCodePath(Model.SuppCode)}?docName={Uri.EscapeDataString(docName ?? string.Empty)}";

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    protected static string FormatDec(decimal? value, string format = "n2") =>
        value.HasValue ? value.Value.ToString(format) : "—";

    protected static string FormatBool(bool? value) =>
        value switch
        {
            true => "Yes",
            false => "No",
            _ => "—"
        };

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConcurrencyVisible = false;
        ConfirmDiscardVisible = false;
        AddressPopupVisible = false;
        SelectedAttachmentName = null;
        ActiveTabIndex = 0;

        try
        {
            if (IsNewMode)
            {
                if (!string.IsNullOrWhiteSpace(CopyFrom))
                {
                    var copyCode = DecodeSuppCode(CopyFrom);
                    var copyResult = await Suppliers.GetAsync(copyCode ?? string.Empty);
                    if (!copyResult.Succeeded || copyResult.Data is null)
                    {
                        ErrorMessage = copyResult.Message ?? "Unable to copy supplier.";
                        Model = CreateBlank();
                    }
                    else
                    {
                        Model = CloneForCopy(copyResult.Data);
                        StatusMessage = $"Copied from {copyCode}";
                    }
                }
                else
                {
                    Model = CreateBlank();
                }

                AttachmentRows = [];
                CaptureCleanSnapshot();
                return;
            }

            var code = DecodeSuppCode(SuppCode);
            if (string.IsNullOrWhiteSpace(code))
            {
                ErrorMessage = "Supplier code is required.";
                Model = CreateBlank();
                AttachmentRows = [];
                return;
            }

            var result = await Suppliers.GetAsync(code);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Supplier was not found.";
                Model = CreateBlank();
                AttachmentRows = [];
                return;
            }

            Model = Clone(result.Data);
            await LoadAttachmentsAsync(Model.SuppCode);
            CaptureCleanSnapshot();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadLookupsAsync()
    {
        Areas = await Lookups.ListAreasForAssignmentAsync();
        States = await Lookups.ListStatesForAssignmentAsync();
        Countries = await Lookups.ListCountriesForAssignmentAsync();
        Currencies = await Lookups.ListCurrenciesForAssignmentAsync();
        TaxGroups = await Lookups.ListTaxGroupsForAssignmentAsync();
        PayCodes = await Lookups.ListPayCodesForAssignmentAsync();
        BuyingTerms = await Lookups.ListBuyingTermsForAssignmentAsync();
    }

    private async Task LoadAttachmentsAsync(string? suppCode)
    {
        if (string.IsNullOrWhiteSpace(suppCode))
        {
            AttachmentRows = [];
            return;
        }

        var result = await AttachmentService.ListAsync(suppCode);
        if (!result.Succeeded || result.Data is null)
        {
            AttachmentRows = [];
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
            return;
        }

        AttachmentRows = result.Data.ToList();
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

    private void CaptureCleanSnapshot() => _cleanSnapshot = Snapshot(Model);

    private static string Snapshot(PoSupplierEditVm model) =>
        JsonSerializer.Serialize(model);

    private static string? DecodeSuppCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value).Trim();

    private static string BuildSuppCodePath(string? value)
    {
        var code = (value ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "/",
            code.Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));
    }

    private static void NormalizeModel(PoSupplierEditVm model)
    {
        model.SuppCode = (model.SuppCode ?? string.Empty).Trim();
        model.SuppName = (model.SuppName ?? string.Empty).Trim();
        model.StatementType = NullIfWhiteSpace(model.StatementType) ?? PoSupplierPaymentOptions.StatementOpenItem;
        model.AgingType = NullIfWhiteSpace(model.AgingType) ?? PoSupplierPaymentOptions.AgingInvoice;
        RenumberAddresses(model);
    }

    private static PoSupplierEditVm CreateBlank() =>
        new()
        {
            IsActive = true,
            StatementType = PoSupplierPaymentOptions.StatementOpenItem,
            AgingType = PoSupplierPaymentOptions.AgingInvoice,
            CreditLimit = 0m
        };

    private PoSupplierAddressVm CreateAddressDraft() =>
        new()
        {
            SuppName = Model.SuppName
        };

    private static PoSupplierEditVm Clone(PoSupplierEditVm source) =>
        new()
        {
            SuppCode = source.SuppCode,
            SuppName = source.SuppName,
            SuppShortName = source.SuppShortName,
            SuppType = source.SuppType,
            SupplierBrn = source.SupplierBrn,
            CategoryCode = source.CategoryCode,
            CreditorSubGroup = source.CreditorSubGroup,
            AreaCode = source.AreaCode,
            RegType = source.RegType,
            PoPrefix = source.PoPrefix,
            IsActive = source.IsActive,
            Lmw = source.Lmw,
            MiscCode = source.MiscCode,
            Address1 = source.Address1,
            Address2 = source.Address2,
            Address3 = source.Address3,
            Address4 = source.Address4,
            City = source.City,
            State = source.State,
            PostalCode = source.PostalCode,
            Country = source.Country,
            Tel = source.Tel,
            Fax = source.Fax,
            Telex = source.Telex,
            Email = source.Email,
            Website = source.Website,
            Addresses = source.Addresses.Select(CloneAddress).ToList(),
            ContactPerson = source.ContactPerson,
            Title = source.Title,
            Department = source.Department,
            ContactEmail = source.ContactEmail,
            ContactTelp = source.ContactTelp,
            ContactFax = source.ContactFax,
            ContactPerson2 = source.ContactPerson2,
            Title2 = source.Title2,
            Department2 = source.Department2,
            ContactEmail2 = source.ContactEmail2,
            ContactTelp2 = source.ContactTelp2,
            ContactFax2 = source.ContactFax2,
            ContactPerson3 = source.ContactPerson3,
            Title3 = source.Title3,
            Department3 = source.Department3,
            ContactEmail3 = source.ContactEmail3,
            ContactTelp3 = source.ContactTelp3,
            ContactFax3 = source.ContactFax3,
            ContactPerson4 = source.ContactPerson4,
            Title4 = source.Title4,
            Department4 = source.Department4,
            ContactEmail4 = source.ContactEmail4,
            ContactTelp4 = source.ContactTelp4,
            ContactFax4 = source.ContactFax4,
            Taxable = source.Taxable,
            TaxGrCode = source.TaxGrCode,
            GstregNo = source.GstregNo,
            BankName = source.BankName,
            AccountNo = source.AccountNo,
            StatementType = source.StatementType,
            PayCode = source.PayCode,
            Currency = source.Currency,
            BuyingTerm = source.BuyingTerm,
            GlCode = source.GlCode,
            AgingType = source.AgingType,
            CreditLimit = source.CreditLimit,
            Remark = source.Remark,
            BizDesc = source.BizDesc,
            RowVersion = source.RowVersion,
            CreatedDate = source.CreatedDate,
            CreatedBy = source.CreatedBy,
            ModifiedDate = source.ModifiedDate,
            ModifiedBy = source.ModifiedBy
        };

    private static PoSupplierEditVm CloneForCopy(PoSupplierEditVm source)
    {
        var clone = Clone(source);
        clone.SuppCode = string.Empty;
        clone.RowVersion = null;
        clone.CreatedBy = null;
        clone.CreatedDate = null;
        clone.ModifiedBy = null;
        clone.ModifiedDate = null;
        clone.BankName = null;
        clone.AccountNo = null;
        clone.GlCode = null;
        clone.IsActive = false;
        return clone;
    }

    private static PoSupplierAddressVm CloneAddress(PoSupplierAddressVm source) =>
        new()
        {
            Line = source.Line,
            SuppName = source.SuppName,
            Address1 = source.Address1,
            Address2 = source.Address2,
            Address3 = source.Address3,
            Address4 = source.Address4,
            City = source.City,
            State = source.State,
            PostalCode = source.PostalCode,
            Country = source.Country,
            Tel = source.Tel,
            Fax = source.Fax
        };

    private void RenumberAddresses() => RenumberAddresses(Model);

    private static void RenumberAddresses(PoSupplierEditVm model)
    {
        var line = 1;
        foreach (var address in model.Addresses)
        {
            address.Line = line++;
        }
    }

    private static bool MatchesOption(string? value, string option) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), option, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknownOutcome(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("unknown outcome", StringComparison.OrdinalIgnoreCase)
            || message.Contains("determine whether", StringComparison.OrdinalIgnoreCase));

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var separators = new[] { '\\', '/' };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private async Task<IJSObjectReference> GetAttachmentModuleAsync()
    {
        _attachmentModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "/js/supplier-attach.js");

        return _attachmentModule;
    }

    public async ValueTask DisposeAsync()
    {
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

    private sealed class AttachmentUploadResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string? Message { get; set; }
    }
}
