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

    // -------- Flat sales code-reference family (docs/sales-master-plan.md) --------
    // All six are Level A (DB RowVersion on the VM) — there is no expectedFingerprint argument.

    // Customer Sub Group (company-scoped, no Active)
    Task<IvMasterOperationResult<IReadOnlyList<SaCustSubGroupListRow>>> ListCustSubGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaCustSubGroupListRow>>> ExportCustSubGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustSubGroupEditVm>> GetCustSubGroupAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCustSubGroupEditVm>> SaveCustSubGroupAsync(SaCustSubGroupEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCustSubGroupsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCustSubGroupsAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Ship Via (company-scoped, Active)
    Task<IvMasterOperationResult<IReadOnlyList<SaShipViaListRow>>> ListShipViasAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaShipViaListRow>>> ExportShipViasAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaShipViaEditVm>> GetShipViaAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaShipViaEditVm>> SaveShipViaAsync(SaShipViaEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetShipViaActiveAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteShipViasAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteShipViasAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // SO Type (company-scoped, Active, no FOCAuto)
    Task<IvMasterOperationResult<IReadOnlyList<SaSOTypeListRow>>> ListSoTypesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaSOTypeListRow>>> ExportSoTypesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaSOTypeEditVm>> GetSoTypeAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaSOTypeEditVm>> SaveSoTypeAsync(SaSOTypeEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetSoTypeActiveAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteSoTypesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteSoTypesAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Comment (company-scoped, Active, no Module / no legacy identity ID)
    Task<IvMasterOperationResult<IReadOnlyList<SaCommentListRow>>> ListCommentsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaCommentListRow>>> ExportCommentsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCommentEditVm>> GetCommentAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaCommentEditVm>> SaveCommentAsync(SaCommentEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCommentActiveAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCommentsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCommentsAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Shipping Lead Time (company-scoped, Active, Days + Type)
    Task<IvMasterOperationResult<IReadOnlyList<SaShippingLeadTimeListRow>>> ListShippingLeadTimesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaShippingLeadTimeListRow>>> ExportShippingLeadTimesAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaShippingLeadTimeEditVm>> GetShippingLeadTimeAsync(string code, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaShippingLeadTimeEditVm>> SaveShippingLeadTimeAsync(SaShippingLeadTimeEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetShippingLeadTimeActiveAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteShippingLeadTimesAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteShippingLeadTimesAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // LMW licence register (company + customer scoped, no Active, serializable overlap check)
    Task<IvMasterOperationResult<IReadOnlyList<SaLMWListRow>>> ListLmwsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaLMWListRow>>> ExportLmwsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaLMWEditVm>> GetLmwAsync(string licenseNo, string custCode, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaLMWEditVm>> SaveLmwAsync(SaLMWEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteLmwsAsync(IReadOnlyList<SaLMWKey> keys, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteLmwsAsync(IReadOnlyList<SaCompanyMasterKeyToken> items, CancellationToken cancellationToken = default);

    // ---- Sales item family (plans/sales-item-family-v2-plan.md) ----
    // Price lists, price lines, customer items and item discount rules. Price values are projected only
    // when the caller holds VIEW_PRICE; a denied caller receives nulls (never a zeroed price).

    // Price list header (saved together with its lines in one transaction)
    Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>> ListCustPriceGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>> ExportCustPriceGroupsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> GetCustPriceGroupAsync(string custPriceCode, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> SaveCustPriceGroupAsync(IvCustPriceGroupEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> SetCustPriceGroupActiveAsync(IReadOnlyList<SaItemFamilyKeyToken> items, bool isActive, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteCustPriceGroupsAsync(IReadOnlyList<string> custPriceCodes, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteCustPriceGroupsAsync(IReadOnlyList<SaItemFamilyKeyToken> items, CancellationToken cancellationToken = default);

    // Price lines (read surface for the line list; writes go through the header aggregate)
    Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceListRow>>> ListCustPricesAsync(string custPriceCode, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceListRow>>> ExportCustPricesAsync(string custPriceCode, CancellationToken cancellationToken = default);

    // Customer item
    Task<IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>> ListItemCustsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>> ExportItemCustsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaItemCustEditVm>> GetItemCustAsync(SaItemCustKey key, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaItemCustEditVm>> SaveItemCustAsync(SaItemCustEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteItemCustsAsync(IReadOnlyList<SaItemCustKey> keys, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteItemCustsAsync(IReadOnlyList<SaItemFamilyKeyToken> items, CancellationToken cancellationToken = default);

    // Item discount rules
    Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>> ListDisGroupItemsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>> ExportDisGroupItemsAsync(CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaDisGroupItemEditVm>> GetDisGroupItemAsync(int id, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaDisGroupItemEditVm>> SaveDisGroupItemAsync(SaDisGroupItemEditVm model, bool isNew, CancellationToken cancellationToken = default);
    Task<DeleteCheckResult> CanDeleteDisGroupItemsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<object>> DeleteDisGroupItemsAsync(IReadOnlyList<SaItemFamilyKeyToken> items, CancellationToken cancellationToken = default);

    // Resolution entry points (plan §7/§8/§8.9). The masters above are the data; these are the single
    // place a price or a discount is decided, so the future SO/DO/INV/CN consumer never re-derives one
    // — and today they are what the price/discount contract is tested against.
    Task<IvMasterOperationResult<SaItemFamilyPriceResolution>> ResolveItemPriceAsync(SaItemFamilyPriceRequest request, CancellationToken cancellationToken = default);
    Task<IvMasterOperationResult<SaItemFamilyDiscountSelection>> ResolveItemDiscountAsync(SaItemFamilyDiscountRequest request, CancellationToken cancellationToken = default);
}

public sealed class SaCompanyMasterKeyToken
{
    public string Code { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];

    /// <summary>
    /// Second half of a two-part natural key. Only <c>SaLMW</c> uses it today
    /// (<see cref="Code"/> = LicenseNo, <see cref="ParentCode"/> = CustCode).
    /// </summary>
    public string? ParentCode { get; init; }
}

/// <summary>Natural key of an LMW licence row.</summary>
public sealed class SaLMWKey
{
    public string LicenseNo { get; init; } = string.Empty;
    public string CustCode { get; init; } = string.Empty;
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
    public string? TaxGlCode { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}

// ===================== Flat sales code-reference family =====================

public sealed class SaCustSubGroupListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCustSubGroupEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public byte[]? RowVersion { get; set; }
}

public sealed class SaShipViaListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaShipViaEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class SaSOTypeListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaSOTypeEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class SaCommentListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Comment { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCommentEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class SaShippingLeadTimeListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public int? Days { get; init; }
    public string? Type { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaShippingLeadTimeEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public int? Days { get; set; }
    public string? Type { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class SaLMWListRow
{
    public string LicenseNo { get; init; } = string.Empty;
    public string CustCode { get; init; } = string.Empty;

    /// <summary>Grid selection key: both halves of the natural key, so two licences of one customer stay distinct.</summary>
    public string RowKey => $"{LicenseNo}|{CustCode}";

    public string? LicenseID { get; init; }
    public string? LicenseType { get; init; }
    public DateTime LicenseStartDate { get; init; }
    public DateTime LicenseEndDate { get; init; }
    public DateTime SystemStartDate { get; init; }
    public DateTime SystemEndDate { get; init; }
    public string? Name { get; init; }
    public string? IC { get; init; }
    public string? Position { get; init; }
    public string? CustName { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaLMWEditVm
{
    public string LicenseNo { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public string? LicenseID { get; set; }
    public string? LicenseType { get; set; }
    public DateTime LicenseStartDate { get; set; }
    public DateTime LicenseEndDate { get; set; }
    public DateTime SystemStartDate { get; set; }
    public DateTime SystemEndDate { get; set; }
    public string? Name { get; set; }
    public string? IC { get; set; }
    public string? Position { get; set; }
    public string? CustName { get; set; }
    public byte[]? RowVersion { get; set; }
}
