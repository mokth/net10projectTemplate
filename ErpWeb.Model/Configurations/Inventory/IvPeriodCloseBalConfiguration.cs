using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvPeriodCloseBalConfiguration : IEntityTypeConfiguration<IvPeriodCloseBal>
{
    public void Configure(EntityTypeBuilder<IvPeriodCloseBal> builder)
    {
        builder.ToTable("IvPeriodCloseBal");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.WhCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LocCode).HasMaxLength(10).IsRequired().HasDefaultValue("");
        builder.Property(e => e.LotNo).HasMaxLength(50).IsRequired().HasDefaultValue("");
        builder.Property(e => e.IStatus).HasMaxLength(10).IsRequired().HasDefaultValue("");
        builder.Property(e => e.StdUom).HasMaxLength(10);

        builder.Property(e => e.OpeningQty).HasPrecision(18, 4);
        builder.Property(e => e.OpeningAdjustQty).HasPrecision(18, 4);
        builder.Property(e => e.InQty).HasPrecision(18, 4);
        builder.Property(e => e.OutQty).HasPrecision(18, 4);
        builder.Property(e => e.AdjustNetQty).HasPrecision(18, 4);
        builder.Property(e => e.ClosingQty).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.ClosingValue).HasPrecision(18, 4);
        builder.Property(e => e.CurrentBalanceDelta).HasPrecision(18, 4);

        // Legacy audit column aliases.
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);

        // One snapshot line per stock slice per closed period (D3/D13).
        builder.HasIndex(e => new { e.PeriodCloseId, e.ICode, e.WhCode, e.LocCode, e.LotNo, e.IStatus })
            .IsUnique()
            .HasDatabaseName("UQ_IvPeriodCloseBal_Slice");
    }
}
