namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Price list / customer price-group header. A customer points at one of these through
/// <c>SaCust.CustPriceCode</c>; the lines live in <see cref="IvCustPrice"/>.
/// </summary>
public class IvCustPriceGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustPriceCode { get; set; } = string.Empty;
    public string? CustPriceDesc { get; set; }

    /// <summary>Retired lists stay out of assignment lists; existing assignments are tolerated.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public ICollection<IvCustPrice> Lines { get; set; } = new List<IvCustPrice>();
}
