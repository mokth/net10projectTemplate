using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionStandardCostVarianceConfiguration : IEntityTypeConfiguration<ProductionStandardCostVariance>
{
    public void Configure(EntityTypeBuilder<ProductionStandardCostVariance> builder)
    {
        builder.ToTable("ProductionStandardCostVariance", table =>
        {
            table.HasCheckConstraint("CK_ProductionStandardCostVariance_Qty", "[BaseQty] > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(19, 6);
        builder.Property(x => x.ActualProductionValue).HasPrecision(19, 6);
        builder.Property(x => x.StandardInventoryValue).HasPrecision(19, 6);
        builder.Property(x => x.VarianceAmount).HasPrecision(19, 6);
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.EffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.InventoryValuationFact).WithMany()
            .HasForeignKey(x => x.InventoryValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesVariance).WithMany()
            .HasForeignKey(x => x.ReversesVarianceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.InventoryValuationFactId)
            .HasDatabaseName("IX_ProductionStandardCostVariance_InventoryFact");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasDatabaseName("IX_ProductionStandardCostVariance_Posting");
        builder.HasIndex(x => x.ReversesVarianceId)
            .IsUnique().HasFilter("[ReversesVarianceId] IS NOT NULL")
            .HasDatabaseName("UQ_ProductionStandardCostVariance_Reversal");
    }
}
