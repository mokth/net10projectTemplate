using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoOrderConfiguration : IEntityTypeConfiguration<PoOrder>
{
    public void Configure(EntityTypeBuilder<PoOrder> builder)
    {
        builder.ToTable("POOrder");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.PoNo, e.PoRelNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.PoNo).HasColumnName("PONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.PoRelNo).HasColumnName("PORelNo").IsRequired();
        builder.Property(e => e.PoDate).HasColumnName("PODt").HasColumnType("datetime2");
        builder.Property(e => e.Buyer).HasMaxLength(20);
        builder.Property(e => e.PoType).HasColumnName("POType").HasMaxLength(20);
        builder.Property(e => e.VendCode).HasMaxLength(60);
        builder.Property(e => e.VendName).HasMaxLength(200);
        builder.Property(e => e.VendAddress1).HasMaxLength(100);
        builder.Property(e => e.VendAddress2).HasMaxLength(100);
        builder.Property(e => e.VendAddress3).HasMaxLength(100);
        builder.Property(e => e.VendAddress4).HasMaxLength(100);
        builder.Property(e => e.VendCity).HasMaxLength(50);
        builder.Property(e => e.VendState).HasMaxLength(50);
        builder.Property(e => e.VendPostal).HasMaxLength(20);
        builder.Property(e => e.VendCountryCode).HasMaxLength(50);
        builder.Property(e => e.VendTel).HasMaxLength(50);
        builder.Property(e => e.VendFax).HasMaxLength(50);
        builder.Property(e => e.CurCode).HasMaxLength(20);
        builder.Property(e => e.ShipCode).HasMaxLength(20);
        builder.Property(e => e.TermCode).HasMaxLength(20);
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.Email).HasMaxLength(100);
        builder.Property(e => e.Website).HasMaxLength(100);
        builder.Property(e => e.ShipName).HasMaxLength(100);
        builder.Property(e => e.ShipAddress1).HasMaxLength(100);
        builder.Property(e => e.ShipAddress2).HasMaxLength(100);
        builder.Property(e => e.ShipAddress3).HasMaxLength(100);
        builder.Property(e => e.ShipAddress4).HasMaxLength(100);
        builder.Property(e => e.ShipCity).HasMaxLength(50);
        builder.Property(e => e.ShipState).HasMaxLength(50);
        builder.Property(e => e.ShipPostal).HasMaxLength(20);
        builder.Property(e => e.ShipCountryCode).HasMaxLength(50);
        builder.Property(e => e.ShipTel).HasMaxLength(50);
        builder.Property(e => e.ShipFax).HasMaxLength(50);
        builder.Property(e => e.TaxGrpCode).HasMaxLength(20);
        builder.Property(e => e.TaxPercentage).HasPrecision(18, 6);
        builder.Property(e => e.TaxAmount).HasPrecision(18, 2);
        builder.Property(e => e.Discount).HasPrecision(18, 6);
        builder.Property(e => e.SiRemark).HasColumnName("SIRemark").HasMaxLength(500);
        builder.Property(e => e.Status).HasColumnName("POStat").HasMaxLength(20).IsRequired();
        builder.Property(e => e.RegNo).HasMaxLength(50);
        builder.Property(e => e.DeptCode).HasMaxLength(20);
        builder.Property(e => e.OneTimeItemYn).HasColumnName("OneTime_ItemYN");
        builder.Property(e => e.VCode).HasColumnName("VCode").HasMaxLength(60);
        builder.Property(e => e.BuyingTerm).HasMaxLength(20);
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30);
        builder.Property(e => e.InvNo).HasMaxLength(30);
        builder.Property(e => e.CostCode).HasMaxLength(20);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.PoCosting).HasColumnName("POCosting");
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.Type).HasMaxLength(20);
        builder.Property(e => e.Prefix).HasColumnName("prefix").HasMaxLength(20);
        builder.Property(e => e.CheckBy).HasMaxLength(20);
        builder.Property(e => e.CheckOn).HasColumnType("datetime2");
        builder.Property(e => e.ApprovedBy).HasMaxLength(20);
        builder.Property(e => e.ApprovedOn).HasColumnType("datetime2");
        builder.Property(e => e.AuthorisedBy).HasMaxLength(20);
        builder.Property(e => e.FinClosed).IsRequired();
        builder.Property(e => e.FinClosedOn).HasColumnType("datetime2");
        builder.Property(e => e.FinClosedBy).HasMaxLength(20);
        builder.Property(e => e.CloseReason).HasMaxLength(200);
        builder.Property(e => e.ClosedBy).HasMaxLength(20);
        builder.Property(e => e.ClosedOn).HasColumnType("datetime2");
        builder.Property(e => e.QuatationNo).HasMaxLength(50);
        builder.Property(e => e.ExpiryDate).HasColumnType("datetime2");
        builder.Property(e => e.HdrType).HasMaxLength(20);
        builder.Property(e => e.Ref1).HasMaxLength(50);
        builder.Property(e => e.Ref2).HasMaxLength(50);
        builder.Property(e => e.Ref3).HasMaxLength(50);
        builder.Property(e => e.Ref4).HasMaxLength(50);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Order)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.PoNo, e.PoRelNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.PoDate })
            .HasDatabaseName("IX_POOrder_Company_Branch_Status_PoDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.VendCode })
            .HasDatabaseName("IX_POOrder_Company_Branch_VendCode");
    }
}
