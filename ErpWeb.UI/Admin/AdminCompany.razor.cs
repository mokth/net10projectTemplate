using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminCompany : PageBase
{
    [Inject] private ICompanyService CompanyService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsCreateMode;
    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected string? StatusMessage;
    protected string ListFilter = string.Empty;

    protected Company EditModel { get; set; } = new() { IsActive = true };
    protected CompanyBootstrapRequest BootstrapModel { get; set; } = CreateDefaultBootstrap();
    protected IReadOnlyList<Company> Companies { get; set; } = [];

    protected bool IsSystemAdmin =>
        CurrentUser.IsInRole(ErpWeb.Core.Security.CompanyService.SystemAdminRole);

    protected bool CanMutate => IsCreateMode ? (IsSystemAdmin && CanAdd) : CanEdit;

    protected bool HasEditableTarget =>
        IsCreateMode || EditModel.CompanyId > 0;

    protected IReadOnlyList<Company> FilteredCompanies
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ListFilter))
            {
                return Companies;
            }

            var term = ListFilter.Trim();
            return Companies
                .Where(c =>
                    c.CompanyCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.CompanyName.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    protected IReadOnlyList<FiscalMonthOption> FiscalMonths { get; } =
    [
        new(1, "January"),
        new(2, "February"),
        new(3, "March"),
        new(4, "April"),
        new(5, "May"),
        new(6, "June"),
        new(7, "July"),
        new(8, "August"),
        new(9, "September"),
        new(10, "October"),
        new(11, "November"),
        new(12, "December")
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.AdminCompany, PermissionCodes.Delete);
        await LoadPageDataAsync();
    }

    private async Task LoadPageDataAsync(int? preferCompanyId = null, string? preserveStatus = null)
    {
        IsLoading = true;
        StatusMessage = preserveStatus;
        ErrorMessage = string.Empty;
        IsCreateMode = false;

        var listResult = await CompanyService.GetCompaniesAsync();
        if (!listResult.Succeeded)
        {
            StatusMessage = listResult.ErrorMessage ?? "Unable to load company.";
            Companies = [];
            EditModel = new Company { IsActive = true };
            IsLoading = false;
            return;
        }

        Companies = listResult.Companies;

        Company? selected = null;
        if (preferCompanyId is int id)
        {
            selected = Companies.FirstOrDefault(c => c.CompanyId == id);
        }

        if (selected is null && IsSystemAdmin)
        {
            selected = Companies.FirstOrDefault(c =>
                           string.Equals(c.CompanyCode, CurrentUser.CompanyCode, StringComparison.OrdinalIgnoreCase))
                       ?? Companies.FirstOrDefault();
        }
        else if (selected is null)
        {
            selected = Companies.FirstOrDefault();
        }

        EditModel = selected is null
            ? new Company
            {
                IsActive = true,
                CompanyCode = CurrentUser.CompanyCode ?? string.Empty,
                Country = "MY",
                CurrencyCode = "MYR",
                TimeZoneId = "Asia/Kuala_Lumpur",
                FiscalYearStartMonth = 1
            }
            : CloneForEdit(selected);

        if (selected is null && !IsSystemAdmin)
        {
            StatusMessage = "Company record is not available. Ask an administrator to create it.";
        }

        IsLoading = false;
    }

    protected async Task SelectCompanyAsync(int companyId)
    {
        if (IsSubmitting)
        {
            return;
        }

        var result = await CompanyService.GetCompanyAsync(companyId);
        if (!result.Succeeded || result.Company is null)
        {
            StatusMessage = result.ErrorMessage ?? "Unable to load company.";
            return;
        }

        IsCreateMode = false;
        ErrorMessage = string.Empty;
        StatusMessage = null;
        EditModel = CloneForEdit(result.Company);
    }

    protected Task OnAddNewClick()
    {
        if (!IsSystemAdmin || !CanAdd)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        IsCreateMode = true;
        ErrorMessage = string.Empty;
        StatusMessage = null;
        BootstrapModel = CreateDefaultBootstrap();
        EditModel = new Company
        {
            IsActive = true,
            Country = "MY",
            CurrencyCode = "MYR",
            TimeZoneId = "Asia/Kuala_Lumpur",
            FiscalYearStartMonth = 1
        };
        return Task.CompletedTask;
    }

    protected async Task CancelCreateAsync()
    {
        await LoadPageDataAsync();
    }

    protected async Task SaveAsync()
    {
        if (IsSubmitting || !CanMutate)
        {
            if (!CanMutate)
            {
                StatusMessage = "Access Denied!!";
            }

            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;

        CompanyOperationResult result;
        if (IsCreateMode)
        {
            result = await CompanyService.AddCompanyAsync(EditModel, BootstrapModel);
        }
        else
        {
            result = await CompanyService.UpdateCompanyAsync(EditModel);
        }

        if (result.Succeeded)
        {
            string? successMessage;
            if (IsCreateMode && result.Bootstrap is not null)
            {
                var boot = result.Bootstrap;
                successMessage =
                    $"Company {boot.CompanyCode} created. Sign in with company {boot.CompanyCode}, user {boot.AdminLoginId} " +
                    $"(branch {boot.BranchCode}, location {boot.LocationCode}). Temporary password must be changed on first login.";
            }
            else
            {
                successMessage = IsCreateMode
                    ? "Company created successfully."
                    : "Company updated successfully.";
            }

            var savedId = result.Company?.CompanyId ?? EditModel.CompanyId;
            await LoadPageDataAsync(savedId, successMessage);
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to save company.";
        }

        IsSubmitting = false;
    }

    protected async Task DeactivateAsync()
    {
        if (!IsSystemAdmin || !CanDelete || EditModel.CompanyId <= 0)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var confirmed = await JsRuntime.InvokeAsync<bool>(
            "confirm",
            $"Deactivate company {EditModel.CompanyCode}? Users in this company will be deactivated and cannot sign in.");
        if (!confirmed)
        {
            return;
        }

        IsSubmitting = true;
        var result = await CompanyService.DeleteCompaniesAsync([EditModel.CompanyId]);
        if (result.Succeeded)
        {
            StatusMessage = "Company deactivated.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to deactivate company.";
        }

        IsSubmitting = false;
    }

    protected static string GetInitials(string? name, string? code)
    {
        var source = string.IsNullOrWhiteSpace(name) ? code : name;
        if (string.IsNullOrWhiteSpace(source))
        {
            return "?";
        }

        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        }

        return source.Length >= 2
            ? source[..2].ToUpperInvariant()
            : source.ToUpperInvariant();
    }

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    private static CompanyBootstrapRequest CreateDefaultBootstrap() =>
        new()
        {
            AdminLoginId = "admin",
            BranchCode = ErpWeb.Core.Security.CompanyService.DefaultBranchCode,
            LocationCode = ErpWeb.Core.Security.CompanyService.DefaultLocationCode
        };

    private static Company CloneForEdit(Company data) =>
        new()
        {
            CompanyId = data.CompanyId,
            CompanyCode = data.CompanyCode,
            CompanyName = data.CompanyName,
            LegalName = data.LegalName,
            RegistrationNo = data.RegistrationNo,
            TaxNo = data.TaxNo,
            Phone = data.Phone,
            Fax = data.Fax,
            Email = data.Email,
            Website = data.Website,
            Address1 = data.Address1,
            Address2 = data.Address2,
            Address3 = data.Address3,
            City = data.City,
            State = data.State,
            PostCode = data.PostCode,
            Country = data.Country,
            LogoUrl = data.LogoUrl,
            CurrencyCode = data.CurrencyCode,
            TimeZoneId = data.TimeZoneId,
            FiscalYearStartMonth = data.FiscalYearStartMonth,
            IsActive = data.IsActive,
            CreatedDate = data.CreatedDate,
            CreatedBy = data.CreatedBy,
            ModifiedDate = data.ModifiedDate,
            ModifiedBy = data.ModifiedBy
        };

    protected sealed record FiscalMonthOption(byte? Value, string Name);
}
