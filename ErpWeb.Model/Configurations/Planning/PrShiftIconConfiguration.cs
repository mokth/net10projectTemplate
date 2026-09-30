using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrShiftIconConfiguration : IEntityTypeConfiguration<PrShiftIcon>
{
    public void Configure(EntityTypeBuilder<PrShiftIcon> builder)
    {
        builder.ToTable("PrShiftIcon");
        builder.HasKey(e => new { e.ShiftCd, e.ShiftPeriod });

        builder.Property(e => e.ShiftCd).HasColumnName("Shift_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.ShiftPeriod).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ImgPath).HasColumnName("imgPath").HasMaxLength(1000);
    }
}
