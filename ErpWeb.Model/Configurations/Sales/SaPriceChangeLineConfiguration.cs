using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaPriceChangeLineConfiguration : IEntityTypeConfiguration<SaPriceChangeLine>
{
    public void Configure(EntityTypeBuilder<SaPriceChangeLine> builder)
    {
        builder.ToTable("SaPriceChangeLine");
        builder.HasKey(x => x.PriceChangeLineId);
        builder.Property(x => x.PriceChangeLineId).ValueGeneratedOnAdd();

        builder.Property(x => x.PriceChangeBatchId).IsRequired();
        builder.Property(x => x.ChangeKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ItemDescriptionSnapshot).HasMaxLength(200);
        builder.Property(x => x.ItemTypeSnapshot).HasMaxLength(20);
        builder.Property(x => x.ItemClassSnapshot).HasMaxLength(20);
        builder.Property(x => x.ItemSubClassSnapshot).HasMaxLength(20);
        builder.Property(x => x.BrandSnapshot).HasMaxLength(50);

        builder.Property(x => x.CustCode).HasMaxLength(20);
        builder.Property(x => x.CustomerNameSnapshot).HasMaxLength(200);
        builder.Property(x => x.CustomerTypeSnapshot).HasMaxLength(20);
        builder.Property(x => x.CustomerGroupSnapshot).HasMaxLength(20);
        builder.Property(x => x.CustPriceCode).HasMaxLength(20);
        builder.Property(x => x.PriceListDescriptionSnapshot).HasMaxLength(50);
        builder.Property(x => x.SourcePriceListLineId);
        builder.Property(x => x.Moq);

        builder.Property(x => x.OldUom).HasMaxLength(10);
        builder.Property(x => x.NewUom).HasMaxLength(10);
        builder.Property(x => x.OldCurrencyCode).HasMaxLength(5);
        builder.Property(x => x.NewCurrencyCode).HasMaxLength(5);

        builder.Property(x => x.OldMinQty).HasPrecision(18, 4);
        builder.Property(x => x.OldMaxQty).HasPrecision(18, 4);
        builder.Property(x => x.NewMinQty).HasPrecision(18, 4);
        builder.Property(x => x.NewMaxQty).HasPrecision(18, 4);
        builder.Property(x => x.OldValidFrom).HasColumnType("date");
        builder.Property(x => x.OldValidTo).HasColumnType("date");
        builder.Property(x => x.NewValidFrom).HasColumnType("date");
        builder.Property(x => x.NewValidTo).HasColumnType("date");
        builder.Property(x => x.OldPrice).HasPrecision(18, 4);
        builder.Property(x => x.NewPrice).HasPrecision(18, 4);

        builder.HasIndex(x => x.PriceChangeBatchId)
            .HasDatabaseName("IX_SaPriceChangeLine_Batch");
        builder.HasIndex(x => new { x.ItemCode, x.PriceChangeBatchId })
            .HasDatabaseName("IX_SaPriceChangeLine_Item_Batch");
        builder.HasIndex(x => new { x.CustCode, x.PriceChangeBatchId })
            .HasDatabaseName("IX_SaPriceChangeLine_Customer_Batch");
        builder.HasIndex(x => new { x.CustPriceCode, x.PriceChangeBatchId })
            .HasDatabaseName("IX_SaPriceChangeLine_PriceList_Batch");
    }
}
