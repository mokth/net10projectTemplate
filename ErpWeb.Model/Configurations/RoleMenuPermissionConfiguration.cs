using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class RoleMenuPermissionConfiguration : IEntityTypeConfiguration<RoleMenuPermission>
{
    public void Configure(EntityTypeBuilder<RoleMenuPermission> builder)
    {
        builder.ToTable("RoleMenuPermission");
        builder.HasKey(e => e.RoleMenuPermissionId);

        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);

        builder.HasIndex(e => new { e.RoleId, e.MenuId, e.PermissionId })
            .IsUnique()
            .HasDatabaseName("UQ_RoleMenuPermission_Role_Menu_Permission");

        builder.HasIndex(e => new { e.MenuId, e.PermissionId });

        builder.HasOne(e => e.Role)
            .WithMany(e => e.RoleMenuPermissions)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Menu)
            .WithMany(e => e.RoleMenuPermissions)
            .HasForeignKey(e => e.MenuId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Permission)
            .WithMany(e => e.RoleMenuPermissions)
            .HasForeignKey(e => e.PermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
