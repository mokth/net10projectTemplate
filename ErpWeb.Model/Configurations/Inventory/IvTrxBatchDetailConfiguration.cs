using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvTrxBatchDetailConfiguration : IEntityTypeConfiguration<IvTrxBatchDetail>
{
    public void Configure(EntityTypeBuilder<IvTrxBatchDetail> builder)
    {
        builder.ToTable("IvTrxBatchDetail");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.TrxType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(30);
        builder.Property(e => e.ProdDesc).HasMaxLength(200);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.FrWarehouse).HasMaxLength(20);
        builder.Property(e => e.FrLocation).HasMaxLength(10);
        builder.Property(e => e.FrLotNo).HasColumnName("FrLot").HasMaxLength(50);
        builder.Property(e => e.FrStdQty).HasPrecision(18, 4);
        builder.Property(e => e.FrStdUom).HasColumnName("FrStdUOM").HasMaxLength(10);
        builder.Property(e => e.FrPurQty).HasPrecision(18, 4);
        builder.Property(e => e.FrPurUom).HasColumnName("FrPurUOM").HasMaxLength(10);
        builder.Property(e => e.ToWarehouse).HasMaxLength(20);
        builder.Property(e => e.ToLocation).HasMaxLength(10);
        builder.Property(e => e.ToLotNo).HasColumnName("ToLot").HasMaxLength(50);
        builder.Property(e => e.ToStdQty).HasPrecision(18, 4);
        builder.Property(e => e.ToStdUom).HasColumnName("ToStdUOM").HasMaxLength(10);
        builder.Property(e => e.ToPurQty).HasPrecision(18, 4);
        builder.Property(e => e.ToPurUom).HasColumnName("ToPurUOM").HasMaxLength(10);
        builder.Property(e => e.IStatus).HasColumnName("IStatus").HasMaxLength(10);
        builder.Property(e => e.IClassCode).HasColumnName("IClassCode").HasMaxLength(30);
        builder.Property(e => e.ExpiryDate);
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30);
        builder.Property(e => e.InvNo).HasMaxLength(30);
        builder.Property(e => e.SoNo).HasColumnName("SO_No").HasMaxLength(30);
        builder.Property(e => e.PoNo).HasColumnName("PO_No").HasMaxLength(30);
        builder.Property(e => e.PoRelNo).HasColumnName("PO_Rel_No");
        builder.Property(e => e.SoLineNo).HasColumnName("SO_Line_No");
        builder.Property(e => e.PoLineNo).HasColumnName("PO_Line_No");
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.Cost).HasPrecision(18, 4);
        builder.Property(e => e.CostPrice).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.BaseUnitPrices).HasPrecision(18, 4);
        builder.Property(e => e.Currency).HasMaxLength(3);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        builder.HasOne(e => e.Batch)
            .WithMany(e => e.Details)
            .HasForeignKey(e => e.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.FromBalLoc)
            .WithMany()
            .HasForeignKey(e => e.FromBalLocId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.ToBalLoc)
            .WithMany()
            .HasForeignKey(e => e.ToBalLocId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.FromLot)
            .WithMany()
            .HasForeignKey(e => e.FromLotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.ToLot)
            .WithMany()
            .HasForeignKey(e => e.ToLotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.BatchNo, e.TrxLineNo })
            .IsUnique()
            .HasDatabaseName("UQ_IvTrxBatchDetail_Company_Branch_Batch_Line");

        builder.HasIndex(e => new { e.CompanyCode, e.ICode })
            .HasDatabaseName("IX_IvTrxBatchDetail_Company_ICode");
    }
}
