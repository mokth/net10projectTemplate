using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvLotConfiguration : IEntityTypeConfiguration<IvLot>
{
    public void Configure(EntityTypeBuilder<IvLot> builder)
    {
        builder.ToTable("IvLot");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.LotNo).HasMaxLength(50).IsRequired();
        builder.Property(e => e.SourceType).HasMaxLength(20);
        builder.Property(e => e.SourceDocNo).HasMaxLength(50);
        builder.Property(e => e.SupplierCode).HasMaxLength(20);
        builder.Property(e => e.QcStatus).HasMaxLength(10);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);

        builder.HasIndex(e => new { e.CompanyCode, e.ICode, e.LotNo })
            .IsUnique()
            .HasDatabaseName("UQ_IvLot_Company_ICode_LotNo");

        builder.HasIndex(e => new { e.CompanyCode, e.LotNo })
            .HasDatabaseName("IX_IvLot_Company_LotNo");

        builder.HasIndex(e => new { e.CompanyCode, e.SupplierCode })
            .HasDatabaseName("IX_IvLot_Company_SupplierCode");

        builder.HasOne(e => e.StockMaster)
            .WithMany(e => e.Lots)
            .HasForeignKey(e => new { e.CompanyCode, e.ICode })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
