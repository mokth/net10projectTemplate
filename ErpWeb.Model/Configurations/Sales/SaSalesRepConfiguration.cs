using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaSalesRepConfiguration : IEntityTypeConfiguration<SaSalesRep>
{
    public void Configure(EntityTypeBuilder<SaSalesRep> builder)
    {
        builder.ToTable("SaSalesRep");
        builder.HasKey(e => new { e.CompanyCode, e.SrepCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SrepCode).HasColumnName("SRepCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.SrepName).HasColumnName("SRepName").HasMaxLength(200);
        builder.Property(e => e.Address1).HasMaxLength(100);
        builder.Property(e => e.Address2).HasMaxLength(100);
        builder.Property(e => e.Address3).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostalCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Tel).HasMaxLength(50);
        builder.Property(e => e.Mobile).HasMaxLength(50);
        builder.Property(e => e.Email).HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("Active");
        builder.Property(e => e.CommissionRate).HasPrecision(18, 6);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
    }
}
