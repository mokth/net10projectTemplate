using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDisGroupItemConfiguration : IEntityTypeConfiguration<SaDisGroupItem>
{
    public void Configure(EntityTypeBuilder<SaDisGroupItem> builder)
    {
        builder.ToTable("SaDisGroupItem");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.IClass).HasMaxLength(10);
        builder.Property(e => e.QtyFr).HasPrecision(18, 4);
        builder.Property(e => e.QtyTo).HasPrecision(18, 4);
        builder.Property(e => e.DateFr).HasColumnType("date");
        builder.Property(e => e.DateTo).HasColumnType("date");
        builder.Property(e => e.Discount).HasPrecision(18, 4);
        builder.Property(e => e.Discount1).HasPrecision(18, 4);
        builder.Property(e => e.DiscountType).HasMaxLength(10);
        builder.Property(e => e.DiscountType1).HasMaxLength(10);
        builder.Property(e => e.EffectPrice).HasMaxLength(10);
        builder.Property(e => e.GroupStatus).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.ICode, e.IClass, e.QtyFr, e.QtyTo, e.DateFr })
            .IsUnique()
            .HasDatabaseName("UX_SaDisGroupItem_BusinessKey");
    }
}
