using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrProcessConfiguration : IEntityTypeConfiguration<PrProcess>
{
    public void Configure(EntityTypeBuilder<PrProcess> builder)
    {
        builder.ToTable("PrProcess");
        builder.HasKey(e => new { e.ProcessCd, e.WorkCentre });

        builder.Property(e => e.ProcessCd).HasColumnName("Process_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.ProcessDes).HasColumnName("Process_Des").HasMaxLength(30);
        builder.Property(e => e.Sequence);
        builder.Property(e => e.WorkCentre).HasColumnName("Work_Centre").HasMaxLength(10).IsRequired();
        builder.Property(e => e.Stock);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
    }
}
