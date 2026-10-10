using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Inventory;

public sealed class IvStockMasterImageConfiguration : IEntityTypeConfiguration<IvStockMasterImage>
{
    public void Configure(EntityTypeBuilder<IvStockMasterImage> builder)
    {
        builder.ToTable("IvStockMasterImage");
        builder.HasKey(x => x.Uid);

        builder.Property(x => x.Uid).HasColumnName("UID").UseIdentityColumn();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ICode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ImagePath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.CreatedDate);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);

        builder.HasIndex(x => new { x.CompanyCode, x.ICode, x.ImagePath })
            .IsUnique()
            .HasDatabaseName("UX_IvStockMasterImage_Company_Item_Path");
        builder.HasIndex(x => new { x.CompanyCode, x.ICode, x.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_IvStockMasterImage_Company_Item_SortOrder");
        builder.HasIndex(x => new { x.CompanyCode, x.ICode })
            .HasDatabaseName("IX_IvStockMasterImage_Company_Item");

        builder.HasOne(x => x.Item)
            .WithMany(x => x.Images)
            .HasForeignKey(x => new { x.CompanyCode, x.ICode })
            .HasPrincipalKey(x => new { x.CompanyCode, x.ICode })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
