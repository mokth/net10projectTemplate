using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Model.Configurations.Sales;

public class SaShippingLeadTimeConfiguration : IEntityTypeConfiguration<SaShippingLeadTime>
{
    public void Configure(EntityTypeBuilder<SaShippingLeadTime> builder)
    {
        builder.ToTable("SaShippingLeadTime");
        builder.HasKey(e => new { e.CompanyCode, e.LeadTimeCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LeadTimeCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LeadTimeDesc).HasMaxLength(100);
        builder.Property(e => e.Days);
        builder.Property(e => e.Type).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        // CK_SaShippingLeadTime_Days / CK_SaShippingLeadTime_Type exist in
        // scripts/init-sales-master-refs.sql (SQL Server, deployed manually). They are not
        // declared here: the constraint text is T-SQL (`N'INTERNAL'`, `[Type]`) while SQLite
        // tests build their schema with EnsureCreated. The service is the primary guard (D-5).
    }
}
