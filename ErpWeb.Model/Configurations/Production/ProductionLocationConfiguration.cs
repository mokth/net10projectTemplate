using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionLocationConfiguration : IEntityTypeConfiguration<ProductionLocation>
{
    public void Configure(EntityTypeBuilder<ProductionLocation> builder)
    {
        builder.ToTable("PrProductionLocation");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(200).IsRequired();
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .HasName("AK_PrProductionLocation_Tenant_Id");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Code })
            .IsUnique().HasDatabaseName("UQ_PrProductionLocation_Code");
    }
}
