using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrHolidayConfiguration : IEntityTypeConfiguration<PrHoliday>
{
    public void Configure(EntityTypeBuilder<PrHoliday> builder)
    {
        builder.ToTable("PrHoliday");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.DateOff);
        builder.Property(e => e.Description).HasMaxLength(50);
        builder.Property(e => e.Year);
    }
}
