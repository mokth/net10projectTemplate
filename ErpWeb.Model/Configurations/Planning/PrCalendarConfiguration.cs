using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrCalendarConfiguration : IEntityTypeConfiguration<PrCalendar>
{
    public void Configure(EntityTypeBuilder<PrCalendar> builder)
    {
        builder.ToTable("PrCalendar");
        builder.HasKey(e => e.Dt);

        builder.Property(e => e.Dt).IsRequired();
        builder.Property(e => e.DateCd).HasColumnName("Date_Cd").HasMaxLength(1);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
    }
}
