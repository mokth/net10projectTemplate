using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SalesReturnStandardCostVarianceConfiguration
    : IEntityTypeConfiguration<SalesReturnStandardCostVariance>
{
    public void Configure(EntityTypeBuilder<SalesReturnStandardCostVariance> builder)
    {
        builder.ToTable("SalesReturnStandardCostVariance", table =>
        {
            table.HasCheckConstraint("CK_SalesReturnStandardCostVariance_Qty", "[BaseQty] > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ReturnDocumentType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ReturnDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(19, 6);
        builder.Property(x => x.CurrentStandardReceiptValue).HasPrecision(19, 6);
        builder.Property(x => x.OriginalCogsReversalValue).HasPrecision(19, 6);
        builder.Property(x => x.VarianceAmount).HasPrecision(19, 6);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.ReturnValuationFact).WithMany()
            .HasForeignKey(x => x.ReturnValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesVariance).WithMany()
            .HasForeignKey(x => x.ReversesVarianceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ReturnValuationFactId })
            .HasDatabaseName("IX_SalesReturnStandardCostVariance_ReturnFact");
        builder.HasIndex(x => x.ReversesVarianceId).IsUnique()
            .HasFilter("[ReversesVarianceId] IS NOT NULL")
            .HasDatabaseName("UQ_SalesReturnStandardCostVariance_Reversal");
    }
}
