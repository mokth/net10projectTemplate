using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class MsLhdnUomConfiguration : IEntityTypeConfiguration<MsLhdnUom>
{
    public void Configure(EntityTypeBuilder<MsLhdnUom> builder)
    {
        builder.ToTable("MsLHDNUOM");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.Code).HasColumnName("Code").HasMaxLength(3);
        builder.Property(e => e.Measurement).HasColumnName("Measurement").HasMaxLength(250);
    }
}
