using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public sealed class IvItemUomConversionConfiguration : IEntityTypeConfiguration<IvItemUomConversion>
{
    public void Configure(EntityTypeBuilder<IvItemUomConversion> builder)
    {
        builder.ToTable("IvItemUomConversion");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        // Legacy IvStockMaster.ICode is nvarchar(20); FK columns must match exactly.
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.FromUom).HasColumnName("FromUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.ToUom).HasColumnName("ToUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.FromQty).HasPrecision(18, 8);
        builder.Property(x => x.ToQty).HasPrecision(18, 8);
        builder.Property(x => x.RoundingMode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.ItemCode, x.FromUom, x.ToUom })
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_IvItemUomConversion_ActivePair");
        builder.HasOne(x => x.Item)
            .WithMany(x => x.UomConversions)
            .HasForeignKey(x => new { x.CompanyCode, x.ItemCode })
            .OnDelete(DeleteBehavior.NoAction);
    }
}
