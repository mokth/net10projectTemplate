using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryTripStopDoConfiguration : IEntityTypeConfiguration<SaDeliveryTripStopDo>
{
    public void Configure(EntityTypeBuilder<SaDeliveryTripStopDo> builder)
    {
        builder.ToTable("SaDeliveryTripStopDo");
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.TripNo, x.StopId, x.DoNo });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TripNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.StopId).HasColumnName("StopID").IsRequired();
        builder.Property(x => x.DoNo).HasColumnName("DONo").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PromisedDeliveryDateSnapshot).HasColumnType("datetime2");
        builder.Property(x => x.PromisedFromTimeSnapshot).HasColumnType("time(0)");
        builder.Property(x => x.PromisedToTimeSnapshot).HasColumnType("time(0)");
        builder.Property(x => x.DoDateSnapshot).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.IsActiveAssignment).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.Do)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.DoNo })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryTripStopDo_SaDO");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DoNo, x.IsActiveAssignment })
            .HasDatabaseName("IX_SaDeliveryTripStopDo_Tenant_DONo_Active");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DoNo })
            .IsUnique()
            .HasFilter("[IsActiveAssignment] = 1")
            .HasDatabaseName("UX_SaDeliveryTripStopDo_ActiveAssignment");
    }
}
