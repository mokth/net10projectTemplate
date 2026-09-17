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
    /// <summary>
    /// The sentinel <see cref="ValidFrom"/> that means "this line has always been effective".
    /// Migration maps every pre-existing row to this value.
    /// </summary>
    public static readonly DateTime AlwaysValidFrom = new(1900, 1, 1);

    /// <summary>
    /// Surrogate primary key (Phase 3). The natural key CANNOT be the primary key: two quantity bands
    /// legitimately start on the SAME <see cref="ValidFrom"/> (a MinQty 1 tier and a MinQty 10 tier both
    /// effective 2026-09-01), so the natural key is enforced by a separate unique index instead.
    /// </summary>
    public int Id { get; set; }

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

    /// <summary>
    /// The date this line becomes effective, compared on the DATE PART. The sentinel
    /// <see cref="AlwaysValidFrom"/> means "always". NOT NULL so the natural unique index can contain
    /// it: SQL Server treats NULLs as equal in a unique index, which would allow only ONE undated row
    /// per key.
    /// </summary>
    public DateTime ValidFrom { get; set; } = AlwaysValidFrom;

    /// <summary>Last effective date, inclusive. NULL = open-ended. Deliberately NOT part of the key.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>
    /// Quantity band floor, inclusive. 0 = any quantity. NOT NULL for the same reason as
    /// <see cref="ValidFrom"/>.
    /// </summary>
    public decimal MinQty { get; set; }

    /// <summary>Quantity band ceiling, inclusive. NULL = unlimited. Deliberately NOT part of the key.</summary>
    public decimal? MaxQty { get; set; }

    /// <summary>Currency of this line; blank/NULL = the company base currency.</summary>
    public string? CurrencyCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
