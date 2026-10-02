using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionBalLotMovementConfiguration : IEntityTypeConfiguration<ProductionBalLotMovement>
{
    public void Configure(EntityTypeBuilder<ProductionBalLotMovement> builder)
    {
        builder.ToTable("PrProductionBalLotMovement", table =>
        {
            table.HasCheckConstraint("CK_PrProductionBalLotMovement_Qty", "[Qty] > 0 AND [BaseQty] > 0");
            table.HasCheckConstraint("CK_PrProductionBalLotMovement_Cost", "[UnitCost] >= 0 AND [TotalCost] >= 0");
            table.HasCheckConstraint(
                "CK_PrProductionBalLotMovement_Type",
                "[MovementType] IN ('ISSUE','ISSUE_REVERSAL','PRODUCE','PRODUCE_REVERSAL','CONSUME','CONSUME_REVERSAL','RETURN','RETURN_REVERSAL')");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ProductionBalLotId).HasColumnName("ProductionBalLotID");
        builder.Property(x => x.MovementType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Qty).HasPrecision(18, 4);
        builder.Property(x => x.Uom).HasColumnName("UOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.UnitCost).HasPrecision(18, 4);
        builder.Property(x => x.TotalCost).HasPrecision(18, 4);
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.ProductionOutputId).HasColumnName("ProductionOutputID");
        builder.Property(x => x.PostingLinkId).HasColumnName("PostingLinkID");
        builder.Property(x => x.OriginalMovementId).HasColumnName("OriginalMovementID");
        builder.Property(x => x.DocumentType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DocumentNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.MovementDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();

        builder.HasOne(x => x.ProductionBalLot).WithMany(x => x.Movements).HasForeignKey(x => x.ProductionBalLotId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionOutput).WithMany().HasForeignKey(x => x.ProductionOutputId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PostingLink).WithMany().HasForeignKey(x => x.PostingLinkId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginalMovement).WithMany().HasForeignKey(x => x.OriginalMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductionBalLotId, x.MovementDate, x.Uid })
            .HasDatabaseName("IX_PrProductionBalLotMovement_Lot_Date");
        builder.HasIndex(x => x.OriginalMovementId).HasDatabaseName("IX_PrProductionBalLotMovement_Original");
        builder.HasIndex(x => new { x.PostingLinkId, x.MovementType }).HasDatabaseName("IX_PrProductionBalLotMovement_Posting");
    }
}
