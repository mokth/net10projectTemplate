using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionPostingLinkConfiguration : IEntityTypeConfiguration<ProductionPostingLink>
{
    public void Configure(EntityTypeBuilder<ProductionPostingLink> builder)
    {
        builder.ToTable("PrProductionPostingLink");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CommandType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PostingRequestId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.ProductionDocumentType).HasMaxLength(30);
        builder.Property(x => x.ProductionDocumentNo).HasMaxLength(40);
        builder.Property(x => x.ProductionDocumentLineId).HasColumnName("ProductionDocumentLineID");
        builder.Property(x => x.PostingOperationId).HasColumnName("PostingOperationID").HasMaxLength(64);
        builder.Property(x => x.SnapshotHash).HasMaxLength(64);
        builder.Property(x => x.ProductionQtyThisIssue).HasPrecision(18, 4);
        builder.Property(x => x.OriginalPostingLinkId).HasColumnName("OriginalPostingLinkID");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ResultCode).HasMaxLength(50);
        builder.Property(x => x.ResultMessage).HasMaxLength(1000);
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CommandType, x.PostingRequestId })
            .IsUnique()
            .HasDatabaseName("UQ_PrProductionPostingLink_Idempotency");
        builder.HasIndex(x => new { x.WorkOrderId, x.CommandType, x.Status })
            .HasDatabaseName("IX_PrProductionPostingLink_Order_Command_Status");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CommandType, x.InventoryBatchNo })
            .IsUnique()
            .HasFilter("[InventoryBatchNo] IS NOT NULL AND [CommandType] = 'MATERIAL_ISSUE_POST'")
            .HasDatabaseName("UQ_PrProductionPostingLink_MaterialIssueBatch");
        builder.HasOne(x => x.OriginalPostingLink)
            .WithMany()
            .HasForeignKey(x => x.OriginalPostingLinkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

