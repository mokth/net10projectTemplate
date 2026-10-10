using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryDriverConfiguration : IEntityTypeConfiguration<SaDeliveryDriver>
{
    public void Configure(EntityTypeBuilder<SaDeliveryDriver> builder)
    {
        builder.ToTable("SaDeliveryDriver", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryDriver_DriverType", "DriverType IN ('EMPLOYEE', 'EXTERNAL')");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.DriverId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DriverId).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DriverName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.MobileNo).HasMaxLength(50);
        builder.Property(x => x.DriverType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.EmployeeCode).HasMaxLength(30);
        builder.Property(x => x.TransporterName).HasMaxLength(100);
        builder.Property(x => x.Active).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Active, x.DriverName })
            .HasDatabaseName("IX_SaDeliveryDriver_Tenant_Active_Name");
    }
}
