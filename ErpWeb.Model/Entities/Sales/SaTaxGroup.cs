namespace ErpWeb.Model.Entities.Sales;

public class SaTaxGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string TaxGrCode { get; set; } = string.Empty;
    public string? TaxGrDesc { get; set; }
    public decimal Percentage { get; set; }
    public string? TaxGlCode { get; set; }

    /// <summary>
    /// LHDN tax type for this group (IvMSCode <c>TAX</c> code, 2 characters — for example <c>06</c>
    /// "Not Applicable"). Blank is normalised to <c>06</c> at save time; e-Invoice lines map
    /// <see cref="TaxGrCode"/> through this column instead of sending the ERP group code.
    /// </summary>
    public string? TaxType { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
