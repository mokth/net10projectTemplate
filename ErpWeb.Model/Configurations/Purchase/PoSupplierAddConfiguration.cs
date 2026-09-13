using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoSupplierAddConfiguration : IEntityTypeConfiguration<PoSupplierAdd>
{
    public void Configure(EntityTypeBuilder<PoSupplierAdd> builder)
    {
        builder.ToTable("POSupplierAdd");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.SuppCode, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SuppCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.SuppName).HasMaxLength(200);
        builder.Property(e => e.Address1).HasMaxLength(100);
        builder.Property(e => e.Address2).HasMaxLength(100);
        builder.Property(e => e.Address3).HasMaxLength(100);
        builder.Property(e => e.Address4).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostalCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Tel).HasMaxLength(50);
        builder.Property(e => e.Fax).HasMaxLength(50);
    }
}
