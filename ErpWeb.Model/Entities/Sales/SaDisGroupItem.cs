namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Item discount rule: discount slots that apply to one item inside a quantity band and a date window.
/// <para>
/// Deliberate differences from the legacy table: <c>ID</c> is <c>int</c> (never <c>smallint</c>), quantities
/// and discounts are <c>decimal(18,4)</c> (never <c>float</c>), and the window is <c>date</c> (never
/// <c>datetime</c>) so a stored time component cannot exclude the boundary day. The legacy
/// <c>GroupName</c>/<c>GroupLevel</c>/<c>Discount2</c>/<c>Discount3</c> columns are not carried — they were
/// write-only and read by no consumer.
/// </para>
/// </summary>
public class SaDisGroupItem
{
    public int Id { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }

    /// <summary>Item class; NULL/blank = applies to all classes.</summary>
    public string? IClass { get; set; }

    /// <summary>Band start, inclusive. Must be greater than zero.</summary>
    public decimal QtyFr { get; set; }

    /// <summary>Band end, inclusive. Must be greater than or equal to <see cref="QtyFr"/>.</summary>
    public decimal QtyTo { get; set; }

    /// <summary>Window start, inclusive (required).</summary>
    public DateTime DateFr { get; set; }

    /// <summary>Window end, inclusive; NULL = open-ended.</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>Slot A value.</summary>
    public decimal? Discount { get; set; }

    /// <summary>Slot A type: <c>PERCENTAGE</c> or <c>AMOUNT</c>.</summary>
    public string? DiscountType { get; set; }

    /// <summary>Slot B value.</summary>
    public decimal? Discount1 { get; set; }

    /// <summary>Slot B type: <c>PERCENTAGE</c> or <c>AMOUNT</c>.</summary>
    public string? DiscountType1 { get; set; }

    /// <summary>
    /// <c>DEALER</c> or <c>SELLING</c>. Stored and validated, but INERT in this phase: no dealer price
    /// source exists anywhere in this repository, and no consumer query studied reads this column.
    /// </summary>
    public string? EffectPrice { get; set; }

    /// <summary>Legacy compatibility column; written but not authoritative, not exposed in the UI.</summary>
    public string? GroupStatus { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
