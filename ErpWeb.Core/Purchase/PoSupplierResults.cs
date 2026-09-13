namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierListQuery
{
    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }
    public string? SuppType { get; set; }
    public string? CategoryCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class PoSupplierListRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string? SuppName { get; init; }
    public string? SuppShortName { get; init; }
    public string? SuppType { get; init; }
    public string? CategoryCode { get; init; }
    public string? CreditorSubGroup { get; init; }
    public string? AreaCode { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }
    public string? Tel { get; init; }
    public string? Email { get; init; }
    public string? SupplierBrn { get; init; }
    public string? PayCode { get; init; }
    public string? Currency { get; init; }
    public string? BuyingTerm { get; init; }
    public string? GlCode { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class PoSupplierListPage
{
    public IReadOnlyList<PoSupplierListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class PoSupplierAddressVm
{
    public int Line { get; set; }
    public string? SuppName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? Address4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }

    public string DisplayText =>
        !string.IsNullOrWhiteSpace(SuppName)
            ? SuppName!
            : $"Line {Line}";
}

public sealed class PoSupplierEditVm
{
    public string SuppCode { get; set; } = string.Empty;
    public string? SuppName { get; set; }
    public string? SuppShortName { get; set; }
    public string? SuppType { get; set; }
    public string? SupplierBrn { get; set; }
    public string? CategoryCode { get; set; }
    public string? CreditorSubGroup { get; set; }
    public string? AreaCode { get; set; }
    public string? RegType { get; set; }
    public string? PoPrefix { get; set; }
    public bool IsActive { get; set; } = true;
    public bool? Lmw { get; set; }
    public string? MiscCode { get; set; }

    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? Address4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }
    public string? Telex { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    public List<PoSupplierAddressVm> Addresses { get; set; } = [];

    public string? ContactPerson { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTelp { get; set; }
    public string? ContactFax { get; set; }

    public string? ContactPerson2 { get; set; }
    public string? Title2 { get; set; }
    public string? Department2 { get; set; }
    public string? ContactEmail2 { get; set; }
    public string? ContactTelp2 { get; set; }
    public string? ContactFax2 { get; set; }

    public string? ContactPerson3 { get; set; }
    public string? Title3 { get; set; }
    public string? Department3 { get; set; }
    public string? ContactEmail3 { get; set; }
    public string? ContactTelp3 { get; set; }
    public string? ContactFax3 { get; set; }

    public string? ContactPerson4 { get; set; }
    public string? Title4 { get; set; }
    public string? Department4 { get; set; }
    public string? ContactEmail4 { get; set; }
    public string? ContactTelp4 { get; set; }
    public string? ContactFax4 { get; set; }

    public bool? Taxable { get; set; }
    public string? TaxGrCode { get; set; }
    public string? GstregNo { get; set; }
    public string? BankName { get; set; }
    public string? AccountNo { get; set; }
    public string? StatementType { get; set; }
    public string? PayCode { get; set; }
    public string? Currency { get; set; }
    public string? BuyingTerm { get; set; }
    public string? GlCode { get; set; }
    public string? AgingType { get; set; }
    public decimal? CreditLimit { get; set; }

    public string? Remark { get; set; }
    public string? BizDesc { get; set; }

    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public static class PoSupplierSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoSupplierListRow.SuppCode),
        nameof(PoSupplierListRow.SuppName),
        nameof(PoSupplierListRow.SuppType),
        nameof(PoSupplierListRow.CategoryCode),
        nameof(PoSupplierListRow.AreaCode),
        nameof(PoSupplierListRow.City),
        nameof(PoSupplierListRow.Tel),
        nameof(PoSupplierListRow.IsActive)
    };
}
