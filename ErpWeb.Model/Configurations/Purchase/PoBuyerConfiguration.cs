using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoBuyerConfiguration : IEntityTypeConfiguration<PoBuyer>
{
    public void Configure(EntityTypeBuilder<PoBuyer> builder)
    {
        builder.ToTable("POBuyer");
        builder.HasKey(e => new { e.CompanyCode, e.BuyerCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BuyerCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.BuyerName).HasMaxLength(100);
        builder.Property(e => e.BuyerDesc).HasMaxLength(200);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
