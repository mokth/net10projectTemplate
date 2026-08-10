using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

internal static class InventoryEntityConfig
{
    public static void ConfigureInventoryEntity<T>(EntityTypeBuilder<T> builder)
        where T : InventoryEntity
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.CreatedBy).HasMaxLength(50);
        builder.Property(e => e.ModifiedBy).HasMaxLength(50);
        builder.Property(e => e.RowVersion)
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate()
            .HasColumnType("rowversion");
    }

    public static void ConfigureSoftDeleteCompany<T>(EntityTypeBuilder<T> builder)
        where T : SoftDeletableCompanyEntity
    {
        ConfigureInventoryEntity(builder);
        builder.Property(e => e.CompanyId).IsRequired();
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.DeletedBy).HasMaxLength(50);
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasQueryFilter(e => !e.IsDeleted);
    }

    public static void ConfigureSoftDeleteBranch<T>(EntityTypeBuilder<T> builder)
        where T : SoftDeletableBranchEntity
    {
        ConfigureInventoryEntity(builder);
        builder.Property(e => e.CompanyId).IsRequired();
        builder.Property(e => e.BranchId).IsRequired();
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.DeletedBy).HasMaxLength(50);
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(e => e.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasQueryFilter(e => !e.IsDeleted);
    }

    public static void ConfigureBranchScoped<T>(EntityTypeBuilder<T> builder)
        where T : BranchScopedEntity
    {
        ConfigureInventoryEntity(builder);
        builder.Property(e => e.CompanyId).IsRequired();
        builder.Property(e => e.BranchId).IsRequired();
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(e => e.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public static void ConfigureCompanyScoped<T>(EntityTypeBuilder<T> builder)
        where T : CompanyScopedEntity
    {
        ConfigureInventoryEntity(builder);
        builder.Property(e => e.CompanyId).IsRequired();
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(e => e.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
