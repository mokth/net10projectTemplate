using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaItemCustConfiguration : IEntityTypeConfiguration<SaItemCust>
{
    public void Configure(EntityTypeBuilder<SaItemCust> builder)
    {
        builder.ToTable("SaItemCust");
        builder.HasKey(e => new { e.CompanyCode, e.CustCode, e.ICode, e.SellingUOM, e.MOQ });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.SellingUOM).HasMaxLength(5).IsRequired();
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.CustICode).HasMaxLength(100).IsRequired();
        builder.Property(e => e.InvDesc).HasMaxLength(300);
        builder.Property(e => e.Currency).HasMaxLength(5);
        builder.Property(e => e.Status).HasMaxLength(10);
        builder.Property(e => e.DG).HasMaxLength(5);
        builder.Property(e => e.SG).HasMaxLength(5);
        builder.Property(e => e.ProjID).HasMaxLength(20);
        builder.Property(e => e.CustModel).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(5);
        builder.Property(e => e.RowVersion).IsRowVersion();

        // No extra lookup index: the PK prefix (CompanyCode, CustCode, ICode) already serves the
        // customer+item searches, and the applied DDL (scripts/init-sales-item-family.sql) is the
        // source of truth for indexes.
    }
}
