using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMachineScheduleReadinessEvidenceConfiguration
    : IEntityTypeConfiguration<ProductionMachineScheduleReadinessEvidence>
{
    public void Configure(EntityTypeBuilder<ProductionMachineScheduleReadinessEvidence> builder)
    {
        builder.ToTable("PrMachineScheduleReadinessEvidence");
        builder.HasKey(x => x.CompanyCode);
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CalendarSourceFingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(x => x.SchemaContractVersion).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ReleaseContractVersion).HasMaxLength(32).IsRequired();
        builder.Property(x => x.VerifiedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.VerifiedBy).HasMaxLength(10).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
