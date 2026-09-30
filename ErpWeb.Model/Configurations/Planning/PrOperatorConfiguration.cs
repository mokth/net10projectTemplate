using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrOperatorConfiguration : IEntityTypeConfiguration<PrOperator>
{
    public void Configure(EntityTypeBuilder<PrOperator> builder)
    {
        builder.ToTable("PrOperator");
        builder.HasKey(e => e.Code);

        builder.Property(e => e.Code).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Active);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompanyCode).HasMaxLength(5);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(5);
    }
}
