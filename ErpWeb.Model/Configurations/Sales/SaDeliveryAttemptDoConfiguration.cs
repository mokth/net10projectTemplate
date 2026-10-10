using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryAttemptDoConfiguration : IEntityTypeConfiguration<SaDeliveryAttemptDo>
{
    public void Configure(EntityTypeBuilder<SaDeliveryAttemptDo> builder)
    {
        builder.ToTable("SaDeliveryAttemptDo", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryAttemptDo_Result", "Result IN ('DELIVERED', 'PARTIAL', 'FAILED')");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.AttemptId, x.DoNo });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.AttemptId).HasColumnName("AttemptID").IsRequired();
        builder.Property(x => x.DoNo).HasColumnName("DONo").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Result).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Remark).HasMaxLength(500);

        builder.HasOne(x => x.Do)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.DoNo })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryAttemptDo_SaDO");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DoNo })
            .HasDatabaseName("IX_SaDeliveryAttemptDo_Tenant_DONo");
    }
}
