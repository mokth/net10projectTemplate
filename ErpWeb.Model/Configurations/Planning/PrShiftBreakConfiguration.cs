using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrShiftBreakConfiguration : IEntityTypeConfiguration<PrShiftBreak>
{
    public void Configure(EntityTypeBuilder<PrShiftBreak> builder)
    {
        builder.ToTable("PrShiftBreak");
        builder.HasKey(e => new { e.CompCode, e.ShiftCd, e.BreakSeq });

        builder.Property(e => e.CompCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ShiftCd).HasColumnName("Shift_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.BreakSeq).IsRequired();
        builder.Property(e => e.BreakFrom).IsRequired();
        builder.Property(e => e.BreakTo).IsRequired();
        builder.Property(e => e.Created);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);

        builder.ToTable(t => t.HasCheckConstraint("CK_PrShiftBreak_BreakSeq", "BreakSeq BETWEEN 1 AND 5"));
    }
}
