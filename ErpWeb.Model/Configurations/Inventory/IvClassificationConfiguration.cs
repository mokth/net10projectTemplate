using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvClassificationConfiguration : IEntityTypeConfiguration<IvClassification>
{
    public void Configure(EntityTypeBuilder<IvClassification> builder)
    {
        builder.ToTable("IvClassification");
        builder.HasKey(e => e.Code);

        builder.Property(e => e.Code).HasMaxLength(3).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(250);
    }
}
