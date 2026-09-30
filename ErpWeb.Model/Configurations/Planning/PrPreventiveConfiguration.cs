using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrPreventiveConfiguration : IEntityTypeConfiguration<PrPreventive>
{
    public void Configure(EntityTypeBuilder<PrPreventive> builder)
    {
        builder.ToTable("PrPreventive");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.MachineCd).HasColumnName("Machine_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.DownDt).HasColumnName("Down_Dt").IsRequired();
        builder.Property(e => e.StartTm).HasColumnName("Start_Tm").IsRequired();
        builder.Property(e => e.EndTm).HasColumnName("End_Tm").IsRequired();
        builder.Property(e => e.ReasonCd).HasColumnName("Reason_Cd").HasMaxLength(10);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.Remark).HasMaxLength(100);
    }
}
