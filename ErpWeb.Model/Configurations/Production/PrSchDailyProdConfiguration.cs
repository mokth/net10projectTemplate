using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class PrSchDailyProdConfiguration : IEntityTypeConfiguration<PrSchDailyProd>
{
    public void Configure(EntityTypeBuilder<PrSchDailyProd> builder)
    {
        builder.ToTable("PrSchDailyProd");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.ScheCode).HasMaxLength(20);
        builder.Property(e => e.ProdCode).HasMaxLength(20);
        builder.Property(e => e.RelNo);
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcICode).HasColumnName("WCICode").HasMaxLength(20);
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LineNo);
        builder.Property(e => e.CompletedDate);
        builder.Property(e => e.CompletedTime);
        builder.Property(e => e.GoodQty);
        builder.Property(e => e.GoodUom).HasColumnName("GoodUom").HasMaxLength(5);
        builder.Property(e => e.OnHoldQty);
        builder.Property(e => e.OnHoldUom).HasColumnName("OnHoldUom").HasMaxLength(5);
        builder.Property(e => e.Remarks).HasMaxLength(200);
        builder.Property(e => e.Shift).HasMaxLength(10);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(50);
        builder.Property(e => e.LotNo).HasMaxLength(20);
        builder.Property(e => e.RevNo);
        builder.Property(e => e.ToWarehouse).HasMaxLength(10);
        builder.Property(e => e.ToLocation).HasMaxLength(10);
        builder.Property(e => e.IStatus).HasColumnName("IStatus").HasMaxLength(10);
        builder.Property(e => e.FinalProcess);
        builder.Property(e => e.ReasonCode).HasMaxLength(10);
        builder.Property(e => e.TrxType).HasMaxLength(5);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20);
        builder.Property(e => e.RefNo).HasMaxLength(20);
        builder.Property(e => e.LotCompleted);
        builder.Property(e => e.BatchNo);
        builder.Property(e => e.PreWCenter).HasMaxLength(10);
        builder.Property(e => e.PreProcess).HasMaxLength(10);
        builder.Property(e => e.PreICode).HasColumnName("PreICode").HasMaxLength(20);
        builder.Property(e => e.PreQty);
        builder.Property(e => e.PreQtyUom).HasColumnName("PreQtyUOM").HasMaxLength(5);
        builder.Property(e => e.PreWt);
        builder.Property(e => e.PreWtUom).HasColumnName("PreWtUOM").HasMaxLength(5);
        builder.Property(e => e.PreRevNo);
        builder.Property(e => e.PreDate);
        builder.Property(e => e.PreLotNo).HasMaxLength(20);
        builder.Property(e => e.PreScheCode).HasMaxLength(50);
        builder.Property(e => e.ScrapStdQty);
        builder.Property(e => e.ScrapStdQtyUom).HasColumnName("ScrapStdQtyUOM").HasMaxLength(5);
        builder.Property(e => e.ScrapWtQty);
        builder.Property(e => e.ScrapWtQtyUom).HasColumnName("ScrapWtQtyUOM").HasMaxLength(5);
        builder.Property(e => e.MachineCode).HasMaxLength(10);
        builder.Property(e => e.PreTrxType).HasMaxLength(5);
        builder.Property(e => e.Operator).HasMaxLength(20);
        builder.Property(e => e.StartTime);
        builder.Property(e => e.EndTime);
        builder.Property(e => e.GoodScrapQty);
        builder.Property(e => e.GoodRejectQty);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 8);
        builder.Property(e => e.NumOperator);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
