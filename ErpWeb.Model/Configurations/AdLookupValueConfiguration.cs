using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

/// <summary>
/// Mapping for <see cref="AdLookupValue"/> (plan §3.2).
///
/// <para>
/// The physical <c>Code</c> column is 50 characters — a ceiling for the table, NOT the assignment
/// limit. Each lookup family registers the real consumer-column width in
/// <c>AdLookupTypeRegistry</c>; the service rejects a longer value before save and never truncates it.
/// </para>
/// </summary>
public class AdLookupValueConfiguration : IEntityTypeConfiguration<AdLookupValue>
{
    public const int CompanyCodeLength = 5;
    public const int LookupTypeLength = 40;
    public const int CodeLength = 50;
    public const int DescriptionLength = 150;

    public void Configure(EntityTypeBuilder<AdLookupValue> builder)
    {
        builder.ToTable("AdLookupValue");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).ValueGeneratedOnAdd();

        builder.Property(e => e.CompanyCode).HasMaxLength(CompanyCodeLength).IsRequired();
        builder.Property(e => e.LookupType).HasMaxLength(LookupTypeLength).IsRequired();
        builder.Property(e => e.Code).HasMaxLength(CodeLength).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(DescriptionLength).IsRequired();

        builder.Property(e => e.SortOrder).IsRequired();
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);

        builder.Property(e => e.CreatedDate).HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasMaxLength(20);

        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.LookupType, e.Code })
            .IsUnique()
            .HasDatabaseName("UX_AdLookupValue_Company_Type_Code");

        builder.HasIndex(e => new { e.CompanyCode, e.LookupType, e.IsActive, e.SortOrder })
            .HasDatabaseName("IX_AdLookupValue_Company_Type_Active_Sort");
    }
}
