using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Quality;

public sealed class QaPolicyConfiguration : IEntityTypeConfiguration<QaPolicy>
{
    public void Configure(EntityTypeBuilder<QaPolicy> builder)
    {
        builder.ToTable("QaPolicy");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.Version })
            .IsUnique()
            .HasDatabaseName("UX_QaPolicy_Company_Version");
        builder.HasIndex(x => x.CompanyCode)
            .IsUnique()
            .HasFilter("[IsPublished] = 1")
            .HasDatabaseName("UX_QaPolicy_Company_Published");
    }
}

public sealed class QaCaseConfiguration : IEntityTypeConfiguration<QaCase>
{
    public void Configure(EntityTypeBuilder<QaCase> builder)
    {
        builder.ToTable("QaCase");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CaseNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceDocType).HasMaxLength(10);
        builder.Property(x => x.SourceDocNo).HasMaxLength(30);
        builder.Property(x => x.SupplierCode).HasMaxLength(20);
        builder.Property(x => x.SupplierName).HasMaxLength(100);
        builder.Property(x => x.DeliveryNote).HasMaxLength(50);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DeliveryInstanceId })
            .IsUnique()
            .HasFilter("[DeliveryInstanceId] IS NOT NULL")
            .HasDatabaseName("UX_QaCase_DeliveryInstance");
        builder.Property(x => x.PoNo).HasMaxLength(30);
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.SourceHref).HasMaxLength(200);
        builder.Property(x => x.EligibilityNote).HasMaxLength(250).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.SubmittedBy).HasMaxLength(20);
        builder.Property(x => x.InspectedBy).HasMaxLength(20);
        builder.Property(x => x.ReviewedBy).HasMaxLength(20);
        builder.Property(x => x.ReviewDecision).HasMaxLength(20);
        builder.Property(x => x.ReviewRemarks).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.CaseDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.SubmittedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.InspectedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.ReviewedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CaseNo })
            .IsUnique()
            .HasDatabaseName("UX_QaCase_Company_Branch_No");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.CaseDate })
            .HasDatabaseName("IX_QaCase_Workbench");
        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Inspections)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Dispositions)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.AuditEvents)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Grants)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.FgGrants)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.LogicalHolds)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.PhysicalHolds)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.NonGoodGrants)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.CorrectiveActions)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.InspectionAttempts)
            .WithOne(x => x.Case)
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaCaseLineConfiguration : IEntityTypeConfiguration<QaCaseLine>
{
    public void Configure(EntityTypeBuilder<QaCaseLine> builder)
    {
        builder.ToTable("QaCaseLine");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ItemName).HasMaxLength(100);
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.InspectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.WarehouseCode).HasMaxLength(10);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.ItemStatus).HasMaxLength(10);
        builder.Property(x => x.SourceIdentity).HasMaxLength(200);
        builder.Property(x => x.LineEligibilityNote).HasMaxLength(250);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.CaseId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_QaCaseLine_Case_LineNo");
        builder.HasMany(x => x.Inspections)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Dispositions)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Grants)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.FgGrants)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.CorrectiveActions)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.InspectionAttempts)
            .WithOne(x => x.CaseLine)
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaInspectionConfiguration : IEntityTypeConfiguration<QaInspection>
{
    public void Configure(EntityTypeBuilder<QaInspection> builder)
    {
        builder.ToTable("QaInspection");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Result).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Inspector).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.MeasuredValues).HasMaxLength(500);
        builder.Property(x => x.InspectedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaInspection_CaseLine");
    }
}

public sealed class QaDispositionLineConfiguration : IEntityTypeConfiguration<QaDispositionLine>
{
    public void Configure(EntityTypeBuilder<QaDispositionLine> builder)
    {
        builder.ToTable("QaDispositionLine");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Kind).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ReasonCode).HasMaxLength(30);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.CaseLineId, x.Kind })
            .IsUnique()
            .HasDatabaseName("UX_QaDisposition_Line_Kind");
    }
}

public sealed class QaAuditEventConfiguration : IEntityTypeConfiguration<QaAuditEvent>
{
    public void Configure(EntityTypeBuilder<QaAuditEvent> builder)
    {
        builder.ToTable("QaAuditEvent");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ByUser).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Payload).HasMaxLength(1000);
        builder.Property(x => x.AtUtc).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => new { x.CaseId, x.AtUtc })
            .HasDatabaseName("IX_QaAuditEvent_Case_At");
    }
}

public sealed class QaReceiptGrantConfiguration : IEntityTypeConfiguration<QaReceiptGrant>
{
    public void Configure(EntityTypeBuilder<QaReceiptGrant> builder)
    {
        builder.ToTable("QaReceiptGrant");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.PoNo).HasMaxLength(30);
        builder.Property(x => x.SupplierCode).HasMaxLength(20);
        builder.Property(x => x.DeliveryNote).HasMaxLength(50);
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.AcceptedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RejectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaReceiptGrant_CaseLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PoNo, x.PoRelNo, x.PoLineNo, x.Status })
            .HasDatabaseName("IX_QaReceiptGrant_PoLine");
        builder.HasMany(x => x.Allocations)
            .WithOne(x => x.Grant)
            .HasForeignKey(x => x.GrantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaReceiptAllocationConfiguration : IEntityTypeConfiguration<QaReceiptAllocation>
{
    public void Configure(EntityTypeBuilder<QaReceiptAllocation> builder)
    {
        builder.ToTable("QaReceiptAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PostedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReversedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.GrantId, x.BatchDetailId })
            .IsUnique()
            .HasFilter("[Status] = 'POSTED'")
            .HasDatabaseName("UX_QaReceiptAllocation_Posted_Grant_Detail");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BatchNo, x.Status })
            .HasDatabaseName("IX_QaReceiptAllocation_Batch");
    }
}

public sealed class QaFgGrantConfiguration : IEntityTypeConfiguration<QaFgGrant>
{
    public void Configure(EntityTypeBuilder<QaFgGrant> builder)
    {
        builder.ToTable("QaFgGrant");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.AcceptedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RejectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaFgGrant_CaseLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductionBalLotId, x.Status })
            .HasDatabaseName("IX_QaFgGrant_Lot");
        builder.HasMany(x => x.Allocations)
            .WithOne(x => x.Grant)
            .HasForeignKey(x => x.GrantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaFgAllocationConfiguration : IEntityTypeConfiguration<QaFgAllocation>
{
    public void Configure(EntityTypeBuilder<QaFgAllocation> builder)
    {
        builder.ToTable("QaFgAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PostedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReversedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.GrantId, x.SourceId })
            .IsUnique()
            .HasFilter("[Status] = 'POSTED'")
            .HasDatabaseName("UX_QaFgAllocation_Posted_Grant_Source");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BatchId, x.Status })
            .HasDatabaseName("IX_QaFgAllocation_Batch");
    }
}

public sealed class QaLogicalHoldConfiguration : IEntityTypeConfiguration<QaLogicalHold>
{
    public void Configure(EntityTypeBuilder<QaLogicalHold> builder)
    {
        builder.ToTable("QaLogicalHold");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ReservedBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Cause).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReleasedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaLogicalHold_CaseLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductionBalLotId, x.Status })
            .HasDatabaseName("IX_QaLogicalHold_Lot");
        builder.HasOne(x => x.CaseLine)
            .WithMany()
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaPhysicalHoldConfiguration : IEntityTypeConfiguration<QaPhysicalHold>
{
    public void Configure(EntityTypeBuilder<QaPhysicalHold> builder)
    {
        builder.ToTable("QaPhysicalHold");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ItemStatus).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ReservedBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Cause).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReleasedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaPhysicalHold_CaseLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BalLocId, x.Status })
            .HasDatabaseName("IX_QaPhysicalHold_BalLoc");
        builder.HasOne(x => x.CaseLine)
            .WithMany()
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Allocations)
            .WithOne(x => x.Hold)
            .HasForeignKey(x => x.HoldId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaPhysicalAllocationConfiguration : IEntityTypeConfiguration<QaPhysicalAllocation>
{
    public void Configure(EntityTypeBuilder<QaPhysicalAllocation> builder)
    {
        builder.ToTable("QaPhysicalAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TrxType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.PostedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReversedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.HoldId, x.BatchDetailId })
            .IsUnique()
            .HasFilter("[Status] = 'POSTED'")
            .HasDatabaseName("UX_QaPhysicalAllocation_Posted_Hold_Detail");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BatchNo, x.Status })
            .HasDatabaseName("IX_QaPhysicalAllocation_Batch");
    }
}

public sealed class QaNonGoodGrantConfiguration : IEntityTypeConfiguration<QaNonGoodGrant>
{
    public void Configure(EntityTypeBuilder<QaNonGoodGrant> builder)
    {
        builder.ToTable("QaNonGoodGrant");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.DocumentNo).HasMaxLength(30);
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.AcceptedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RejectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.CaseLineId)
            .IsUnique()
            .HasDatabaseName("UX_QaNonGoodGrant_CaseLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductionOutputId, x.Status })
            .HasDatabaseName("IX_QaNonGoodGrant_Output");
        builder.HasOne(x => x.CaseLine)
            .WithMany()
            .HasForeignKey(x => x.CaseLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Allocations)
            .WithOne(x => x.Grant)
            .HasForeignKey(x => x.GrantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QaNonGoodAllocationConfiguration : IEntityTypeConfiguration<QaNonGoodAllocation>
{
    public void Configure(EntityTypeBuilder<QaNonGoodAllocation> builder)
    {
        builder.ToTable("QaNonGoodAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PostedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ReversedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.GrantId, x.ProductionOutputId })
            .IsUnique()
            .HasFilter("[Status] = 'POSTED'")
            .HasDatabaseName("UX_QaNonGoodAllocation_Posted_Grant_Output");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductionOutputId, x.Status })
            .HasDatabaseName("IX_QaNonGoodAllocation_Output");
    }
}

public sealed class QaCorrectiveActionConfiguration : IEntityTypeConfiguration<QaCorrectiveAction>
{
    public void Configure(EntityTypeBuilder<QaCorrectiveAction> builder)
    {
        builder.ToTable("QaCorrectiveAction");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ActionType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.AssignedTo).HasMaxLength(20);
        builder.Property(x => x.AssignedDepartment).HasMaxLength(30);
        builder.Property(x => x.Reason).HasMaxLength(250);
        builder.Property(x => x.DueDate).HasColumnType("datetime2");
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CompletionEvidence).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.CompletedBy).HasMaxLength(20);
        builder.Property(x => x.CancelledBy).HasMaxLength(20);
        builder.Property(x => x.CancelReason).HasMaxLength(250);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.CompletedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.CancelledAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CaseId, x.ActionNo })
            .IsUnique()
            .HasDatabaseName("UX_QaCorrectiveAction_Case_No");
        builder.HasIndex(x => x.RequestId)
            .IsUnique()
            .HasFilter("[RequestId] IS NOT NULL")
            .HasDatabaseName("UX_QaCorrectiveAction_RequestId");
        builder.HasIndex(x => new { x.CaseId, x.Status })
            .HasDatabaseName("IX_QaCorrectiveAction_Case_Status");
    }
}

public sealed class QaInspectionAttemptConfiguration : IEntityTypeConfiguration<QaInspectionAttempt>
{
    public void Configure(EntityTypeBuilder<QaInspectionAttempt> builder)
    {
        builder.ToTable("QaInspectionAttempt");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Result).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Inspector).HasMaxLength(20).IsRequired();
        builder.Property(x => x.InspectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.MeasuredValues).HasMaxLength(500);
        builder.Property(x => x.DispositionSnapshot).HasMaxLength(500);
        builder.Property(x => x.SourceIdentity).HasMaxLength(200);
        builder.Property(x => x.Reason).HasMaxLength(250);
        builder.Property(x => x.InspectedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => new { x.CaseLineId, x.AttemptNo })
            .IsUnique()
            .HasDatabaseName("UX_QaInspectionAttempt_Line_No");
        builder.HasIndex(x => new { x.CaseId, x.InspectedAtUtc })
            .HasDatabaseName("IX_QaInspectionAttempt_Case_At");
    }
}
