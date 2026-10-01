using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMaterialIssueLineConfiguration : IEntityTypeConfiguration<ProductionMaterialIssueLine>
{
    public void Configure(EntityTypeBuilder<ProductionMaterialIssueLine> builder)
    {
        builder.ToTable("PrMaterialIssueLine", t =>
        {
            t.HasCheckConstraint("CK_PrMaterialIssueLine_IssueQty", "[IssueQty] > 0");
            t.HasCheckConstraint("CK_PrMaterialIssueLine_BaseQty", "[BaseQty] > 0");
        });
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.PostingLinkId).HasColumnName("PostingLinkID");
        builder.Property(x => x.InventoryBatchId).HasColumnName("InventoryBatchID");
        builder.Property(x => x.InventoryBatchDetailId).HasColumnName("InventoryBatchDetailID");
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.IssueQty).HasPrecision(18, 4);
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.InventoryBatchDetailId).IsUnique().HasDatabaseName("UQ_PrMaterialIssueLine_InventoryDetail");
        builder.HasIndex(x => new { x.PostingLinkId, x.InventoryTrxLineNo }).IsUnique().HasDatabaseName("UQ_PrMaterialIssueLine_PostingLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.InventoryBatchNo }).HasDatabaseName("IX_PrMaterialIssueLine_Batch");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderMaterialId }).HasDatabaseName("IX_PrMaterialIssueLine_Material");
        builder.HasIndex(x => new { x.WorkOrderOperationId, x.WorkOrderMaterialId }).HasDatabaseName("IX_PrMaterialIssueLine_Operation");
        builder.HasOne(x => x.PostingLink).WithMany().HasForeignKey(x => x.PostingLinkId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryBatch).WithMany().HasForeignKey(x => x.InventoryBatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryBatchDetail).WithMany().HasForeignKey(x => x.InventoryBatchDetailId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderOperation).WithMany().HasForeignKey(x => x.WorkOrderOperationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderMaterial).WithMany().HasForeignKey(x => x.WorkOrderMaterialId).OnDelete(DeleteBehavior.Restrict);
    }
}
