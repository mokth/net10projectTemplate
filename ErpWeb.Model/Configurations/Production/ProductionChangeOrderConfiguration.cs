using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionChangeOrderConfiguration : IEntityTypeConfiguration<ProductionChangeOrder>
{
    public void Configure(EntityTypeBuilder<ProductionChangeOrder> builder)
    {
        builder.ToTable("PrWorkOrderChange");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ChangeOrderNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.RequestedBy).HasMaxLength(10);
        builder.Property(x => x.ApprovedBy).HasMaxLength(10);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.ChangeOrderNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrderChange_Company_Number");
        builder.HasIndex(x => new { x.WorkOrderId, x.Status })
            .HasDatabaseName("IX_PrWorkOrderChange_Order_Status");
        builder.HasMany(x => x.Lines)
            .WithOne(x => x.ChangeOrder!)
            .HasForeignKey(x => x.ChangeOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProductionChangeOrderLineConfiguration : IEntityTypeConfiguration<ProductionChangeOrderLine>
{
    public void Configure(EntityTypeBuilder<ProductionChangeOrderLine> builder)
    {
        builder.ToTable("PrWorkOrderChangeLine");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ChangeOrderId).HasColumnName("ChangeOrderID");
        builder.Property(x => x.ChangeType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.TargetUid).HasColumnName("TargetUID");
        builder.Property(x => x.FieldName).HasMaxLength(100);
        builder.Property(x => x.BeforeValue).HasMaxLength(2000);
        builder.Property(x => x.AfterValue).HasMaxLength(2000);
        builder.HasIndex(x => new { x.ChangeOrderId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrderChangeLine_Change_Line");
    }
}

