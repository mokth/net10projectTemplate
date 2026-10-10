using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryExceptionReasonConfiguration : IEntityTypeConfiguration<SaDeliveryExceptionReason>
{
    public void Configure(EntityTypeBuilder<SaDeliveryExceptionReason> builder)
    {
        builder.ToTable("SaDeliveryExceptionReason", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryExceptionReason_Responsibility", "Responsibility IN ('DRIVER', 'WAREHOUSE', 'CUSTOMER', 'SALES', 'VEHICLE', 'EXTERNAL', 'WEATHER_TRAFFIC', 'OTHER')");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.ReasonCode });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Responsibility).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Active).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.SortOrder).HasDefaultValue(0).IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Active, x.SortOrder, x.Description })
            .HasDatabaseName("IX_SaDeliveryExceptionReason_Tenant_Active_Sort");
    }
}
