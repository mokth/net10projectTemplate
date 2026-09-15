using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Model.Configurations.Sales;

public class SaLMWConfiguration : IEntityTypeConfiguration<SaLMW>
{
    public void Configure(EntityTypeBuilder<SaLMW> builder)
    {
        builder.ToTable("SaLMW");
        builder.HasKey(e => new { e.CompanyCode, e.LicenseNo, e.CustCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LicenseNo).HasMaxLength(40).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.LicenseID).HasMaxLength(40);
        builder.Property(e => e.LicenseType).HasMaxLength(20);

        // Day-granular windows: SQL `date`, never `datetime`. The inclusive-endpoint overlap
        // rule in the plan §9.3 is only total because these are NOT NULL.
        builder.Property(e => e.LicenseStartDate).HasColumnType("date");
        builder.Property(e => e.LicenseEndDate).HasColumnType("date");
        builder.Property(e => e.SystemStartDate).HasColumnType("date");
        builder.Property(e => e.SystemEndDate).HasColumnType("date");

        builder.Property(e => e.Name).HasMaxLength(100);
        builder.Property(e => e.IC).HasMaxLength(30);
        builder.Property(e => e.Position).HasMaxLength(50);
        builder.Property(e => e.CustName).HasMaxLength(200);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        // CK_SaLMW_LicenseDates / CK_SaLMW_SystemDates / CK_SaLMW_Contained and the
        // IX_SaLMW_Overlap range-scan index live in scripts/init-sales-master-refs.sql
        // (SQL Server, deployed manually). They are not declared here: the index uses
        // INCLUDE and the constraints use T-SQL, while SQLite tests build their schema with
        // EnsureCreated. The service re-enforces ordering + containment + overlap (D-8/D-9).
    }
}
