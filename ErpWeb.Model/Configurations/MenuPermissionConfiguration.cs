using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class MenuPermissionConfiguration : IEntityTypeConfiguration<MenuPermission>
{
    public void Configure(EntityTypeBuilder<MenuPermission> builder)
    {
        builder.ToTable("MenuPermission");
        builder.HasKey(e => new { e.MenuId, e.PermissionId });

        builder.Property(e => e.IsActive).HasDefaultValue(true);

        builder.HasIndex(e => new { e.MenuId, e.PermissionId })
            .IsUnique()
            .HasDatabaseName("UQ_MenuPermission_Menu_Permission");

        builder.HasOne(e => e.Menu)
            .WithMany(e => e.MenuPermissions)
            .HasForeignKey(e => e.MenuId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Permission)
            .WithMany(e => e.MenuPermissions)
            .HasForeignKey(e => e.PermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
