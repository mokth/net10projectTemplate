using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvTypeConfiguration : IEntityTypeConfiguration<IvType>
{
    public void Configure(EntityTypeBuilder<IvType> builder)
    {
        builder.ToTable("IvType");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.TypeCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.TypeName).HasMaxLength(100);
        builder.Property(e => e.TypeDesc).HasMaxLength(200);
        builder.Property(e => e.KeepStock).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.TypeCode })
            .IsUnique()
            .HasDatabaseName("UQ_IvType_Company_TypeCode");

        builder.HasIndex(e => e.CompanyCode)
            .HasDatabaseName("IX_IvType_CompanyCode");
    }
}
