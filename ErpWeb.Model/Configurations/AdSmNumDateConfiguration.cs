using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class AdSmNumDateConfiguration : IEntityTypeConfiguration<AdSmNumDate>
{
    public void Configure(EntityTypeBuilder<AdSmNumDate> builder)
    {
        builder.ToTable("AdSmNumDate");
        builder.HasKey(e => e.Uid);
        builder.Property(e => e.Uid).HasColumnName("uid").ValueGeneratedOnAdd();

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.NumCd).HasMaxLength(20);
        builder.Property(e => e.NumDes).HasMaxLength(30);
        builder.Property(e => e.Prefix).HasMaxLength(20);
        builder.Property(e => e.UserID).HasMaxLength(10);
        builder.Property(e => e.NumberingDelimeter).HasMaxLength(5);
        builder.Property(e => e.NumberingFormat).HasMaxLength(50);
        builder.Property(e => e.Created).HasColumnType("datetime");
        builder.Property(e => e.Updated).HasColumnType("datetime");
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.NumCd, e.Year, e.Month })
            .IsUnique()
            .HasDatabaseName("UX_AdSmNumDate_Tenant_NumCd_Year_Month")
            .HasFilter("[NumCd] IS NOT NULL AND [Year] IS NOT NULL AND [Month] IS NOT NULL");
    }
}
