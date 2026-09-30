using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrDefMaConfiguration : IEntityTypeConfiguration<PrDefMa>
{
    public void Configure(EntityTypeBuilder<PrDefMa> builder)
    {
        builder.ToTable("PrDefMas");
        builder.HasKey(e => e.ICode);

        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.StdBatchSize);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(5);
        builder.Property(e => e.Active);
        builder.Property(e => e.TotalTime);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.Remark).HasMaxLength(1000);
        builder.Property(e => e.Prefix).HasMaxLength(10);
        builder.Property(e => e.DrNo).HasColumnName("DRNo").HasMaxLength(20);
        builder.Property(e => e.ActPrdCode).HasMaxLength(20);

        builder.HasMany(e => e.PrDefMachines)
            .WithOne(e => e.PrDefMa)
            .HasForeignKey(e => e.ProdCode)
            .HasPrincipalKey(e => e.ICode)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrDefProcesses)
            .WithOne(e => e.PrDefMa)
            .HasForeignKey(e => e.ProdCode)
            .HasPrincipalKey(e => e.ICode)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.PrDefWcenters)
            .WithOne(e => e.PrDefMa)
            .HasForeignKey(e => e.ProdCode)
            .HasPrincipalKey(e => e.ICode)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
