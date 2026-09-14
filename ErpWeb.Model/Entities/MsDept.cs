namespace ErpWeb.Model.Entities;

/// <summary>
/// Department reference master. Company + branch scoped; DeptCode matches the
/// nvarchar(20) Dept/DeptCode columns on sales and purchase transactions.
/// </summary>
public class MsDept
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DeptCode { get; set; } = string.Empty;
    public string? DeptName { get; set; }

    /// <summary>Free text — no employee master exists in this scope.</summary>
    public string? ManagerEmpId { get; set; }

    /// <summary>Cost-centre GL code (nvarchar(20), matching the other GL-code columns).</summary>
    public string? GlCode { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Remarks { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[]? RowVersion { get; set; }
}
