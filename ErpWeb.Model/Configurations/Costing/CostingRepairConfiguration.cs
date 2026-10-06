using ErpWeb.Model.Entities.Costing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Costing;

public sealed class CostingRepairCaseConfiguration : IEntityTypeConfiguration<CostingRepairCase>
{
    public void Configure(EntityTypeBuilder<CostingRepairCase> builder)
    {
        builder.ToTable("CostingRepairCase");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.RootFindingCode).HasMaxLength(20);
        builder.Property(x => x.Strategy).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PreviewHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.RequestedBy).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.RepairRequestId })
            .IsUnique();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status });
        builder.HasIndex(x => x.RootStockPostingId);
    }
}

public sealed class CostingRepairAuditEventConfiguration : IEntityTypeConfiguration<CostingRepairAuditEvent>
{
    public void Configure(EntityTypeBuilder<CostingRepairAuditEvent> builder)
    {
        builder.ToTable("CostingRepairAuditEvent");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.StableStepId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Module).HasMaxLength(40).IsRequired();
        builder.Property(x => x.DocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.DocumentNo).HasMaxLength(40).IsRequired();
        builder.Property(x => x.DesiredAction).HasMaxLength(20);
        builder.Property(x => x.BeforeStatus).HasMaxLength(20);
        builder.Property(x => x.AfterStatus).HasMaxLength(20);
        builder.Property(x => x.EvidenceJson).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(50).IsRequired();
        builder.HasIndex(x => new { x.RepairCaseId, x.Sequence }).IsUnique();
        builder.HasOne(x => x.RepairCase).WithMany(x => x.Events)
            .HasForeignKey(x => x.RepairCaseId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
