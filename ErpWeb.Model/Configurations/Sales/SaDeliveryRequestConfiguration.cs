using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryRequestConfiguration : IEntityTypeConfiguration<SaDeliveryRequest>
{
    public void Configure(EntityTypeBuilder<SaDeliveryRequest> builder)
    {
        builder.ToTable("SaDeliveryRequest");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DeliveryRequestNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ProductCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ProductDescription).HasMaxLength(200);
        builder.Property(x => x.ProductionUom).HasColumnName("ProductionUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.RequestedQty).HasPrecision(18, 4).IsRequired();
        builder.Property(x => x.RequiredDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.DefinitionCode).HasMaxLength(30);
        builder.Property(x => x.WarehouseCode).HasMaxLength(20);
        builder.Property(x => x.ProjectCode).HasMaxLength(20);
        builder.Property(x => x.Priority).HasMaxLength(20);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Remark).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DeliveryRequestNo })
            .IsUnique()
            .HasDatabaseName("UQ_SaDeliveryRequest_Company_Branch_No");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.RequiredDate })
            .HasDatabaseName("IX_SaDeliveryRequest_Company_Branch_Status_Date");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductCode, x.ProductionUom })
            .HasDatabaseName("IX_SaDeliveryRequest_Company_Branch_Product_Uom");

        builder.HasMany(x => x.Sources)
            .WithOne(x => x.DeliveryRequest)
            .HasForeignKey(x => x.DeliveryRequestId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(x => x.WorkOrderAllocations)
            .WithOne(x => x.DeliveryRequest)
            .HasForeignKey(x => x.DeliveryRequestId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasCheckConstraint("CK_SaDeliveryRequest_Status", "Status IN (N'DRAFT', N'RELEASED', N'IN_PRODUCTION', N'COMPLETED', N'CANCELLED')");
        builder.HasCheckConstraint("CK_SaDeliveryRequest_RequestedQty", "RequestedQty > 0");
    }
}

public sealed class SaDeliveryRequestSourceConfiguration : IEntityTypeConfiguration<SaDeliveryRequestSource>
{
    public void Configure(EntityTypeBuilder<SaDeliveryRequestSource> builder)
    {
        builder.ToTable("SaDeliveryRequestSource");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.DeliveryRequestId).HasColumnName("DeliveryRequestID").IsRequired();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.SoNo).HasColumnName("SONo").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustRel).IsRequired();
        builder.Property(x => x.SoLine).HasColumnName("SOLine").IsRequired();
        builder.Property(x => x.ProductCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceUom).HasColumnName("SourceUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.ProductionUom).HasColumnName("ProductionUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.SourceQty).HasPrecision(18, 4).IsRequired();
        builder.Property(x => x.AllocatedProductionQty).HasPrecision(18, 4).IsRequired();
        builder.Property(x => x.CustomerCode).HasMaxLength(60);
        builder.Property(x => x.RequestedDeliveryDate).HasColumnType("datetime2");
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.ReleasedBy).HasMaxLength(20);
        builder.Property(x => x.ReleaseReason).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.SalesOrderDetail)
            .WithMany(x => x.DeliveryRequestSources)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.SoNo, x.CustRel, x.SoLine })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.SoNo, x.CustRel, x.Line })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.DeliveryRequestId)
            .HasDatabaseName("IX_SaDeliveryRequestSource_DeliveryRequest");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SoNo, x.CustRel, x.SoLine })
            .HasDatabaseName("IX_SaDeliveryRequestSource_SO_Line");
        builder.HasIndex(x => new { x.DeliveryRequestId, x.CompanyCode, x.BranchCode, x.SoNo, x.CustRel, x.SoLine })
            .IsUnique()
            .HasDatabaseName("UQ_SaDeliveryRequestSource_Request_SO_Line");
        builder.HasCheckConstraint("CK_SaDeliveryRequestSource_AllocatedQty", "AllocatedProductionQty > 0");
    }
}
