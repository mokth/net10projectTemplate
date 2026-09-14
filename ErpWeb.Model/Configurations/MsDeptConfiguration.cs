using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

/// <summary>
/// MsDept is a brand-new table, so the mapping is 1:1 with the create-ms-dept-project.sql
/// column names. No legacy aliases (Active / Created / UserID / UpdatedUID).
/// </summary>
public class MsDeptConfiguration : IEntityTypeConfiguration<MsDept>
{
    public void Configure(EntityTypeBuilder<MsDept> builder)
    {
        builder.ToTable("MsDept");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DeptCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DeptCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.DeptName).HasMaxLength(100);
        builder.Property(e => e.ManagerEmpId).HasMaxLength(20);
        builder.Property(e => e.GlCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.CreatedBy).HasMaxLength(20);
        builder.Property(e => e.ModifiedBy).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
