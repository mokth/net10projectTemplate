namespace ErpWeb.Model.Entities;

/// <summary>
/// Project reference master. Company + branch scoped; ProjCode matches the
/// nvarchar(20) ProjID columns on sales and purchase transactions.
/// Actual cost is derived by reporting over transaction headers — there is no
/// ActualAmnt column.
/// </summary>
public class MsProject
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ProjCode { get; set; } = string.Empty;
    public string? ProjName { get; set; }

    /// <summary>Customer / contract link, same company scope as SaCust.</summary>
    public string? CustCode { get; set; }

    /// <summary>Default department for the project (logical FK to MsDept).</summary>
    public string? DeptCode { get; set; }

    /// <summary>Free text — no employee master exists in this scope.</summary>
    public string? ManagerEmpId { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? CloseDate { get; set; }

    /// <summary>ACTIVE / CLOSED.</summary>
    public string Status { get; set; } = MsProjectStatus.Active;

    /// <summary>Budget only — actual cost is derived from transactions.</summary>
    public decimal? BudgetAmnt { get; set; }

    public string? Remarks { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[]? RowVersion { get; set; }
}

public static class MsProjectStatus
{
    public const string Active = "ACTIVE";
    public const string Closed = "CLOSED";

    public static readonly string[] All = [Active, Closed];

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Closed, StringComparison.OrdinalIgnoreCase) ? Closed : Active;
}
