using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoCjConfiguration : IEntityTypeConfiguration<PoCj>
{
    public void Configure(EntityTypeBuilder<PoCj> builder)
    {
        builder.ToTable("POCJ");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.CjNo, e.RelNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CjNo).HasColumnName("CJNo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.CjDtFrom).HasColumnName("CJDtFrom").HasColumnType("datetime2");
        builder.Property(e => e.CjDtTo).HasColumnName("CJDtTo").HasColumnType("datetime2");
        builder.Property(e => e.CjOrderQty).HasColumnName("CJOrderQty").HasPrecision(18, 4);
        builder.Property(e => e.CjBalQty).HasColumnName("CJBalQty").HasPrecision(18, 4);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.Remark).HasMaxLength(500);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Cj)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.CjNo, e.RelNo })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
