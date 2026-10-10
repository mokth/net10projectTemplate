using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryTripConfiguration : IEntityTypeConfiguration<SaDeliveryTrip>
{
    public void Configure(EntityTypeBuilder<SaDeliveryTrip> builder)
    {
        builder.ToTable("SaDeliveryTrip", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryTrip_Status", "Status IN ('PLANNED', 'OUT_FOR_DELIVERY', 'COMPLETED', 'CANCELLED')");
            table.HasCheckConstraint("CK_SaDeliveryTrip_TransportType", "TransportType IN ('OWN', 'THIRD_PARTY')");
            table.HasCheckConstraint("CK_SaDeliveryTrip_Costs", "(FuelCost IS NULL OR FuelCost >= 0) AND (TollCost IS NULL OR TollCost >= 0) AND (ParkingCost IS NULL OR ParkingCost >= 0) AND (OtherCost IS NULL OR OtherCost >= 0)");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TripNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.TripDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.Status).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DriverId).HasMaxLength(30);
        builder.Property(x => x.DriverNameSnapshot).HasMaxLength(100);
        builder.Property(x => x.DriverMobileSnapshot).HasMaxLength(50);
        builder.Property(x => x.VehicleId).HasMaxLength(30);
        builder.Property(x => x.VehicleRegistrationSnapshot).HasMaxLength(50);
        builder.Property(x => x.TransportType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TransporterNameSnapshot).HasMaxLength(100);
        builder.Property(x => x.PlannedDepartureAt).HasColumnType("datetime2");
        builder.Property(x => x.ActualDepartureAt).HasColumnType("datetime2");
        builder.Property(x => x.CompletedAt).HasColumnType("datetime2");
        builder.Property(x => x.CancelledAt).HasColumnType("datetime2");
        builder.Property(x => x.CancelledBy).HasMaxLength(20);
        builder.Property(x => x.CancelReason).HasMaxLength(500);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.FuelCost).HasPrecision(18, 2);
        builder.Property(x => x.TollCost).HasPrecision(18, 2);
        builder.Property(x => x.ParkingCost).HasPrecision(18, 2);
        builder.Property(x => x.OtherCost).HasPrecision(18, 2);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.Driver)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.DriverId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryTrip_Driver");
        builder.HasOne(x => x.Vehicle)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.VehicleId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryTrip_Vehicle");
        builder.HasMany(x => x.Stops)
            .WithOne(x => x.Trip)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryTripStop_Trip");

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TripDate, x.Status })
            .HasDatabaseName("IX_SaDeliveryTrip_Tenant_Date_Status");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DriverId, x.TripDate })
            .HasDatabaseName("IX_SaDeliveryTrip_Tenant_Driver_Date");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.VehicleId, x.TripDate })
            .HasDatabaseName("IX_SaDeliveryTrip_Tenant_Vehicle_Date");
    }
}
