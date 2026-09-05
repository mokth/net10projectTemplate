using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCurrRateConfiguration : IEntityTypeConfiguration<SaCurrRate>
{
    public void Configure(EntityTypeBuilder<SaCurrRate> builder)
    {
        builder.ToTable("SaCurrRate");
        builder.HasKey(e => new { e.StartDate, e.EndDate, e.CurrCode });

        builder.Property(e => e.StartDate).HasColumnName("SDate").HasColumnType("datetime2");
        builder.Property(e => e.EndDate).HasColumnName("EDate").HasColumnType("datetime2");
        builder.Property(e => e.CurrCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.HomeCurPerUnit).IsRequired();
        builder.Property(e => e.Status).HasDefaultValue(true);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);

        builder.HasIndex(e => new { e.CurrCode, e.StartDate, e.EndDate })
            .HasDatabaseName("IX_SaCurrRate_CurrCode_Dates");
    }
}
