using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branch");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyId).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.CreatedBy).HasMaxLength(50);
        builder.Property(e => e.ModifiedBy).HasMaxLength(50);
        builder.Property(e => e.DeletedBy).HasMaxLength(50);
        builder.Property(e => e.RowVersion)
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate()
            .HasColumnType("rowversion");

        builder.HasIndex(e => new { e.CompanyId, e.BranchCode })
            .IsUnique()
            .HasDatabaseName("UQ_Branch_CompanyId_BranchCode");

        builder.HasIndex(e => new { e.CompanyId, e.IsDeleted, e.IsActive })
            .HasDatabaseName("IX_Branch_Company_Active");

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Branch_Company");

        builder.HasQueryFilter(e => !e.IsDeleted);
    }
}
