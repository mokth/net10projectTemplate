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
        builder.Property(x => x.DefaultCustodyWarehouseCode).HasMaxLength(20);
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
        builder.Property(x => x.PostedBy).HasMaxLength(20);
        builder.Property(x => x.RollbackBy).HasMaxLength(20);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.TrxDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.PromisedReturnDate).HasColumnType("datetime2");
        builder.Property(x => x.PostedDate).HasColumnType("datetime2");
        builder.Property(x => x.RollbackDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasOne(x => x.Job)
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(x => x.BaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ConvFactor).HasColumnType("decimal(18,6)");
        builder.Property(x => x.StockStatus).HasMaxLength(10);
        builder.Property(x => x.WarehouseCode).HasMaxLength(10);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.CustodyWarehouseCode).HasMaxLength(20);
        builder.Property(x => x.CustodyLocationCode).HasMaxLength(10);
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
        builder.Property(x => x.PostedBy).HasMaxLength(20);
        builder.Property(x => x.RollbackBy).HasMaxLength(20);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.TrxDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.PostedDate).HasColumnType("datetime2");
        builder.Property(x => x.RollbackDate).HasColumnType("datetime2");
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
        builder.Property(x => x.BaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ConvFactor).HasColumnType("decimal(18,6)");
        builder.Property(x => x.ToWarehouseCode).HasMaxLength(20);
        builder.Property(x => x.ToLocationCode).HasMaxLength(10);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.ReceiptId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconReceiptLine_Receipt_LineNo");
    }
}

public sealed class SubconJobConfiguration : IEntityTypeConfiguration<SubconJob>
{
    public void Configure(EntityTypeBuilder<SubconJob> builder)
    {
        builder.ToTable("SubconJob");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.JobNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.JobKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Mode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierName).HasMaxLength(100);
        builder.Property(x => x.TargetItemCode).HasMaxLength(30);
        builder.Property(x => x.TargetItemName).HasMaxLength(100);
        builder.Property(x => x.TargetUom).HasMaxLength(10);
        builder.Property(x => x.TargetQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.DefinitionCode).HasMaxLength(20);
        builder.Property(x => x.PrimaryDispatchDocNo).HasMaxLength(20);
        builder.Property(x => x.PoNo).HasMaxLength(30);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.TrxDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.PromisedReturnDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.JobNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconJob_Company_Branch_No");
        builder.HasMany(x => x.SourceSnapshots)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Materials)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Outputs)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.SupplierAssignments)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubconJobSourceSnapshotConfiguration : IEntityTypeConfiguration<SubconJobSourceSnapshot>
{
    public void Configure(EntityTypeBuilder<SubconJobSourceSnapshot> builder)
    {
        builder.ToTable("SubconJobSourceSnapshot");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.SourceKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProdCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DefinitionCode).HasMaxLength(20);
        builder.Property(x => x.RootQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.RootUom).HasMaxLength(10);
        builder.Property(x => x.CapturedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CapturedBy).HasMaxLength(20).IsRequired();
    }
}

public sealed class SubconJobMaterialConfiguration : IEntityTypeConfiguration<SubconJobMaterial>
{
    public void Configure(EntityTypeBuilder<SubconJobMaterial> builder)
    {
        builder.ToTable("SubconJobMaterial");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ItemName).HasMaxLength(100);
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10);
        builder.Property(x => x.RequiredQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.SendNowQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ScrapPercent).HasColumnType("decimal(9,4)");
        builder.Property(x => x.WarehouseCode).HasMaxLength(10);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.JobId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconJobMaterial_Job_LineNo");
    }
}

public sealed class SubconJobOutputConfiguration : IEntityTypeConfiguration<SubconJobOutput>
{
    public void Configure(EntityTypeBuilder<SubconJobOutput> builder)
    {
        builder.ToTable("SubconJobOutput");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.OutputKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ItemName).HasMaxLength(100);
        builder.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ExpectedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.WarehouseCode).HasMaxLength(10);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.JobId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconJobOutput_Job_LineNo");
    }
}

public sealed class SubconSupplierAssignmentConfiguration : IEntityTypeConfiguration<SubconSupplierAssignment>
{
    public void Configure(EntityTypeBuilder<SubconSupplierAssignment> builder)
    {
        builder.ToTable("SubconSupplierAssignment");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplierName).HasMaxLength(100);
        builder.Property(x => x.AssignedQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.PoNo).HasMaxLength(30);
        builder.Property(x => x.PromisedReturnDate).HasColumnType("datetime2");
        builder.Property(x => x.DispatchDocNo).HasMaxLength(20);
        builder.Property(x => x.Remarks).HasMaxLength(500);
        builder.HasIndex(x => new { x.JobId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UX_SubconSupplierAssignment_Job_LineNo");
    }
}

public sealed class SubconPostingLinkConfiguration : IEntityTypeConfiguration<SubconPostingLink>
{
    public void Configure(EntityTypeBuilder<SubconPostingLink> builder)
    {
        builder.ToTable("SubconPostingLink");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.DocKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.DocNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PostingRole).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TrxType).HasMaxLength(10).IsRequired();
        builder.Property(x => x.EffectiveAt).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocKind, x.DocId })
            .IsUnique()
            .HasFilter("[IsActive] = 1 AND [PostingRole] = N'PRIMARY'")
            .HasDatabaseName("UX_SubconPostingLink_ActiveDoc");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TrxBatchId, x.DocumentRevision, x.PostingRole })
            .IsUnique()
            .HasDatabaseName("UX_SubconPostingLink_Batch_Role_Rev");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TrxBatchId })
            .HasDatabaseName("IX_SubconPostingLink_BatchLookup");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocKind, x.PostingRole, x.RequestId })
            .IsUnique()
            .HasDatabaseName("UX_SubconPostingLink_RequestId");
    }
}

public sealed class SubconCustodyAllocationConfiguration : IEntityTypeConfiguration<SubconCustodyAllocation>
{
    public void Configure(EntityTypeBuilder<SubconCustodyAllocation> builder)
    {
        builder.ToTable("SubconCustodyAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.StockStatus).HasMaxLength(10).IsRequired();
        builder.Property(x => x.CustodyWarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustodyLocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.DispatchedBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ReturnedBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ConsumedBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.ScrapBaseQty).HasColumnType("decimal(18,4)");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.Dispatch)
            .WithMany()
            .HasForeignKey(x => x.DispatchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DispatchLine)
            .WithMany()
            .HasForeignKey(x => x.DispatchLineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PostingLink)
            .WithMany()
            .HasForeignKey(x => x.PostingLinkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DispatchLineId)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_SubconCustodyAllocation_ActiveDispatchLine");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CustodyBalLocId, x.IsActive })
            .HasDatabaseName("IX_SubconCustodyAllocation_BalLoc");
    }
}

public sealed class SubconSupplierCustodyMapConfiguration : IEntityTypeConfiguration<SubconSupplierCustodyMap>
{
    public void Configure(EntityTypeBuilder<SubconSupplierCustodyMap> builder)
    {
        builder.ToTable("SubconSupplierCustodyMap");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.SupplierCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustodyWarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustodyLocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SupplierCode })
            .IsUnique()
            .HasDatabaseName("UX_SubconSupplierCustodyMap_Supplier");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CustodyWarehouseCode, x.CustodyLocationCode })
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_SubconSupplierCustodyMap_ActiveBin");
    }
}
