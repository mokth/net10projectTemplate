using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvStockCountLineConfiguration : IEntityTypeConfiguration<IvStockCountLine>
{
    public void Configure(EntityTypeBuilder<IvStockCountLine> builder)
    {
        builder.ToTable("IvStockCountLine");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.ICode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.WHCode).HasMaxLength(20);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.LotNo).HasMaxLength(50);
        builder.Property(e => e.IStatus).HasMaxLength(20).IsRequired();
        builder.Property(e => e.IClassCode).HasMaxLength(10);
        builder.Property(e => e.StdUom).HasMaxLength(10);
        builder.Property(e => e.CountedBy).HasMaxLength(10);

        builder.Property(e => e.SystemQty).HasPrecision(18, 4);
        builder.Property(e => e.PhysicalQty).HasPrecision(18, 4);
        builder.Property(e => e.SnapshotUnitPrice).HasPrecision(18, 4);

        builder.Property(e => e.RowVersion).IsRowVersion();

        // One line per pile — this is what makes the post-time net-per-BalLoc unambiguous.
        builder.HasIndex(e => new { e.StockCountId, e.BalLocId })
            .IsUnique()
            .HasDatabaseName("UQ_IvStockCountLine_BalLoc");

        builder.HasIndex(e => new { e.StockCountId, e.LineNumber })
            .IsUnique()
            .HasDatabaseName("UQ_IvStockCountLine_No");
    }
}
