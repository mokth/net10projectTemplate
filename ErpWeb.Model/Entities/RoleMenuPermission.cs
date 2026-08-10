namespace ErpWeb.Model.Entities;

public class RoleMenuPermission
{
    public int RoleMenuPermissionId { get; set; }
    public int RoleId { get; set; }
    public int MenuId { get; set; }
    public int PermissionId { get; set; }
    public bool IsAllowed { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public Role Role { get; set; } = null!;
    public Menu Menu { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}
