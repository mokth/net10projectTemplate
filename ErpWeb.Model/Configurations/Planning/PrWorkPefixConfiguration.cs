using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrWorkPefixConfiguration : IEntityTypeConfiguration<PrWorkPefix>
{
    public void Configure(EntityTypeBuilder<PrWorkPefix> builder)
    {
        builder.ToTable("PrWorkPefix");
        builder.HasKey(e => e.Prefix);

        builder.Property(e => e.Prefix).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(30);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
    }
}
