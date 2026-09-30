using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrShiftConfiguration : IEntityTypeConfiguration<PrShift>
{
    public void Configure(EntityTypeBuilder<PrShift> builder)
    {
        builder.ToTable("PrShift");
        builder.HasKey(e => e.ShiftCd);

        builder.Property(e => e.ShiftCd).HasColumnName("Shift_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.ShiftDes).HasColumnName("Shift_Des").HasMaxLength(30);
        builder.Property(e => e.StartTm).HasColumnName("Start_Tm");
        builder.Property(e => e.EndTm).HasColumnName("End_Tm");
        builder.Property(e => e.BreakTm1From).HasColumnName("Break_Tm1From");
        builder.Property(e => e.BreakTm1To).HasColumnName("Break_Tm1To");
        builder.Property(e => e.BreakTm2From).HasColumnName("Break_Tm2From");
        builder.Property(e => e.BreakTm2To).HasColumnName("Break_Tm2To");
        builder.Property(e => e.BreakTm3From).HasColumnName("Break_Tm3From");
        builder.Property(e => e.BreakTm3To).HasColumnName("Break_Tm3To");
        builder.Property(e => e.BreakTm4From).HasColumnName("Break_Tm4From");
        builder.Property(e => e.BreakTm4To).HasColumnName("Break_Tm4To");
        builder.Property(e => e.BreakTm5From).HasColumnName("Break_Tm5From");
        builder.Property(e => e.BreakTm5To).HasColumnName("Break_Tm5To");
        builder.Property(e => e.BreakStorageVersion).HasDefaultValue((byte)0);
        builder.Property(e => e.OtStartTime).HasColumnName("OT_StartTime");
        builder.Property(e => e.OverrideMrpPlan).HasColumnName("Override_MRP_Plan").HasMaxLength(1);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.TotalTime).HasColumnName("totaltime");
    }
}
