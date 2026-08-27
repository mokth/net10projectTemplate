using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class MsRunningNoConfiguration : IEntityTypeConfiguration<MsRunningNo>
{
    public void Configure(EntityTypeBuilder<MsRunningNo> builder)
    {
        builder.ToTable("MsRunningNo");
        builder.HasKey(e => new { e.CompanyCode, e.DocKey });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.DocKey).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
    }
}
