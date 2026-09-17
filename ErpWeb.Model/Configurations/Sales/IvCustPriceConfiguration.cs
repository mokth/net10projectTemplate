using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class IvCustPriceConfiguration : IEntityTypeConfiguration<IvCustPrice>
{
    public void Configure(EntityTypeBuilder<IvCustPrice> builder)
    {
        builder.ToTable("IvCustPrice");

        // Phase 3 — surrogate key. The natural key is a separate UNIQUE index below, because two
        // quantity bands may share one ValidFrom and the natural key can therefore not be the PK.
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustPriceCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.UOM).HasMaxLength(5).IsRequired();
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.CustPriceDesc).HasMaxLength(50);
        builder.Property(e => e.SellingPrice).HasPrecision(18, 4);
        builder.Property(e => e.SellPackSize).HasPrecision(18, 4);

        // ValidFrom / MinQty are NOT NULL so they can live in the unique index (SQL Server treats NULLs
        // as equal there, which would admit only one undated / unbanded row per key).
        builder.Property(e => e.ValidFrom).HasColumnType("date").IsRequired().HasDefaultValue(IvCustPrice.AlwaysValidFrom);
        builder.Property(e => e.ValidTo).HasColumnType("date");
        builder.Property(e => e.MinQty).HasPrecision(18, 4).IsRequired().HasDefaultValue(0m);
        builder.Property(e => e.MaxQty).HasPrecision(18, 4);
        builder.Property(e => e.CurrencyCode).HasMaxLength(5);

        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        // The natural key: exactly ONE row per (list, item, UOM, effective-from, band-floor, CURRENCY).
        // ValidTo / MaxQty stay OUT so a tier or a promotion can be edited without key churn.
        //
        // CurrencyCode IS included, which deviates from the letter of plan 3.2 ("CurrencyCode stays OUT
        // of the unique index"). It has to be: plan 3.4 states that "a MYR tier and a USD tier over the
        // same band may coexist, because the resolver filters by currency before ranking", and that is
        // impossible if the unique index forbids two rows on one band. Including it satisfies the
        // capability the resolver was designed for; leaving it out would silently make mixed-currency
        // lists illegal and force one list per currency.
        builder.HasIndex(e => new { e.CompanyCode, e.CustPriceCode, e.ICode, e.UOM, e.ValidFrom, e.MinQty, e.CurrencyCode })
            .IsUnique()
            .HasDatabaseName("UX_IvCustPrice_BusinessKey");

        // Query index for the resolver: it filters by list + item + UOM and then by the window.
        builder.HasIndex(e => new { e.CompanyCode, e.CustPriceCode, e.ICode, e.UOM, e.ValidFrom, e.ValidTo })
            .HasDatabaseName("IX_IvCustPrice_Resolve");

        // Currency-covering index: the currency filter runs after the window/band filters.
        builder.HasIndex(e => new { e.CompanyCode, e.CustPriceCode, e.ICode, e.UOM, e.CurrencyCode })
            .HasDatabaseName("IX_IvCustPrice_Currency");
    }
}
