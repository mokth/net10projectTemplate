using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrDefBomConfiguration : IEntityTypeConfiguration<PrDefBOM>
{
    public void Configure(EntityTypeBuilder<PrDefBOM> builder)
    {
        builder.ToTable("PrDefBOM");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.BomHdrId).HasColumnName("BomHdrId").IsRequired();
        builder.Property(e => e.OperationId).HasColumnName("OperationID");
        builder.Property(e => e.ProducingRouteStepId).HasColumnName("ProducingRouteStepID");
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.IName).HasMaxLength(200);
        builder.Property(e => e.StdQty).HasPrecision(18, 4);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.SeqNo).HasDefaultValue(0).ValueGeneratedNever();
        builder.Property(e => e.ScrapPercent).HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
        builder.Property(e => e.Warehouse).HasMaxLength(20);
        builder.Property(e => e.BomDefault).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.AlternateGroupCode).HasMaxLength(30);
        builder.Property(e => e.WipBomDefault).HasColumnName("WIPBomDefault").HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(e => e.Tolerance).HasPrecision(18, 4);
        builder.Property(e => e.IssueMethod).HasMaxLength(20).IsRequired()
            .HasDefaultValue(PrMaterialIssueMethods.Manual).ValueGeneratedNever();
        builder.Property(e => e.SupplySource).HasMaxLength(40).IsRequired()
            .HasDefaultValue(PrMaterialSupplySources.Purchased).ValueGeneratedNever();
        builder.Ignore(e => e.MaterialStandardQty);
        builder.Ignore(e => e.StandardUom);
        builder.Ignore(e => e.TolerancePercent);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.BomHdrId, e.OperationKey, e.ICode })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UQ_PrDefBOM_Company_Hdr_Process_ICode");

        builder.HasIndex(e => new { e.CompanyCode, e.ProdCode })
            .HasDatabaseName("IX_PrDefBOM_Company_ProdCode");

        builder.HasIndex(e => new { e.CompanyCode, e.ICode })
            .HasDatabaseName("IX_PrDefBOM_Company_ICode");

        builder.HasIndex(e => e.BomHdrId)
            .HasDatabaseName("IX_PrDefBOM_BomHdrId");

        builder.HasIndex(e => e.OperationId)
            .HasDatabaseName("IX_PrDefBOM_OperationID");

        builder.HasIndex(e => e.ProducingRouteStepId)
            .HasDatabaseName("IX_PrDefBOM_ProducingRouteStepID");

        // Defense-in-depth: at most one default per (header, operation, group). Application
        // validation still requires at least one default; legacy null groups are excluded.
        builder.HasIndex(e => new { e.BomHdrId, e.OperationId, e.AlternateGroupCode })
            .IsUnique()
            .HasFilter("[BomDefault] = 1 AND [OperationId] IS NOT NULL AND [AlternateGroupCode] IS NOT NULL")
            .HasDatabaseName("UX_PrDefBOM_OneDefaultPerGroup");

        builder.HasOne(e => e.Operation)
            .WithMany(e => e.Materials)
            .HasForeignKey(e => e.OperationId)
            .OnDelete(DeleteBehavior.NoAction);

        // Producer reference is a structural pointer only: a NO ACTION edge keeps the single
        // cascade path (header -> route step) intact and avoids SQL Server multiple-cascade-path
        // errors. The service clears ProducingRouteStepID before deleting route steps.
        builder.HasOne(e => e.ProducingRouteStep)
            .WithMany()
            .HasForeignKey(e => e.ProducingRouteStepId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(e => e.BranchDefaults)
            .WithOne(e => e.Material!)
            .HasForeignKey(e => e.MaterialId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
