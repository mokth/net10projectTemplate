namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Customer-specific item: the customer's own part number plus the selling defaults used when that item
/// is sold to that customer. Business key = (CompanyCode, CustCode, ICode, SellingUOM, MOQ) — one row per
/// customer/item/UOM/MOQ band, which is what allows "same item, different UOM, different price".
/// <para>
/// Live legacy types are preserved deliberately: <see cref="UnitPrice"/> is <c>float</c> (<c>double</c> here)
/// and <see cref="MOQ"/> is <c>int</c>. Money is scaled explicitly at the service boundary; fractional MOQ
/// is not supported by this schema.
/// </para>
/// </summary>
public class SaItemCust
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string SellingUOM { get; set; } = string.Empty;

    /// <summary>Minimum order quantity band; 0/blank row = the base row for the item/UOM.</summary>
    public int MOQ { get; set; }

    public string? IDesc { get; set; }

    /// <summary>Customer's own item code (NOT NULL in the live schema).</summary>
    public string CustICode { get; set; } = string.Empty;

    /// <summary>Invoice description override.</summary>
    public string? InvDesc { get; set; }

    /// <summary>Tax-EXCLUSIVE customer-item price (legacy <c>float</c>).</summary>
    public double? UnitPrice { get; set; }

    /// <summary>When non-blank and different from the document currency, pricing fails closed.</summary>
    public string? Currency { get; set; }

    /// <summary>Advisory standard pack size (legacy <c>float</c>).</summary>
    public double? StdCustPSize { get; set; }

    /// <summary>
    /// Refresh state, NOT activation: <c>NEW</c> = current, <c>FALSE</c> = flagged for refresh by the
    /// legacy refresh flow. There is no active/inactive toggle on this master.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>Legacy flag, always false from the legacy save path.</summary>
    public bool? SPart { get; set; }

    /// <summary>Legacy unexplained column (string written, read as int elsewhere) — carried, never parsed here.</summary>
    public string? DG { get; set; }

    /// <summary>Legacy column, never written by the studied screens — carried as-is.</summary>
    public string? SG { get; set; }

    /// <summary>Legacy project tag; never written by the studied screens — carried as-is.</summary>
    public string? ProjID { get; set; }

    /// <summary>Legacy column that is absent from the logic spec — carried so no value is lost.</summary>
    public string? CustModel { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
