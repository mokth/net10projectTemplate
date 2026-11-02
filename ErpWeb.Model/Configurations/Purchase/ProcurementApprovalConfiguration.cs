using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public sealed class ProcurementApprovalPolicyConfiguration : IEntityTypeConfiguration<ProcurementApprovalPolicy>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalPolicy> builder)
    {
        builder.ToTable("PoApprovalPolicy");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LegacyDraftMode).HasMaxLength(40).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.CutoverAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.EffectiveAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.DocType, x.Version })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalPolicy_Company_DocType_Version");
        builder.HasMany(x => x.Bands)
            .WithOne(x => x.Policy)
            .HasForeignKey(x => x.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProcurementApprovalBandConfiguration : IEntityTypeConfiguration<ProcurementApprovalBand>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalBand> builder)
    {
        builder.ToTable("PoApprovalBand");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Currency).HasMaxLength(20).IsRequired();
        builder.Property(x => x.MinInclusive).HasColumnType("decimal(18,2)");
        builder.Property(x => x.MaxExclusive).HasColumnType("decimal(18,2)");
        builder.HasIndex(x => new { x.PolicyId, x.Currency, x.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalBand_Policy_Currency_Seq");
        builder.HasMany(x => x.Levels)
            .WithOne(x => x.Band)
            .HasForeignKey(x => x.BandId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProcurementApprovalLevelConfiguration : IEntityTypeConfiguration<ProcurementApprovalLevel>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalLevel> builder)
    {
        builder.ToTable("PoApprovalLevel");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.MatchMode).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => new { x.BandId, x.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalLevel_Band_Seq");
        builder.HasMany(x => x.Assignees)
            .WithOne(x => x.Level)
            .HasForeignKey(x => x.LevelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProcurementApprovalLevelAssigneeConfiguration : IEntityTypeConfiguration<ProcurementApprovalLevelAssignee>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalLevelAssignee> builder)
    {
        builder.ToTable("PoApprovalLevelAssignee");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Kind).HasMaxLength(10).IsRequired();
        builder.Property(x => x.RefCode).HasMaxLength(50).IsRequired();
        builder.HasIndex(x => new { x.LevelId, x.Kind, x.RefCode })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalLevelAssignee_Level_Kind_Ref");
    }
}

public sealed class ProcurementApprovalInstanceConfiguration : IEntityTypeConfiguration<ProcurementApprovalInstance>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalInstance> builder)
    {
        builder.ToTable("PoApprovalInstance");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Gross).HasColumnType("decimal(18,2)");
        builder.Property(x => x.Taxes).HasColumnType("decimal(18,2)");
        builder.Property(x => x.Total).HasColumnType("decimal(18,2)");
        builder.Property(x => x.MaterialFingerprint).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SubmittedBy).HasMaxLength(20);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(80);
        builder.Property(x => x.SubmittedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocType, x.DocNo, x.DocRev, x.Cycle })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalInstance_Doc_Cycle");
        builder.HasIndex(x => new { x.CompanyCode, x.Status, x.SubmittedAtUtc })
            .HasDatabaseName("IX_PoApprovalInstance_Inbox");
        builder.HasMany(x => x.Assignees)
            .WithOne(x => x.Instance)
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Decisions)
            .WithOne(x => x.Instance)
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Events)
            .WithOne(x => x.Instance)
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProcurementApprovalAssigneeConfiguration : IEntityTypeConfiguration<ProcurementApprovalAssignee>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalAssignee> builder)
    {
        builder.ToTable("PoApprovalAssignee");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.UserId).HasMaxLength(20).IsRequired();
        builder.Property(x => x.MatchMode).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => new { x.InstanceId, x.LevelSequence, x.UserId })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalAssignee_Instance_Level_User");
        builder.HasIndex(x => new { x.UserId, x.InstanceId })
            .HasDatabaseName("IX_PoApprovalAssignee_User");
    }
}

public sealed class ProcurementApprovalDecisionConfiguration : IEntityTypeConfiguration<ProcurementApprovalDecision>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalDecision> builder)
    {
        builder.ToTable("PoApprovalDecision");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.UserId).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Decision).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.DecidedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => new { x.InstanceId, x.LevelSequence, x.UserId })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalDecision_Vote");
    }
}

public sealed class ProcurementApprovalEventConfiguration : IEntityTypeConfiguration<ProcurementApprovalEvent>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalEvent> builder)
    {
        builder.ToTable("PoApprovalEvent");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PriorStatus).HasMaxLength(20);
        builder.Property(x => x.NewStatus).HasMaxLength(20);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(80);
        builder.Property(x => x.ActorUserId).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => new { x.CompanyCode, x.DocType, x.DocNo, x.OccurredAtUtc })
            .HasDatabaseName("IX_PoApprovalEvent_Doc_Date");
    }
}

public sealed class ProcurementApprovalCutoverExemptionConfiguration : IEntityTypeConfiguration<ProcurementApprovalCutoverExemption>
{
    public void Configure(EntityTypeBuilder<ProcurementApprovalCutoverExemption> builder)
    {
        builder.ToTable("PoApprovalCutoverExemption");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ActorUserId).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CutoverAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocType, x.DocNo, x.DocRev, x.PolicyId })
            .IsUnique()
            .HasDatabaseName("UX_PoApprovalCutover_Doc_Policy");
    }
}
