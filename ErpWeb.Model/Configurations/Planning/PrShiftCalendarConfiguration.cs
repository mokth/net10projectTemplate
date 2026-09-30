using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrShiftCalendarConfiguration : IEntityTypeConfiguration<PrShiftCalendar>
{
    public void Configure(EntityTypeBuilder<PrShiftCalendar> builder)
    {
        builder.ToTable("PrShiftCalendar");
        builder.HasKey(e => new { e.Dt, e.MachineCode });

        builder.Property(e => e.Dt).IsRequired();
        builder.Property(e => e.DateCd).HasColumnName("Date_Cd").HasMaxLength(1);
        builder.Property(e => e.ShfGrpCd).HasColumnName("ShfGrp_Cd").HasMaxLength(10);
        builder.Property(e => e.MachineCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
    }
}
