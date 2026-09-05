using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class IvMsCodeConfiguration : IEntityTypeConfiguration<IvMsCode>
{
    public void Configure(EntityTypeBuilder<IvMsCode> builder)
    {
        builder.ToTable("IvMSCode");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("ID")
            .ValueGeneratedOnAdd();

        builder.Property(e => e.Code).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.Property(e => e.CodeType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Latitude).HasColumnType("decimal(9,6)");
        builder.Property(e => e.Longitude).HasColumnType("decimal(9,6)");

        builder.HasIndex(e => new { e.CodeType, e.Code })
            .IsUnique()
            .HasDatabaseName("UX_IvMSCode_CodeType_Code");
    }
}
