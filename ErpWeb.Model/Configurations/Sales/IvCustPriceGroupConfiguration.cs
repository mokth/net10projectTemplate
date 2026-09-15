using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class IvCustPriceGroupConfiguration : IEntityTypeConfiguration<IvCustPriceGroup>
{
    public void Configure(EntityTypeBuilder<IvCustPriceGroup> builder)
    {
        builder.ToTable("IvCustPriceGroup");
        builder.HasKey(e => new { e.CompanyCode, e.CustPriceCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustPriceCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CustPriceDesc).HasMaxLength(50);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.IsActive })
            .HasDatabaseName("IX_IvCustPriceGroup_Company_Active");

        // No cascade: deleting a header is blocked in the service while lines exist.
        builder.HasMany(e => e.Lines)
            .WithOne()
            .HasForeignKey(l => new { l.CompanyCode, l.CustPriceCode })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
