using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockValuationPeriodSnapshotHdrConfiguration : IEntityTypeConfiguration<StockValuationPeriodSnapshotHdr>
{
    public void Configure(EntityTypeBuilder<StockValuationPeriodSnapshotHdr> builder)
    {
        builder.ToTable("StockValuationPeriodSnapshotHdr", table =>
        {
            table.HasCheckConstraint("CK_StockValuationPeriodSnapshotHdr_Revision", "[Revision] > 0");
            table.HasCheckConstraint("CK_StockValuationPeriodSnapshotHdr_Watermark", "[PostingSequenceWatermark] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.PeriodKey).HasColumnType("char(7)").IsRequired();
        builder.Property(x => x.SourceDataHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.ValuationStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.LedgerEpoch).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.LedgerEpochId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PeriodKey, x.Revision })
            .IsUnique().HasDatabaseName("UQ_StockValuationPeriodSnapshotHdr_Revision");
    }
}

public sealed class StockValuationPeriodSnapshotLineConfiguration : IEntityTypeConfiguration<StockValuationPeriodSnapshotLine>
{
    public void Configure(EntityTypeBuilder<StockValuationPeriodSnapshotLine> builder)
    {
        builder.ToTable("StockValuationPeriodSnapshotLine");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.CostMethod).HasMaxLength(30).IsRequired();
        foreach (var property in new[]
                 {
                     nameof(StockValuationPeriodSnapshotLine.OpeningQty), nameof(StockValuationPeriodSnapshotLine.OpeningValue),
                     nameof(StockValuationPeriodSnapshotLine.InQty), nameof(StockValuationPeriodSnapshotLine.InValue),
                     nameof(StockValuationPeriodSnapshotLine.AdjustmentQty), nameof(StockValuationPeriodSnapshotLine.AdjustmentValue),
                     nameof(StockValuationPeriodSnapshotLine.OutQty), nameof(StockValuationPeriodSnapshotLine.OutValue),
                     nameof(StockValuationPeriodSnapshotLine.ClosingQty), nameof(StockValuationPeriodSnapshotLine.ClosingValue)
                 })
            builder.Property<decimal>(property).HasPrecision(19, 6);
        builder.HasOne(x => x.Header).WithMany(x => x.Lines)
            .HasForeignKey(x => x.HeaderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.HeaderId, x.ItemCode, x.CostMethod })
            .IsUnique().HasDatabaseName("UQ_StockValuationPeriodSnapshotLine_Pool");
    }
}
