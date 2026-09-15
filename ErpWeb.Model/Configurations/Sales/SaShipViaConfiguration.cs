using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Model.Configurations.Sales;

public class SaShipViaConfiguration : IEntityTypeConfiguration<SaShipVia>
{
    public void Configure(EntityTypeBuilder<SaShipVia> builder)
    {
        builder.ToTable("SaShipVia");
        builder.HasKey(e => new { e.CompanyCode, e.ShipViaCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ShipViaCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ShipViaDesc).HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
