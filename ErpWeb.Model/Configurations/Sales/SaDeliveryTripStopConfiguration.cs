using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryTripStopConfiguration : IEntityTypeConfiguration<SaDeliveryTripStop>
{
    public void Configure(EntityTypeBuilder<SaDeliveryTripStop> builder)
    {
        builder.ToTable("SaDeliveryTripStop", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryTripStop_Status", "Status IN ('PLANNED', 'OUT_FOR_DELIVERY', 'DELIVERED', 'PARTIALLY_DELIVERED', 'FAILED', 'RESCHEDULED', 'CANCELLED')");
            table.HasCheckConstraint("CK_SaDeliveryTripStop_StopSequence", "StopSequence > 0");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TripNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.StopId).HasColumnName("StopID").ValueGeneratedOnAdd();
        builder.Property(x => x.StopSequence).IsRequired();
        builder.Property(x => x.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(x => x.CustNameSnapshot).HasMaxLength(200);
        builder.Property(x => x.ShipNameSnapshot).HasMaxLength(100);
        builder.Property(x => x.ShipAddress1Snapshot).HasMaxLength(100);
        builder.Property(x => x.ShipAddress2Snapshot).HasMaxLength(100);
        builder.Property(x => x.ShipAddress3Snapshot).HasMaxLength(100);
        builder.Property(x => x.ShipAddress4Snapshot).HasMaxLength(100);
        builder.Property(x => x.ShipCitySnapshot).HasMaxLength(50);
        builder.Property(x => x.ShipStateSnapshot).HasMaxLength(50);
        builder.Property(x => x.ShipPostalCodeSnapshot).HasMaxLength(20);
        builder.Property(x => x.ShipCountrySnapshot).HasMaxLength(50);
        builder.Property(x => x.ContactNameSnapshot).HasMaxLength(100);
        builder.Property(x => x.ContactPhoneSnapshot).HasMaxLength(50);
        builder.Property(x => x.PlannedArrivalAt).HasColumnType("datetime2");
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnType("datetime2");
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasMany(x => x.DeliveryOrders)
            .WithOne(x => x.Stop)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryTripStopDo_Stop");
        builder.HasMany(x => x.Attempts)
            .WithOne(x => x.Stop)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryAttempt_Stop");

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopSequence })
            .IsUnique()
            .HasDatabaseName("UX_SaDeliveryTripStop_Tenant_Trip_Sequence");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.TripNo })
            .HasDatabaseName("IX_SaDeliveryTripStop_Tenant_Status_Trip");
    }
}
