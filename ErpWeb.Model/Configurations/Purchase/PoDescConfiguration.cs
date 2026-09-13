using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoDescConfiguration : IEntityTypeConfiguration<PoDesc>
{
    public void Configure(EntityTypeBuilder<PoDesc> builder)
    {
        builder.ToTable("PODesc");
        builder.HasKey(e => new { e.CompanyCode, e.ItemDesc });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ItemDesc).HasMaxLength(200).IsRequired();
        builder.Property(e => e.DeptCode).HasMaxLength(20);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
