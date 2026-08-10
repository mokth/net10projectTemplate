using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class MenuConfiguration : IEntityTypeConfiguration<Menu>
{
    public void Configure(EntityTypeBuilder<Menu> builder)
    {
        builder.ToTable("Menu");
        builder.HasKey(e => e.MenuId);

        builder.Property(e => e.MenuCode).HasMaxLength(50).IsRequired();
        builder.Property(e => e.MenuName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Route).HasMaxLength(200);
        builder.Property(e => e.Icon).HasMaxLength(100);
        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        builder.HasIndex(e => e.MenuCode)
            .IsUnique()
            .HasDatabaseName("UQ_Menu_MenuCode");

        builder.HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentMenuId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
