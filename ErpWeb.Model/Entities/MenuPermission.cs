namespace ErpWeb.Model.Entities;

public class MenuPermission
{
    public int MenuId { get; set; }
    public int PermissionId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public Menu Menu { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}
