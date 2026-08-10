namespace ErpWeb.Model.Entities;

public class Branch : SoftDeletableCompanyEntity
{
    public string BranchCode { get; set; } = null!;
    public string BranchName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
