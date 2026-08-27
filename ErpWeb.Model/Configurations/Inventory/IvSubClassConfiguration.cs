using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvSubClassConfiguration : IEntityTypeConfiguration<IvSubClass>
{
    public void Configure(EntityTypeBuilder<IvSubClass> builder)
    {
        builder.ToTable("IvSubClass");
        builder.HasKey(e => new { e.CompanyCode, e.IClassCode, e.ISubClassCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.IClassCode).HasColumnName("IClass").HasMaxLength(30).IsRequired();
        builder.Property(e => e.ISubClassCode).HasColumnName("ISubClassCode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.ISubClassName).HasColumnName("ISubClassName").HasMaxLength(100);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasOne(e => e.Class)
            .WithMany(e => e.SubClasses)
            .HasForeignKey(e => new { e.CompanyCode, e.IClassCode })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.CompanyCode, e.IClassCode })
            .HasDatabaseName("IX_IvSubClass_Company_IClass");
    }
}
