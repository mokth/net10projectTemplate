namespace ErpWeb.Model.Entities;

public class Role
{
    public int RoleId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<UserRoleMapping> UserRoleMappings { get; set; } = new List<UserRoleMapping>();
    public ICollection<RoleMenuPermission> RoleMenuPermissions { get; set; } = new List<RoleMenuPermission>();
}
