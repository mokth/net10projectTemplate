using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public sealed class PoDeliveryFollowUpConfiguration : IEntityTypeConfiguration<PoDeliveryFollowUp>
{
    public void Configure(EntityTypeBuilder<PoDeliveryFollowUp> builder)
    {
        builder.ToTable("PoDeliveryFollowUp");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasColumnType("varchar(10)").HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasColumnType("varchar(10)").HasMaxLength(10).IsRequired();
        builder.Property(x => x.PoNo).HasColumnType("varchar(30)").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PoRelNo).IsRequired();
        builder.Property(x => x.PoLineNo).IsRequired();
        builder.Property(x => x.ContactAtUtc).HasColumnType("datetime2(7)").IsRequired();
        builder.Property(x => x.Outcome).HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierStatedDate).HasColumnType("date");
        builder.Property(x => x.NextFollowUpDate).HasColumnType("date");
        builder.Property(x => x.Note).HasColumnType("nvarchar(500)").HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasColumnType("varchar(10)").HasMaxLength(10).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)").IsRequired();
        builder.HasIndex(x => new
            {
                x.CompanyCode, x.BranchCode, x.PoNo, x.PoRelNo, x.PoLineNo, x.ContactAtUtc
            })
            .HasDatabaseName("IX_PoDeliveryFollowUp_LineContact");
    }
}
