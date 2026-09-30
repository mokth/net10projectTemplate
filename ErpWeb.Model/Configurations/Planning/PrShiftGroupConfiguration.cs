using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrShiftGroupConfiguration : IEntityTypeConfiguration<PrShiftGroup>
{
    public void Configure(EntityTypeBuilder<PrShiftGroup> builder)
    {
        builder.ToTable("PrShiftGroup");
        builder.HasKey(e => new { e.ShfGrpCd, e.ShiftCd });

        builder.Property(e => e.ShfGrpCd).HasColumnName("ShfGrp_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.ShfGrpDes).HasColumnName("ShfGrp_Des").HasMaxLength(30).IsRequired();
        builder.Property(e => e.ShiftCd).HasColumnName("Shift_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.TotalTime);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.DefaultGrp);
        builder.Property(e => e.ShiftColor).HasMaxLength(20);
    }
}
