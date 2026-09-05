using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class AdSmNumConfiguration : IEntityTypeConfiguration<AdSmNum>
{
    public void Configure(EntityTypeBuilder<AdSmNum> builder)
    {
        builder.ToTable("AdSmNum");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.NumCd });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.NumCd).HasMaxLength(10).IsRequired();
        builder.Property(e => e.NumDes).HasMaxLength(30);
        builder.Property(e => e.Prefix).HasMaxLength(10);
        builder.Property(e => e.UserID).HasMaxLength(10);
        builder.Property(e => e.UpdatedUID).HasMaxLength(10);
        builder.Property(e => e.Created).HasColumnType("datetime");
        builder.Property(e => e.Updated).HasColumnType("datetime");
    }
}
