using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrMacMaintenanceImageConfiguration : IEntityTypeConfiguration<PrMacMaintenanceImage>
{
    public void Configure(EntityTypeBuilder<PrMacMaintenanceImage> builder)
    {
        builder.ToTable("PrMacMaintenanceImages");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.RefCode).HasMaxLength(25);
        builder.Property(e => e.ImageUrl).HasColumnName("ImageURL").HasMaxLength(100);
        builder.Property(e => e.Filename).HasColumnName("filename").HasMaxLength(100);
    }
}
