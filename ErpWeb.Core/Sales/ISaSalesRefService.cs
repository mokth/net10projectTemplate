using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaSalesRefService
{
    // Customer Type
    Task<IvMasterOperationResult<IReadOnlyList<SaCustTypeListRow>>> ListCustTypesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustTypeEditVm>> GetCustTypeAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustTypeEditVm>> SaveCustTypeAsync(SaCustTypeEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCustTypeActiveAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCustTypesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCustTypesAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Customer Group
    Task<IvMasterOperationResult<IReadOnlyList<SaCustGroupListRow>>> ListCustGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustGroupEditVm>> GetCustGroupAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustGroupEditVm>> SaveCustGroupAsync(SaCustGroupEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCustGroupsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCustGroupsAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Area
    Task<IvMasterOperationResult<IReadOnlyList<SaAreaListRow>>> ListAreasAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaAreaEditVm>> GetAreaAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaAreaEditVm>> SaveAreaAsync(SaAreaEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteAreasAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteAreasAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    // Country (global)
    Task<IvMasterOperationResult<IReadOnlyList<SaCountryListRow>>> ListCountriesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCountryEditVm>> GetCountryAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCountryEditVm>> SaveCountryAsync(SaCountryEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCountriesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCountriesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    // Currency
    Task<IvMasterOperationResult<IReadOnlyList<SaCurrencyListRow>>> ListCurrenciesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCurrencyEditVm>> GetCurrencyAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCurrencyEditVm>> SaveCurrencyAsync(SaCurrencyEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCurrencyActiveAsync(IReadOnlyList<string> codes, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCurrenciesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCurrenciesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    // Discount Group
    Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupListRow>>> ListDisGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaDisGroupEditVm>> GetDisGroupAsync(SaDisGroupKey key, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaDisGroupEditVm>> SaveDisGroupAsync(SaDisGroupEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteDisGroupsAsync(IReadOnlyList<SaDisGroupKey> keys, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteDisGroupsAsync(IReadOnlyList<SaDisGroupKey> keys, CancellationToken cancellationToken = default);

    // Currency Rate
    Task<IvMasterOperationResult<IReadOnlyList<SaCurrRateListRow>>> ListCurrRatesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCurrRateEditVm>> GetCurrRateAsync(SaCurrRateKey key, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCurrRateEditVm>> SaveCurrRateAsync(SaCurrRateEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCurrRatesAsync(IReadOnlyList<SaCurrRateKey> keys, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCurrRatesAsync(IReadOnlyList<SaCurrRateKey> keys, CancellationToken cancellationToken = default);

    // Payment Term
    Task<IvMasterOperationResult<IReadOnlyList<SaPaymentTermListRow>>> ListPaymentTermsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaPaymentTermEditVm>> GetPaymentTermAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaPaymentTermEditVm>> SavePaymentTermAsync(SaPaymentTermEditVm model, bool isNew, string? expectedFingerprint, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetPaymentTermActiveAsync(IReadOnlyList<string> codes, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeletePaymentTermsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeletePaymentTermsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    // Sales Rep
    Task<IvMasterOperationResult<IReadOnlyList<SaSalesRepListRow>>> ListSalesRepsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaSalesRepEditVm>> GetSalesRepAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaSalesRepEditVm>> SaveSalesRepAsync(SaSalesRepEditVm model, bool isNew, string? expectedFingerprint, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetSalesRepActiveAsync(IReadOnlyList<string> codes, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteSalesRepsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteSalesRepsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    // Tax Group (global)
    Task<IvMasterOperationResult<IReadOnlyList<SaTaxGroupListRow>>> ListTaxGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaTaxGroupEditVm>> GetTaxGroupAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaTaxGroupEditVm>> SaveTaxGroupAsync(SaTaxGroupEditVm model, bool isNew, string? expectedFingerprint, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteTaxGroupsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteTaxGroupsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
}

public sealed class SaCompanyMasterKeyToken
{
    public string Code { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDisGroupKey
{
    public string GroupName { get; init; } = string.Empty;
    public string PayCode { get; init; } = string.Empty;
}

public sealed class SaCurrRateKey
{
    public string CurrCode { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
}

public sealed class SaCustTypeListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCustTypeEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class SaCustGroupListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCustGroupEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public byte[]? RowVersion { get; set; }
}

public sealed class SaAreaListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }
}

public sealed class SaAreaEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public string? Latitude { get; set; }
    public string? Longitude { get; set; }
}

public sealed class SaCountryListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
}

public sealed class SaCountryEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}

public sealed class SaCurrencyListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
}

public sealed class SaCurrencyEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SaDisGroupListRow
{
    public string GroupName { get; init; } = string.Empty;
    public string PayCode { get; init; } = string.Empty;
    public short? GroupLevel { get; init; }
    public double? Discount { get; init; }
    public string? GroupStatus { get; init; }
    public int MemberCount { get; init; }
    public string RowKey => $"{GroupName}|{PayCode}";
}

public sealed class SaDisGroupMemberVm
{
    public string CustCode { get; set; } = string.Empty;
    public string CustName { get; set; } = string.Empty;
}

public sealed class SaDisGroupEditVm
{
    public string GroupName { get; set; } = string.Empty;
    public string PayCode { get; set; } = string.Empty;
    public short? GroupLevel { get; set; }
    public double? Discount { get; set; }
    public double? Discount2 { get; set; }
    public double? Discount3 { get; set; }
    public string? DiscountType { get; set; }
    public string? GroupStatus { get; set; }
    public List<SaDisGroupMemberVm> Members { get; set; } = [];
}

public sealed class SaCurrRateListRow
{
    public string CurrCode { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public double HomeCurPerUnit { get; init; }
    public bool Status { get; init; }
    public string RowKey => $"{CurrCode}|{StartDate:yyyyMMdd}|{EndDate:yyyyMMdd}";
}

public sealed class SaCurrRateEditVm
{
    public string CurrCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public double HomeCurPerUnit { get; set; }
    public bool Status { get; set; } = true;
}

public sealed class SaPaymentTermListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public int? Days { get; init; }
    public bool IsActive { get; init; }
}

public sealed class SaPaymentTermEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public int? Days { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SaSalesRepListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Tel { get; init; }
    public string? Email { get; init; }
    public decimal? CommissionRate { get; init; }
    public bool IsActive { get; init; }
}

public sealed class SaSalesRepEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public decimal? CommissionRate { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SaTaxGroupListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public decimal Percentage { get; init; }
    public string CompanyCode { get; init; } = string.Empty;
    public string? BranchCode { get; init; }
    public string? LocationCode { get; init; }
}

public sealed class SaTaxGroupEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public decimal Percentage { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
