using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryVehicleConfiguration : IEntityTypeConfiguration<SaDeliveryVehicle>
{
    public void Configure(EntityTypeBuilder<SaDeliveryVehicle> builder)
    {
        builder.ToTable("SaDeliveryVehicle", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryVehicle_TransportType", "TransportType IN ('OWN', 'THIRD_PARTY')");
            table.HasCheckConstraint("CK_SaDeliveryVehicle_CapacityWeight", "CapacityWeight IS NULL OR CapacityWeight >= 0");
            table.HasCheckConstraint("CK_SaDeliveryVehicle_CapacityVolume", "CapacityVolume IS NULL OR CapacityVolume >= 0");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.VehicleId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.VehicleId).HasMaxLength(30).IsRequired();
        builder.Property(x => x.RegistrationNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.VehicleType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(100);
        builder.Property(x => x.CapacityWeight).HasPrecision(18, 3);
        builder.Property(x => x.CapacityVolume).HasPrecision(18, 3);
        builder.Property(x => x.TransportType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TransporterName).HasMaxLength(100);
        builder.Property(x => x.Active).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Active, x.RegistrationNo })
            .HasDatabaseName("IX_SaDeliveryVehicle_Tenant_Active_Registration");
    }
}
