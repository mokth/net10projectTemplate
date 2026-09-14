using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

/// <summary>
/// MsProject is a brand-new table, so the mapping is 1:1 with the create-ms-dept-project.sql
/// column names. No legacy aliases (Active / Created / UserID / UpdatedUID).
/// </summary>
public class MsProjectConfiguration : IEntityTypeConfiguration<MsProject>
{
    public void Configure(EntityTypeBuilder<MsProject> builder)
    {
        builder.ToTable("MsProject");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.ProjCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ProjCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProjName).HasMaxLength(150);
        builder.Property(e => e.CustCode).HasMaxLength(20);
        builder.Property(e => e.DeptCode).HasMaxLength(20);
        builder.Property(e => e.ManagerEmpId).HasMaxLength(20);
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.BudgetAmnt).HasColumnType("decimal(18,4)");
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.CreatedBy).HasMaxLength(20);
        builder.Property(e => e.ModifiedBy).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
