using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryRequestStockReservationConfiguration
    : IEntityTypeConfiguration<SaDeliveryRequestStockReservation>
{
    public void Configure(EntityTypeBuilder<SaDeliveryRequestStockReservation> builder)
    {
        builder.ToTable("SaDeliveryRequestStockReservation", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryRequestStockReservation_ReservedQty", "ReservedQty > 0");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.DeliveryRequestId).HasColumnName("DeliveryRequestID").IsRequired();
        builder.Property(x => x.DeliveryRequestSourceId).HasColumnName("DeliveryRequestSourceID").IsRequired();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BalLocId).HasColumnName("BalLocID").IsRequired();
        builder.Property(x => x.ReservedQty).HasPrecision(18, 4).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.ReleasedBy).HasMaxLength(20);
        builder.Property(x => x.ReleaseReason).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.DeliveryRequest)
            .WithMany()
            .HasForeignKey(x => x.DeliveryRequestId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.DeliveryRequestSource)
            .WithMany()
            .HasForeignKey(x => x.DeliveryRequestSourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.BalLoc)
            .WithMany()
            .HasForeignKey(x => x.BalLocId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DeliveryRequestId, x.IsActive })
            .HasDatabaseName("IX_DrStockReservation_Dr_Active");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DeliveryRequestSourceId, x.IsActive })
            .HasDatabaseName("IX_DrStockReservation_Source_Active");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BalLocId, x.IsActive })
            .HasDatabaseName("IX_DrStockReservation_BalLoc_Active");
        builder.HasIndex(x => new { x.DeliveryRequestSourceId, x.BalLocId })
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_DrStockReservation_Source_BalLoc_Active");
    }
}
