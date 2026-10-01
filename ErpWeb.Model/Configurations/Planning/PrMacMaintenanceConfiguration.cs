using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrMacMaintenanceConfiguration : IEntityTypeConfiguration<PrMacMaintenance>
{
    public void Configure(EntityTypeBuilder<PrMacMaintenance> builder)
    {
        builder.ToTable("PrMacMaintenance");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.TrxDate);
        builder.Property(e => e.MacCode).HasMaxLength(30);
        builder.Property(e => e.Description);
        builder.Property(e => e.ActionTaken);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.ReportBy).HasMaxLength(20);
        builder.Property(e => e.ActionBy).HasMaxLength(20);
        builder.Property(e => e.ActionOn);
        builder.Property(e => e.RefCode).HasMaxLength(25);
        builder.Property(e => e.RepType).HasMaxLength(25);
        builder.Property(e => e.MType).HasColumnName("MType").HasMaxLength(10);
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.Property(e => e.Reminder).HasMaxLength(10);
        builder.Property(e => e.StartDateTime);
        builder.Property(e => e.EndDateTime);
        builder.Property(e => e.ReasonCd).HasMaxLength(10);
        builder.Property(e => e.PartsCost).HasPrecision(19, 2).IsRequired().HasDefaultValue(0m);
        builder.Property(e => e.LabourCost).HasPrecision(19, 2).IsRequired().HasDefaultValue(0m);
        builder.Property(e => e.OtherCost).HasPrecision(19, 2).IsRequired().HasDefaultValue(0m);
        builder.Property(e => e.PreventiveUid);
        builder.Property(e => e.Remark).HasMaxLength(500);
        builder.Property(e => e.CompCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);

        builder.HasIndex(e => e.PreventiveUid)
            .IsUnique()
            .HasFilter("[PreventiveUid] IS NOT NULL")
            .HasDatabaseName("UX_PrMacMaintenance_PreventiveUid");
    }
}
