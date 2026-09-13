using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoBuyingTermConfiguration : IEntityTypeConfiguration<PoBuyingTerm>
{
    public void Configure(EntityTypeBuilder<PoBuyingTerm> builder)
    {
        builder.ToTable("POBuyingTerm");
        builder.HasKey(e => new { e.CompanyCode, e.BuyingTerm });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BuyingTerm).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(200);
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
