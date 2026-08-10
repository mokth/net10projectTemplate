using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class InventoryDocumentConfiguration : IEntityTypeConfiguration<InventoryDocument>
{
    public void Configure(EntityTypeBuilder<InventoryDocument> builder)
    {
        builder.ToTable("InventoryDocument");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.DocType).HasConversion<int>();
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.ReferenceNo).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.ApprovedBy).HasMaxLength(50);
        builder.Property(e => e.PostedBy).HasMaxLength(50);
        builder.Property(e => e.AllowZeroCost).HasDefaultValue(false);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.DocType, e.DocNo })
            .IsUnique()
            .HasDatabaseName("UQ_InventoryDocument_Company_Branch_DocType_DocNo");
        builder.HasMany(e => e.Lines)
            .WithOne()
            .HasForeignKey(l => l.DocumentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_InventoryDocumentLine_Document");
    }
}

public class InventoryDocumentLineConfiguration : IEntityTypeConfiguration<InventoryDocumentLine>
{
    public void Configure(EntityTypeBuilder<InventoryDocumentLine> builder)
    {
        builder.ToTable("InventoryDocumentLine");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.Qty).HasPrecision(19, 6);
        builder.Property(e => e.ConversionRateUsed).HasPrecision(19, 10);
        builder.Property(e => e.QtyInBase).HasPrecision(19, 6);
        builder.Property(e => e.UnitCost).HasPrecision(19, 6);
        builder.Property(e => e.TotalCost).HasPrecision(19, 4);
        builder.Property(e => e.LotNo).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.Direction).HasConversion<int?>();
        builder.HasIndex(e => new { e.DocumentId, e.LineNo })
            .IsUnique()
            .HasDatabaseName("UQ_InventoryDocumentLine_DocumentId_LineNo");
        builder.HasMany(e => e.LotSplits)
            .WithOne()
            .HasForeignKey(s => s.DocumentLineId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_InventoryDocumentLineLotSplit_Line");
    }
}

public class StockMovementAllocationConfiguration : IEntityTypeConfiguration<StockMovementAllocation>
{
    public void Configure(EntityTypeBuilder<StockMovementAllocation> builder)
    {
        builder.ToTable("StockMovementAllocation");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.Quantity).HasPrecision(19, 6);
        builder.Property(e => e.UnitCost).HasPrecision(19, 6);
        builder.Property(e => e.Amount).HasPrecision(19, 4);
        builder.HasIndex(e => e.StockLedgerId)
            .IsUnique()
            .HasDatabaseName("UQ_StockMovementAllocation_StockLedgerId");
        builder.HasOne<StockLedger>()
            .WithMany()
            .HasForeignKey(e => e.StockLedgerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockMovementAllocation_StockLedger");
        builder.HasOne<InventoryDocumentLine>()
            .WithMany()
            .HasForeignKey(e => e.DocumentLineId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockMovementAllocation_DocumentLine");
    }
}

public class StockLedgerConfiguration : IEntityTypeConfiguration<StockLedger>
{
    public void Configure(EntityTypeBuilder<StockLedger> builder)
    {
        builder.ToTable("StockLedger");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.DocType).HasConversion<int>();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ReferenceNo).HasMaxLength(50);
        builder.Property(e => e.PostedBy).HasMaxLength(50);
        builder.Property(e => e.SKU).HasMaxLength(40).IsRequired();
        builder.Property(e => e.ItemDescription).HasMaxLength(200).IsRequired();
        builder.Property(e => e.UOMCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LocationCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LotNo).HasMaxLength(50);
        builder.Property(e => e.ReasonCodeValue).HasMaxLength(20);
        builder.Property(e => e.CurrencyCode).HasMaxLength(3).HasDefaultValue("MYR");
        builder.Property(e => e.CostingMethod).HasConversion<int>();
        builder.Property(e => e.UnitQty).HasPrecision(19, 6);
        builder.Property(e => e.ConversionRateUsed).HasPrecision(19, 10);
        builder.Property(e => e.QtyInBase).HasPrecision(19, 6);
        builder.Property(e => e.QtyOutBase).HasPrecision(19, 6);
        builder.Property(e => e.UnitCost).HasPrecision(19, 6);
        builder.Property(e => e.Amount).HasPrecision(19, 4);
        builder.Property(e => e.ExchangeRate).HasPrecision(19, 10);
        builder.Property(e => e.BaseAmount).HasPrecision(19, 4);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.ItemVariantId, e.WarehouseId, e.TransactionDate, e.LedgerSequence, e.Id })
            .HasDatabaseName("IX_StockLedger_Item_WH_Date");
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.DocType, e.DocNo })
            .HasDatabaseName("IX_StockLedger_Doc");
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.DocumentId })
            .HasDatabaseName("IX_StockLedger_DocumentId");
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.WarehouseId, e.TransactionDate })
            .HasDatabaseName("IX_StockLedger_WH_Date");
    }
}

public class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("StockBalance");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.QtyOnHand).HasPrecision(19, 6);
        builder.Property(e => e.ReservedQty).HasPrecision(19, 6);
        builder.Ignore(e => e.AvailableQty);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.WarehouseId, e.LocationId, e.ItemVariantId })
            .IsUnique()
            .HasDatabaseName("UQ_StockBalance_Grain");
    }
}

public class ItemCostConfiguration : IEntityTypeConfiguration<ItemCost>
{
    public void Configure(EntityTypeBuilder<ItemCost> builder)
    {
        builder.ToTable("ItemCost");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.AverageCost).HasPrecision(19, 6);
        builder.Property(e => e.LastCost).HasPrecision(19, 6);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.WarehouseId, e.ItemVariantId })
            .IsUnique()
            .HasDatabaseName("UQ_ItemCost_Grain");
    }
}

public class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.ToTable("DocumentSequence");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.DocType).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Prefix).HasMaxLength(10).IsRequired();
        builder.Property(e => e.NumberLength).HasDefaultValue(4);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.DocType, e.YearMonth })
            .IsUnique()
            .HasDatabaseName("UQ_DocumentSequence_Grain");
    }
}

public class LedgerSequenceConfiguration : IEntityTypeConfiguration<LedgerSequence>
{
    public void Configure(EntityTypeBuilder<LedgerSequence> builder)
    {
        builder.ToTable("LedgerSequence");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId })
            .IsUnique()
            .HasDatabaseName("UQ_LedgerSequence_Company_Branch");
    }
}

public class StockTakeConfiguration : IEntityTypeConfiguration<StockTake>
{
    public void Configure(EntityTypeBuilder<StockTake> builder)
    {
        builder.ToTable("StockTake");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.StockTakeNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.ApprovedBy).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.StockTakeNo })
            .IsUnique()
            .HasDatabaseName("UQ_StockTake_Company_Branch_No");
        builder.HasMany(e => e.Lines)
            .WithOne()
            .HasForeignKey(l => l.StockTakeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTakeLine_StockTake");
    }
}

public class StockTakeLineConfiguration : IEntityTypeConfiguration<StockTakeLine>
{
    public void Configure(EntityTypeBuilder<StockTakeLine> builder)
    {
        builder.ToTable("StockTakeLine");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.SystemQty).HasPrecision(19, 6);
        builder.Property(e => e.CountedQty).HasPrecision(19, 6);
        builder.Property(e => e.VarianceQty).HasPrecision(19, 6);
        builder.HasIndex(e => new { e.StockTakeId, e.LineNo })
            .IsUnique()
            .HasDatabaseName("UQ_StockTakeLine_StockTakeId_LineNo");
    }
}

public class InventoryPeriodConfiguration : IEntityTypeConfiguration<InventoryPeriod>
{
    public void Configure(EntityTypeBuilder<InventoryPeriod> builder)
    {
        builder.ToTable("InventoryPeriod");
        InventoryEntityConfig.ConfigureCompanyScoped(builder);
        builder.Property(e => e.ClosedBy).HasMaxLength(50);
        builder.HasIndex(e => new { e.CompanyId, e.FiscalYear, e.FiscalMonth })
            .IsUnique()
            .HasDatabaseName("UQ_InventoryPeriod_Company_Year_Month");
    }
}

public class StockSnapshotConfiguration : IEntityTypeConfiguration<StockSnapshot>
{
    public void Configure(EntityTypeBuilder<StockSnapshot> builder)
    {
        builder.ToTable("StockSnapshot");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.ClosingQty).HasPrecision(19, 6);
        builder.Property(e => e.ClosingCost).HasPrecision(19, 6);
        builder.Property(e => e.ClosingValue).HasPrecision(19, 4);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.PeriodId, e.WarehouseId, e.ItemVariantId })
            .IsUnique()
            .HasDatabaseName("UQ_StockSnapshot_Grain");
        builder.HasOne<InventoryPeriod>()
            .WithMany()
            .HasForeignKey(e => e.PeriodId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockSnapshot_Period");
    }
}

public class LotConfiguration : IEntityTypeConfiguration<Lot>
{
    public void Configure(EntityTypeBuilder<Lot> builder)
    {
        builder.ToTable("Lot");
        InventoryEntityConfig.ConfigureCompanyScoped(builder);
        builder.Property(e => e.LotNo).HasMaxLength(50).IsRequired();
        builder.Property(e => e.SupplierRef).HasMaxLength(50);
        builder.Property(e => e.SourceDocType).HasMaxLength(10);
        builder.Property(e => e.SourceDocNo).HasMaxLength(30);
        builder.Property(e => e.CostCurrencyCode).HasMaxLength(3).HasDefaultValue("MYR");
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.ReceivedQty).HasPrecision(19, 6);
        builder.Property(e => e.ReceivedUnitCost).HasPrecision(19, 6);
        builder.HasIndex(e => new { e.CompanyId, e.ItemVariantId, e.LotNo })
            .IsUnique()
            .HasDatabaseName("UQ_Lot_Company_Variant_LotNo");
        builder.HasOne<ItemVariant>()
            .WithMany()
            .HasForeignKey(e => e.ItemVariantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Lot_ItemVariant");
    }
}

public class LotBalanceConfiguration : IEntityTypeConfiguration<LotBalance>
{
    public void Configure(EntityTypeBuilder<LotBalance> builder)
    {
        builder.ToTable("LotBalance");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.QtyOnHand).HasPrecision(19, 6);
        builder.HasIndex(e => new { e.CompanyId, e.BranchId, e.LotId, e.WarehouseId, e.LocationId })
            .IsUnique()
            .HasDatabaseName("UQ_LotBalance_Grain");
        builder.HasOne<Lot>()
            .WithMany()
            .HasForeignKey(e => e.LotId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_LotBalance_Lot");
    }
}

public class InventoryDocumentLineLotSplitConfiguration : IEntityTypeConfiguration<InventoryDocumentLineLotSplit>
{
    public void Configure(EntityTypeBuilder<InventoryDocumentLineLotSplit> builder)
    {
        builder.ToTable("InventoryDocumentLineLotSplit");
        InventoryEntityConfig.ConfigureBranchScoped(builder);
        builder.Property(e => e.LotNo).HasMaxLength(50);
        builder.Property(e => e.Qty).HasPrecision(19, 6);
        builder.HasIndex(e => new { e.DocumentLineId, e.LotId, e.LotNo })
            .HasDatabaseName("IX_InventoryDocumentLineLotSplit_Line");
    }
}
