using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class UserLoginConfiguration : IEntityTypeConfiguration<UserLogin>
{
    public void Configure(EntityTypeBuilder<UserLogin> builder)
    {
        builder.ToTable("userlogin");
        builder.HasKey(e => e.uid);

        builder.Property(e => e.id).HasMaxLength(10).IsRequired();
        builder.Property(e => e.name).HasMaxLength(50).IsRequired();
        builder.Property(e => e.password).HasMaxLength(100).IsRequired();
        builder.Property(e => e.email).HasMaxLength(50);
        builder.Property(e => e.mobileno).HasMaxLength(20);
        builder.Property(e => e.userlevel).HasMaxLength(20);
        builder.Property(e => e.UserID).HasMaxLength(10);
        builder.Property(e => e.UpdatedUID).HasMaxLength(10);
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.LocationCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ImagePath).HasMaxLength(100);
        builder.Property(e => e.changepass).IsRequired();

        builder.HasIndex(e => new { e.id, e.CompanyCode }).IsUnique();
    }
}
