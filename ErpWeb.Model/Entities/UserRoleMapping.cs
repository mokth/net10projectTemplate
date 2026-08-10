namespace ErpWeb.Model.Entities;

public class UserRoleMapping
{
    public int UserUid { get; set; }
    public int RoleId { get; set; }

    public Role Role { get; set; } = null!;
}
