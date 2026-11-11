using ErpWeb.Model.Entities.Subcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Subcon;

public sealed class SubconPolicyConfiguration : IEntityTypeConfiguration<SubconPolicy>
{
    public void Configure(EntityTypeBuilder<SubconPolicy> builder)
    {
        builder.ToTable("SubconPolicy");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.Version })
            .IsUnique()
            .HasDatabaseName("UX_SubconPolicy_Company_Version");
        builder.HasIndex(x => x.CompanyCode)
            .IsUnique()
            .HasFilter("[IsPublished] = 1")
            .HasDatabaseName("UX_SubconPolicy_Company_Published");
    }
}

public sealed class SubconDispatchConfiguration : IEntityTypeConfiguration<SubconDispatch>
{
    public void Configure(EntityTypeBuilder<SubconDispatch> builder)
    {
        builder.ToTable("SubconDispatch");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Mode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierName).HasMaxLength(100);
        builder.Property(x => x.PoNo).HasMaxLength(30);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.TrxDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.PromisedReturnDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconDispatch_Company_Branch_No");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.TrxDate })
            .HasDatabaseName("IX_SubconDispatch_Workbench");
        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Dispatch)
            .HasForeignKey(x => x.DispatchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.ReceiptAllocations)
            .WithOne(x => x.Dispatch)
            .HasForeignKey(x => x.DispatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubconDispatchLineConfiguration : IEntityTypeConfiguration<SubconDispatchLine>
{
    public void Configure(EntityTypeBuilder<SubconDispatchLine> builder)
    {
        builder.ToTable("SubconDispatchLine");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ItemName).HasMaxLength(100);
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10);
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.WarehouseCode).HasMaxLength(10);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.OperationCode).HasMaxLength(20);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.PromisedReturnDate).HasColumnType("datetime2");
        builder.HasIndex(x => new { x.DispatchId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconDispatchLine_Dispatch_LineNo");
        builder.HasMany(x => x.ReceiptAllocations)
            .WithOne(x => x.DispatchLine)
            .HasForeignKey(x => x.DispatchLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubconReceiptConfiguration : IEntityTypeConfiguration<SubconReceipt>
{
    public void Configure(EntityTypeBuilder<SubconReceipt> builder)
    {
        builder.ToTable("SubconReceipt");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierName).HasMaxLength(100);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.TrxDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconReceipt_Company_Branch_No");
        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Receipt)
            .HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubconReceiptLineConfiguration : IEntityTypeConfiguration<SubconReceiptLine>
{
    public void Configure(EntityTypeBuilder<SubconReceiptLine> builder)
    {
        builder.ToTable("SubconReceiptLine");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Qty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.ReceiptId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconReceiptLine_Receipt_LineNo");
    }
}
