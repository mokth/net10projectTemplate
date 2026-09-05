using System.Text.Json;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaCustEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? CustCode { get; set; }
    [SupplyParameterFromQuery(Name = "copy")] public string? CopyFrom { get; set; }

    [Inject] private ISaCustService Customers { get; set; } = default!;
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private string _cleanSnapshot = string.Empty;
    private bool _lookupsLoaded;
    private string? _loadedKey;
    private int? _editingAddressIndex;
    private int? _editingContactIndex;

    protected int ActiveTabIndex { get; set; }
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool AddressPopupVisible;
    protected bool ContactPopupVisible;
    protected string? AddressPopupError;
    protected string? ContactPopupError;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected SaCustEditVm Model { get; set; } = CreateBlank();
    protected SaCustAddressVm AddressPopup { get; set; } = new();
    protected SaCustContactVm ContactPopup { get; set; } = new();
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    protected IReadOnlyList<IvCodeLookupRow> Types { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Groups { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Areas { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Countries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Currencies { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> DisGroups { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> States { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> TaxGroups { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> PayCodes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Industries { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Channels { get; set; } = [];

    private static readonly IvCodeLookupRow[] CreditTermOptions =
    [
        new() { Code = SaCustPaymentOptions.CreditLimit, Desc = SaCustPaymentOptions.CreditLimit }
    ];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;

    protected string PageHeading => IsNewMode
        ? (string.IsNullOrWhiteSpace(CopyFrom) ? "New customer" : "Copy customer")
        : IsEditMode
            ? "Edit customer"
            : "View customer";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected bool IsDirty =>
        !IsViewMode
        && !IsLoading
        && !string.Equals(_cleanSnapshot, Snapshot(Model), StringComparison.Ordinal);

    protected bool IsEditingAddress => _editingAddressIndex.HasValue;
    protected bool IsEditingContact => _editingContactIndex.HasValue;
    protected string AddressPopupTitle => IsEditingAddress ? "Edit address" : "New address";
    protected string ContactPopupTitle => IsEditingContact ? "Edit contact" : "New contact";

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEdit = await AccessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Edit);
            await LoadLookupsAsync();
            _lookupsLoaded = true;
        }

        var key = $"{Mode}|{CustCode}|{CopyFrom}";
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
        if (!await AccessRights.CanAsync(MenuCodes.SalesCustomerProfile, permission))
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
            var result = await Customers.SaveAsync(Model, IsNewMode);
            if (result.Succeeded)
            {
                Navigation.NavigateTo("/sales/customers");
                return;
            }

            if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ConcurrencyVisible = true;
                ErrorMessage = result.Message ?? "Concurrency conflict.";
            }
            else
            {
                ErrorMessage = result.Message ?? "Unable to save customer.";
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
            }
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

        Navigation.NavigateTo("/sales/customers");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/sales/customers");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (string.IsNullOrWhiteSpace(Model.CustCode))
        {
            return;
        }

        Navigation.NavigateTo($"/sales/customers/edit/{Uri.EscapeDataString(Model.CustCode)}");
    }

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        Navigation.NavigateTo("/sales/customers");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.CustCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = DecodeCustCode(CustCode);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await Customers.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to reload customer.";
            return;
        }

        Model = Clone(result.Data);
        ValidationErrors.Clear();
        ErrorMessage = null;
        StatusMessage = "Loaded latest version.";
        CaptureCleanSnapshot();
    }

    protected async Task KeepMyChangesAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.CustCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await Customers.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to refresh concurrency token.";
            return;
        }

        Model.RowVersion = result.Data.RowVersion;
        StatusMessage = "Kept your changes. Save again to overwrite.";
        await Task.CompletedTask;
    }

    protected void OnNewAddressClick()
    {
        _editingAddressIndex = null;
        AddressPopupError = null;
        AddressPopup = new SaCustAddressVm();
        AddressPopupVisible = true;
    }

    protected void OnEditAddress(SaCustAddressVm address)
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

    protected void OnDeleteAddress(SaCustAddressVm address)
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

    protected void OnNewContactClick()
    {
        _editingContactIndex = null;
        ContactPopupError = null;
        ContactPopup = new SaCustContactVm();
        ContactPopupVisible = true;
    }

    protected void OnEditContact(SaCustContactVm contact)
    {
        var index = Model.Contacts.IndexOf(contact);
        if (index < 0)
        {
            return;
        }

        _editingContactIndex = index;
        ContactPopupError = null;
        ContactPopup = CloneContact(contact);
        ContactPopupVisible = true;
    }

    protected void OnDeleteContact(SaCustContactVm contact)
    {
        if (contact.Line <= 1 || Model.Contacts.Count <= 1)
        {
            return;
        }

        Model.Contacts.Remove(contact);
        RenumberContacts();
    }

    protected void OnContactPopupCancel()
    {
        ContactPopupVisible = false;
        _editingContactIndex = null;
        ContactPopupError = null;
    }

    protected void OnContactCommit()
    {
        ContactPopupError = null;

        if (_editingContactIndex is int index && index >= 0 && index < Model.Contacts.Count)
        {
            Model.Contacts[index] = CloneContact(ContactPopup);
        }
        else
        {
            Model.Contacts.Add(CloneContact(ContactPopup));
        }

        RenumberContacts();
        ContactPopupVisible = false;
        _editingContactIndex = null;
    }

    protected bool LmwAtsValue
    {
        get => Model.LmwAts ?? false;
        set => Model.LmwAts = value;
    }

    protected bool AppInvoiceValue
    {
        get => Model.AppInvoice ?? false;
        set => Model.AppInvoice = value;
    }

    protected bool AppShipValue
    {
        get => Model.AppShip ?? false;
        set
        {
            Model.AppShip = value;
            if (value || IsShipAddressEmpty())
            {
                CopyMainToShip();
            }
        }
    }

    protected bool TaxableValue
    {
        get => Model.Taxable ?? false;
        set => Model.Taxable = value;
    }

    protected decimal OutstandingAmount { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> CreditTerms
    {
        get
        {
            var current = Model.CreditTerm?.Trim();
            if (string.IsNullOrWhiteSpace(current)
                || CreditTermOptions.Any(x => string.Equals(x.Code, current, StringComparison.OrdinalIgnoreCase)))
            {
                return CreditTermOptions;
            }

            return [.. CreditTermOptions, new IvCodeLookupRow { Code = current, Desc = current }];
        }
    }

    protected string GroupDiscountPercentText
    {
        get
        {
            var code = Model.GroupDiscount?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return "0";
            }

            var row = DisGroups.FirstOrDefault(x =>
                string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            var rate = row?.Rate ?? 0m;
            return rate == decimal.Truncate(rate) ? rate.ToString("0") : rate.ToString("0.##");
        }
    }

    protected bool IsDiscountJoin => MatchesOption(Model.DiscountMethod, SaCustPaymentOptions.DiscountJoin);
    protected bool IsDiscountSplit => MatchesOption(Model.DiscountMethod, SaCustPaymentOptions.DiscountSplit);
    protected bool IsPriceDealer =>
        MatchesOption(Model.PriceMethod, SaCustPaymentOptions.PriceDealer)
        || ContainsToken(Model.PriceMethod, "DEALER");
    protected bool IsPriceSelling =>
        !IsPriceDealer
        && (MatchesOption(Model.PriceMethod, SaCustPaymentOptions.PriceSelling)
            || ContainsToken(Model.PriceMethod, "SELLING"));
    protected bool IsAgingDue =>
        MatchesOption(Model.AgingType, SaCustPaymentOptions.AgingDue)
        || ContainsToken(Model.AgingType, "DUE");
    protected bool IsAgingInvoice =>
        !IsAgingDue
        && (MatchesOption(Model.AgingType, SaCustPaymentOptions.AgingInvoice)
            || ContainsToken(Model.AgingType, "INVOICE"));

    protected void SetDiscountMethod(string value) => Model.DiscountMethod = value;
    protected void SetPriceMethod(string value) => Model.PriceMethod = value;
    protected void SetAgingType(string value) => Model.AgingType = value;
    protected void RefreshOutstanding() => OutstandingAmount = 0m;

    protected void OnAddressRowDoubleClick(GridRowClickEventArgs e)
    {
        if (e.Grid.GetDataItem(e.VisibleIndex) is SaCustAddressVm address)
        {
            OnEditAddress(address);
        }
    }

    protected void OnContactRowDoubleClick(GridRowClickEventArgs e)
    {
        if (e.Grid.GetDataItem(e.VisibleIndex) is SaCustContactVm contact)
        {
            OnEditContact(contact);
        }
    }

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

    protected string FormatGroupDiscount()
    {
        var code = Model.GroupDiscount?.Trim();
        var percent = $"{GroupDiscountPercentText} %";
        return string.IsNullOrWhiteSpace(code) ? percent : $"{code} · {percent}";
    }

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConcurrencyVisible = false;
        ConfirmDiscardVisible = false;
        AddressPopupVisible = false;
        ContactPopupVisible = false;
        ActiveTabIndex = 0;

        try
        {
            if (IsNewMode)
            {
                if (!string.IsNullOrWhiteSpace(CopyFrom))
                {
                    var copyResult = await Customers.GetAsync(CopyFrom);
                    if (!copyResult.Succeeded || copyResult.Data is null)
                    {
                        ErrorMessage = copyResult.Message ?? "Unable to copy customer.";
                        Model = CreateBlank();
                    }
                    else
                    {
                        Model = Clone(copyResult.Data);
                        Model.CustCode = string.Empty;
                        Model.RowVersion = null;
                        Model.CreatedBy = null;
                        Model.CreatedDate = null;
                        Model.ModifiedBy = null;
                        Model.ModifiedDate = null;
                        StatusMessage = $"Copied from {CopyFrom}.";
                    }
                }
                else
                {
                    Model = CreateBlank();
                }

                CaptureCleanSnapshot();
                return;
            }

            var code = DecodeCustCode(CustCode);
            if (string.IsNullOrWhiteSpace(code))
            {
                ErrorMessage = "Customer code is required.";
                Model = CreateBlank();
                return;
            }

            var result = await Customers.GetAsync(code);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Customer was not found.";
                Model = CreateBlank();
                return;
            }

            Model = Clone(result.Data);
            CaptureCleanSnapshot();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadLookupsAsync()
    {
        Types = await Lookups.ListTypesForAssignmentAsync();
        Groups = await Lookups.ListGroupsForAssignmentAsync();
        Areas = await Lookups.ListAreasForAssignmentAsync();
        Countries = await Lookups.ListCountriesForAssignmentAsync();
        Currencies = await Lookups.ListCurrenciesForAssignmentAsync();
        DisGroups = await Lookups.ListDisGroupsForAssignmentAsync();
        States = await Lookups.ListStatesForAssignmentAsync();
        TaxGroups = await Lookups.ListTaxGroupsForAssignmentAsync();
        PayCodes = await Lookups.ListPayCodesForAssignmentAsync();
        Industries = await Lookups.ListIndustriesForAssignmentAsync();
        Channels = await Lookups.ListChannelsForAssignmentAsync();
    }

    private void CaptureCleanSnapshot() => _cleanSnapshot = Snapshot(Model);

    private static string Snapshot(SaCustEditVm model) =>
        JsonSerializer.Serialize(model);

    private static string? DecodeCustCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value).Trim();

    private static void NormalizeModel(SaCustEditVm model)
    {
        model.CustCode = (model.CustCode ?? string.Empty).Trim();
        model.CustName = (model.CustName ?? string.Empty).Trim();

        if (model.Contacts.Count == 0)
        {
            model.Contacts.Add(new SaCustContactVm { Line = 1 });
        }

        RenumberAddresses(model);
        RenumberContacts(model);
    }

    private void RenumberAddresses() => RenumberAddresses(Model);

    private static void RenumberAddresses(SaCustEditVm model)
    {
        var line = 1;
        foreach (var address in model.Addresses)
        {
            address.Line = line++;
        }
    }

    private void RenumberContacts() => RenumberContacts(Model);

    private static void RenumberContacts(SaCustEditVm model)
    {
        var line = 1;
        foreach (var contact in model.Contacts)
        {
            contact.Line = line++;
        }
    }

    private static SaCustEditVm CreateBlank() =>
        new()
        {
            IsActive = true,
            DiscountMethod = SaCustPaymentOptions.DiscountJoin,
            PriceMethod = SaCustPaymentOptions.PriceSelling,
            AgingType = SaCustPaymentOptions.AgingInvoice,
            CreditTerm = SaCustPaymentOptions.CreditLimit,
            AppShip = true,
            PaidUpCapital = 0m,
            OpeningAmount = 0m,
            CreditLimit = 0m,
            Contacts = [new SaCustContactVm { Line = 1 }]
        };

    private static bool MatchesOption(string? value, string option) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), option, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static SaCustEditVm Clone(SaCustEditVm source) =>
        new()
        {
            CustCode = source.CustCode,
            CustName = source.CustName,
            CustShortName = source.CustShortName,
            CustType = source.CustType,
            InvoicePrefix = source.InvoicePrefix,
            CustGroupCode = source.CustGroupCode,
            LmwAts = source.LmwAts,
            SalesmanCode = source.SalesmanCode,
            AreaCode = source.AreaCode,
            SubGroupCode = source.SubGroupCode,
            IndustryCode = source.IndustryCode,
            ChannelCode = source.ChannelCode,
            IsActive = source.IsActive,
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
            Email = source.Email,
            Website = source.Website,
            CjLmw = source.CjLmw,
            CustBrn = source.CustBrn,
            RegType = source.RegType,
            Remark = source.Remark,
            AppInvoice = source.AppInvoice,
            AppShip = source.AppShip,
            ShipName = source.ShipName,
            ShipAddress1 = source.ShipAddress1,
            ShipAddress2 = source.ShipAddress2,
            ShipAddress3 = source.ShipAddress3,
            ShipCity = source.ShipCity,
            ShipState = source.ShipState,
            ShipPostalCode = source.ShipPostalCode,
            ShipCountry = source.ShipCountry,
            ShipTel = source.ShipTel,
            ShipFax = source.ShipFax,
            ShipEmail = source.ShipEmail,
            ShipWebsite = source.ShipWebsite,
            Addresses = source.Addresses.Select(CloneAddress).ToList(),
            Contacts = source.Contacts.Select(CloneContact).ToList(),
            Taxable = source.Taxable,
            TaxGrCode = source.TaxGrCode,
            GstregNo = source.GstregNo,
            PayCode = source.PayCode,
            Currency = source.Currency,
            GroupDiscount = source.GroupDiscount,
            DiscountMethod = source.DiscountMethod,
            PriceMethod = source.PriceMethod,
            AgingType = source.AgingType,
            PaidUpCapital = source.PaidUpCapital,
            GlCode = source.GlCode,
            OpeningAmount = source.OpeningAmount,
            CreditTerm = source.CreditTerm,
            CreditLimit = source.CreditLimit,
            CustPriceCode = source.CustPriceCode,
            RowVersion = source.RowVersion,
            CreatedDate = source.CreatedDate,
            CreatedBy = source.CreatedBy,
            ModifiedDate = source.ModifiedDate,
            ModifiedBy = source.ModifiedBy
        };

    private static SaCustAddressVm CloneAddress(SaCustAddressVm source) =>
        new()
        {
            Line = source.Line,
            AddName = source.AddName,
            DeliverTo = source.DeliverTo,
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

    private static SaCustContactVm CloneContact(SaCustContactVm source) =>
        new()
        {
            Line = source.Line,
            ContactPerson = source.ContactPerson,
            Title = source.Title,
            Department = source.Department,
            ContactEmail = source.ContactEmail,
            ContactTelp = source.ContactTelp,
            ContactFax = source.ContactFax
        };

    private bool IsShipAddressEmpty() =>
        string.IsNullOrWhiteSpace(Model.ShipName)
        && string.IsNullOrWhiteSpace(Model.ShipAddress1)
        && string.IsNullOrWhiteSpace(Model.ShipAddress2)
        && string.IsNullOrWhiteSpace(Model.ShipAddress3)
        && string.IsNullOrWhiteSpace(Model.ShipCity)
        && string.IsNullOrWhiteSpace(Model.ShipState)
        && string.IsNullOrWhiteSpace(Model.ShipPostalCode)
        && string.IsNullOrWhiteSpace(Model.ShipCountry)
        && string.IsNullOrWhiteSpace(Model.ShipTel)
        && string.IsNullOrWhiteSpace(Model.ShipFax)
        && string.IsNullOrWhiteSpace(Model.ShipEmail)
        && string.IsNullOrWhiteSpace(Model.ShipWebsite);

    private void CopyMainToShip()
    {
        Model.ShipName = Model.CustName;
        Model.ShipAddress1 = Model.Address1;
        Model.ShipAddress2 = Model.Address2;
        Model.ShipAddress3 = Model.Address3;
        Model.ShipCity = Model.City;
        Model.ShipState = Model.State;
        Model.ShipPostalCode = Model.PostalCode;
        Model.ShipCountry = Model.Country;
        Model.ShipTel = Model.Tel;
        Model.ShipFax = Model.Fax;
        Model.ShipEmail = Model.Email;
        Model.ShipWebsite = Model.Website;
    }
}
