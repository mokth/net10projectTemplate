namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCustGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustGroupCode { get; set; } = string.Empty;
    public string? CustGroupDesc { get; set; }

    /// <summary>
    /// Phase 5: the default price list for this group. Consulted ONLY as a fallback — when
    /// <c>SaCust.CustPriceCode</c> is set the customer's own assignment always wins. Blank means
    /// "no group default", and the price walk continues to the item's selling price.
    /// Width matches <c>SaCust.CustPriceCode</c> (20).
    /// </summary>
    public string? CustPriceCode { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
