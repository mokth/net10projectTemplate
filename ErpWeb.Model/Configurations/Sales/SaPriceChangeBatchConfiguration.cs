using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaPriceChangeBatchConfiguration : IEntityTypeConfiguration<SaPriceChangeBatch>
{
    public void Configure(EntityTypeBuilder<SaPriceChangeBatch> builder)
    {
        builder.ToTable("SaPriceChangeBatch");
        builder.HasKey(x => x.PriceChangeBatchId);
        builder.Property(x => x.PriceChangeBatchId).ValueGeneratedOnAdd();

        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Origin).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.AdjustmentMethod).HasMaxLength(30);
        builder.Property(x => x.AdjustmentValue).HasPrecision(18, 4);
        builder.Property(x => x.RoundingMode).HasMaxLength(20);
        builder.Property(x => x.EffectiveDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(200);

        builder.Property(x => x.ItemSearchFilter).HasMaxLength(100);
        builder.Property(x => x.ItemTypeFilter).HasMaxLength(20);
        builder.Property(x => x.ItemClassFilter).HasMaxLength(20);
        builder.Property(x => x.ItemSubClassFilter).HasMaxLength(20);
        builder.Property(x => x.BrandFilter).HasMaxLength(50);
        builder.Property(x => x.CustCodeFilter).HasMaxLength(20);
        builder.Property(x => x.CustTypeFilter).HasMaxLength(20);
        builder.Property(x => x.CustGroupFilter).HasMaxLength(20);
        builder.Property(x => x.CustPriceCodeFilter).HasMaxLength(20);
        builder.Property(x => x.CurrencyFilter).HasMaxLength(5);
        builder.Property(x => x.UomFilter).HasMaxLength(10);

        builder.Property(x => x.ChangedRowCount).IsRequired();
        builder.Property(x => x.ChangedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ChangedBy).HasMaxLength(10).IsRequired();

        builder.HasIndex(x => new { x.CompanyCode, x.ChangedAtUtc })
            .HasDatabaseName("IX_SaPriceChangeBatch_Company_ChangedAtUtc");
        builder.HasIndex(x => new { x.CompanyCode, x.TargetType, x.ChangedAtUtc })
            .HasDatabaseName("IX_SaPriceChangeBatch_Company_Target_ChangedAtUtc");
        builder.HasIndex(x => new { x.CompanyCode, x.EffectiveDate })
            .HasDatabaseName("IX_SaPriceChangeBatch_Company_EffectiveDate");

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.PriceChangeBatch)
            .HasForeignKey(x => x.PriceChangeBatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
