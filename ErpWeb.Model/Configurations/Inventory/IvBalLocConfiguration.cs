using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvBalLocConfiguration : IEntityTypeConfiguration<IvBalLoc>
{
    public void Configure(EntityTypeBuilder<IvBalLoc> builder)
    {
        builder.ToTable("IvBalLoc");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.WhCode).HasColumnName("WHCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.LocCode).HasMaxLength(10).IsRequired().HasDefaultValue("");
        builder.Property(e => e.LotNo).HasMaxLength(50).IsRequired().HasDefaultValue("");
        builder.Property(e => e.IStatus).HasColumnName("IStatus").HasMaxLength(10).IsRequired().HasDefaultValue("");
        builder.Property(e => e.RefNo).HasMaxLength(50);
        builder.Property(e => e.StdQty).HasPrecision(18, 4).IsRequired();
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.PoNo).HasColumnName("PO_No").HasMaxLength(30);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.Cost).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.ICode, e.WhCode, e.LocCode, e.LotNo, e.IStatus })
            .IsUnique()
            .HasDatabaseName("UQ_IvBalLoc_StockSlice");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.ICode, e.WhCode })
            .HasDatabaseName("IX_IvBalLoc_ICode_WhCode");

        builder.HasIndex(e => e.LotId)
            .HasDatabaseName("IX_IvBalLoc_LotId");

        builder.HasOne(e => e.StockMaster)
            .WithMany(e => e.Balances)
            .HasForeignKey(e => new { e.CompanyCode, e.ICode })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Warehouse)
            .WithMany(e => e.Balances)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.WhCode })
            .HasPrincipalKey(e => new { e.CompanyCode, e.BranchCode, e.WarehouseCode })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Lot)
            .WithMany(e => e.Balances)
            .HasForeignKey(e => e.LotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
