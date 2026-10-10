using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryAttemptConfiguration : IEntityTypeConfiguration<SaDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<SaDeliveryAttempt> builder)
    {
        builder.ToTable("SaDeliveryAttempt", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryAttempt_Result", "Result IN ('DELIVERED', 'PARTIAL', 'FAILED')");
            table.HasCheckConstraint("CK_SaDeliveryAttempt_Responsibility", "Responsibility IS NULL OR Responsibility IN ('DRIVER', 'WAREHOUSE', 'CUSTOMER', 'SALES', 'VEHICLE', 'EXTERNAL', 'WEATHER_TRAFFIC', 'OTHER')");
            table.HasCheckConstraint("CK_SaDeliveryAttempt_AttemptNo", "AttemptNo > 0");
            table.HasCheckConstraint("CK_SaDeliveryAttempt_Latitude", "Latitude IS NULL OR Latitude BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_SaDeliveryAttempt_Longitude", "Longitude IS NULL OR Longitude BETWEEN -180 AND 180");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.AttemptId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.AttemptId).HasColumnName("AttemptID").ValueGeneratedOnAdd();
        builder.Property(x => x.TripNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.StopId).HasColumnName("StopID").IsRequired();
        builder.Property(x => x.AttemptNo).IsRequired();
        builder.Property(x => x.StartedAt).HasColumnType("datetime2");
        builder.Property(x => x.ArrivedAt).HasColumnType("datetime2");
        builder.Property(x => x.CompletedAt).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.Result).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(30);
        builder.Property(x => x.Responsibility).HasMaxLength(30);
        builder.Property(x => x.ReceivedBy).HasMaxLength(100);
        builder.Property(x => x.ReceiverContact).HasMaxLength(50);
        builder.Property(x => x.Remark).HasMaxLength(1000);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.Property(x => x.RecordedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.RecordedAt).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.ExceptionReason)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReasonCode })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryAttempt_ExceptionReason");
        builder.HasMany(x => x.DeliveryOrders)
            .WithOne(x => x.Attempt)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.AttemptId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryAttemptDo_Attempt");
        builder.HasMany(x => x.Attachments)
            .WithOne(x => x.Attempt)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.AttemptId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryPodAttachment_Attempt");

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId, x.CompletedAt, x.Result })
            .HasDatabaseName("IX_SaDeliveryAttempt_Tenant_Stop_Date_Result");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ReasonCode, x.Responsibility, x.CompletedAt })
            .HasDatabaseName("IX_SaDeliveryAttempt_Tenant_Reason_Responsibility_Date");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId, x.AttemptNo })
            .IsUnique()
            .HasDatabaseName("UX_SaDeliveryAttempt_Tenant_Stop_AttemptNo");
    }
}
