using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrWorkCentreConfiguration : IEntityTypeConfiguration<PrWorkCentre>
{
    public void Configure(EntityTypeBuilder<PrWorkCentre> builder)
    {
        builder.ToTable("PrWorkCentre");
        builder.HasKey(e => e.WrkCtrCd);

        builder.Property(e => e.WrkCtrCd).HasColumnName("Wrk_Ctr_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.WrkCtrDes).HasColumnName("Wrk_Ctr_Des").HasMaxLength(30);
        builder.Property(e => e.Class).HasMaxLength(10);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
    }
}
