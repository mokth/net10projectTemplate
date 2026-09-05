namespace ErpWeb.Core.Sales;

public sealed class SaCustListQuery
{
    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }
    public string? CustType { get; set; }
    public string? CustGroupCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class SaCustListRow
{
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public string? CustShortName { get; init; }
    public string? CustType { get; init; }
    public string? CustGroupCode { get; init; }
    public string? SalesmanCode { get; init; }
    public string? AreaCode { get; init; }
    public string? City { get; init; }
    public string? Tel { get; init; }
    public string? PayCode { get; init; }
    public string? Currency { get; init; }
    public decimal? CreditLimit { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaCustListPage
{
    public IReadOnlyList<SaCustListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaCustAddressVm
{
    public int Line { get; set; }
    public string? AddName { get; set; }
    public string? DeliverTo { get; set; }
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
}

public sealed class SaCustContactVm
{
    public int Line { get; set; }
    public string? ContactPerson { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactTelp { get; set; }
    public string? ContactFax { get; set; }
}

public sealed class SaCustEditVm
{
    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; set; }
    public string? CustShortName { get; set; }
    public string? CustType { get; set; }
    public string? InvoicePrefix { get; set; }
    public string? CustGroupCode { get; set; }
    public bool? LmwAts { get; set; }
    public string? SalesmanCode { get; set; }
    public string? AreaCode { get; set; }
    public string? SubGroupCode { get; set; }
    public string? IndustryCode { get; set; }
    public string? ChannelCode { get; set; }
    public bool IsActive { get; set; } = true;

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
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? CjLmw { get; set; }
    public string? CustBrn { get; set; }
    public string? RegType { get; set; }
    public string? Remark { get; set; }
    public bool? AppInvoice { get; set; }
    public bool? AppShip { get; set; }

    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? ShipCountry { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }
    public string? ShipEmail { get; set; }
    public string? ShipWebsite { get; set; }

    public List<SaCustAddressVm> Addresses { get; set; } = [];
    public List<SaCustContactVm> Contacts { get; set; } = [];

    public bool? Taxable { get; set; }
    public string? TaxGrCode { get; set; }
    public string? GstregNo { get; set; }
    public string? PayCode { get; set; }
    public string? Currency { get; set; }
    public string? GroupDiscount { get; set; }
    public string? DiscountMethod { get; set; }
    public string? PriceMethod { get; set; }
    public string? AgingType { get; set; }
    public decimal? PaidUpCapital { get; set; }
    public string? GlCode { get; set; }
    public decimal? OpeningAmount { get; set; }
    public string? CreditTerm { get; set; }
    public decimal? CreditLimit { get; set; }
    public string? CustPriceCode { get; set; }

    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public static class SaCustSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaCustListRow.CustCode),
        nameof(SaCustListRow.CustName),
        nameof(SaCustListRow.CustType),
        nameof(SaCustListRow.CustGroupCode),
        nameof(SaCustListRow.SalesmanCode),
        nameof(SaCustListRow.City),
        nameof(SaCustListRow.Tel),
        nameof(SaCustListRow.IsActive)
    };
}
