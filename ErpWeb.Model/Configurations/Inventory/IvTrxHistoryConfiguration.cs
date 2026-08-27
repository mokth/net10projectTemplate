using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvTrxHistoryConfiguration : IEntityTypeConfiguration<IvTrxHistory>
{
    public void Configure(EntityTypeBuilder<IvTrxHistory> builder)
    {
        builder.ToTable("IvTrxHistory");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.TrxType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.BatchStatus).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RefNo).HasMaxLength(50);
        builder.Property(e => e.ProdCode).HasMaxLength(30);
        builder.Property(e => e.ProdDesc).HasMaxLength(200);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
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
        builder.Property(e => e.AsNowCost).HasColumnName("asNowCost").HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.BaseUnitPrices).HasPrecision(18, 4);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");

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
            .HasDatabaseName("UQ_IvTrxHistory_Company_Branch_Batch_Line");

        builder.HasIndex(e => new { e.ICode, e.TrxDtTime })
            .HasDatabaseName("IX_IvTrxHistory_ICode_TrxDtTime");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.BatchNo })
            .HasDatabaseName("IX_IvTrxHistory_BatchNo");

        builder.HasIndex(e => e.FromBalLocId)
            .HasDatabaseName("IX_IvTrxHistory_FromBalLocId");

        builder.HasIndex(e => e.ToBalLocId)
            .HasDatabaseName("IX_IvTrxHistory_ToBalLocId");

        builder.HasIndex(e => e.FromLotId)
            .HasDatabaseName("IX_IvTrxHistory_FromLotId");

        builder.HasIndex(e => e.ToLotId)
            .HasDatabaseName("IX_IvTrxHistory_ToLotId");
    }
}
