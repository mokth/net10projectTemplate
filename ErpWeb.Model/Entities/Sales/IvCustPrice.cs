namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// One item price inside a price list. Child of <see cref="IvCustPriceGroup"/>; the header carries the
/// row version, so lines have none (they are saved in the same transaction).
/// <para>
/// A price belongs to a UOM: the same item may hold several rows, one per UOM. There is no automatic
/// UOM conversion anywhere in this repository.
/// </para>
/// </summary>
public class IvCustPrice
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustPriceCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string UOM { get; set; } = string.Empty;

    public string? IDesc { get; set; }

    /// <summary>Denormalised from the header, as in the legacy table.</summary>
    public string? CustPriceDesc { get; set; }

    /// <summary>Tax-EXCLUSIVE commercial price (decimal(18,4)).</summary>
    public decimal? SellingPrice { get; set; }

    /// <summary>Advisory pack size; never used in pricing arithmetic.</summary>
    public decimal? SellPackSize { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
