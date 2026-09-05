using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDisGroupConfiguration : IEntityTypeConfiguration<SaDisGroup>
{
    public void Configure(EntityTypeBuilder<SaDisGroup> builder)
    {
        builder.ToTable("SaDisGroup");
        builder.HasKey(e => new { e.CompanyCode, e.GroupName, e.PayCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.GroupName).HasMaxLength(40).IsRequired();
        builder.Property(e => e.PayCode).HasMaxLength(40).IsRequired();
        builder.Property(e => e.DiscountType).HasMaxLength(20);
        builder.Property(e => e.GroupStatus).HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);

        builder.HasMany(e => e.Members)
            .WithOne()
            .HasForeignKey(m => new { m.CompanyCode, m.GroupName, m.PayCode })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
