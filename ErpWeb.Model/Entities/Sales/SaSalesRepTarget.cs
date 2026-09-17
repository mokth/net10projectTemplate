namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Company-wide monthly sales target for a sales rep (sales-analysis Phase 1).
/// <para>
/// The key is <c>(CompanyCode, SRepCode, Year, Month)</c>. There is deliberately no
/// <c>BranchCode</c>: <see cref="SaSalesRep"/> is company-scoped, so attainment is always
/// company-wide and the optional branch filter on Sales Summary never changes a target.
/// </para>
/// <para>
/// Targets are month-based and are never prorated — a date range that touches a month consumes
/// that month's full target. A missing row means a target of zero for that month.
/// </para>
/// </summary>
public class SaSalesRepTarget
{
    public string CompanyCode { get; set; } = string.Empty;
    public string SrepCode { get; set; } = string.Empty;

    /// <summary>Calendar year of the target. Validated &gt; 0 on save.</summary>
    public int Year { get; set; }

    /// <summary>Calendar month of the target, 1..12. Validated on save.</summary>
    public int Month { get; set; }

    /// <summary>Monthly target amount. Validated &gt;= 0 on save; 0 is a legitimate explicit target.</summary>
    public decimal TargetAmount { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
