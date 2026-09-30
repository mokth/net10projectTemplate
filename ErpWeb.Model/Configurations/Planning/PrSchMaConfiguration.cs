using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchMaConfiguration : IEntityTypeConfiguration<PrSchMa>
{
    public void Configure(EntityTypeBuilder<PrSchMa> builder)
    {
        builder.ToTable("PrSchMas");
        builder.HasKey(e => new { e.ScheCode, e.RelNo });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(15);
        builder.Property(e => e.DrNo).HasColumnName("DRNo").HasMaxLength(50);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.StdBatchSize);
        builder.Property(e => e.ScheQty);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(5);
        builder.Property(e => e.Active);
        builder.Property(e => e.StartDate);
        builder.Property(e => e.CompletedDate);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ConsignQty);
        builder.Property(e => e.DesireQty);
        builder.Property(e => e.DeliveryQty);
        builder.Property(e => e.Remarks).HasMaxLength(1000);
        builder.Property(e => e.FinalIssue);
        builder.Property(e => e.Release);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.TransactionDate);
        builder.Property(e => e.ImageUrl).HasMaxLength(100);
        builder.Property(e => e.StartFromStartDate);

        builder.HasMany(e => e.PrSchBoms)
            .WithOne(e => e.PrSchMa)
            .HasForeignKey(e => new { e.ScheCode, e.RelNo })
            .HasPrincipalKey(e => new { e.ScheCode, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrSchLabours)
            .WithOne(e => e.PrSchMa)
            .HasForeignKey(e => new { e.ScheCode, e.RelNo })
            .HasPrincipalKey(e => new { e.ScheCode, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrSchMachines)
            .WithOne(e => e.PrSchMa)
            .HasForeignKey(e => new { e.ScheCode, e.RelNo })
            .HasPrincipalKey(e => new { e.ScheCode, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrSchProcesses)
            .WithOne(e => e.PrSchMa)
            .HasForeignKey(e => new { e.ScheCode, e.RelNo })
            .HasPrincipalKey(e => new { e.ScheCode, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrSchWcenters)
            .WithOne(e => e.PrSchMa)
            .HasForeignKey(e => new { e.ScheCode, e.RelNo })
            .HasPrincipalKey(e => new { e.ScheCode, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
